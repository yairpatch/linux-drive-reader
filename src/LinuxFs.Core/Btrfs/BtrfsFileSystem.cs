using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using LinuxFs.Core.Devices;

namespace LinuxFs.Core.Btrfs;

/// <summary>Read-only btrfs: subvolumes, snapshots, inline and compressed files. One disk only.</summary>
public sealed class BtrfsFileSystem : IFileSystem
{
    private const int HeaderSize = 101;
    private const byte TypeInodeItem = 1, TypeDirIndex = 96, TypeExtentData = 108, TypeRootItem = 132, TypeChunkItem = 228;
    private const ulong ProfileStriped = 0x8 | 0x40 | 0x80 | 0x100; // RAID0, RAID10, RAID5, RAID6
    private const long FsTreeId = 5;
    private const byte TypeRootRef = 156, TypeExtentCsum = 128;
    private const ulong CsumObjectId = unchecked((ulong)-10);
    private const long CsumTreeId = 7;
    private const long EmptyPlaceholder = -1;
    private const int MaxNodeCache = 512;

    internal readonly record struct Key(ulong ObjectId, byte Type, ulong Offset) : IComparable<Key>
    {
        public int CompareTo(Key o)
        {
            int c = ObjectId.CompareTo(o.ObjectId);
            if (c != 0) return c;
            c = Type.CompareTo(o.Type);
            return c != 0 ? c : Offset.CompareTo(o.Offset);
        }
    }

    private readonly record struct Item(Key Key, byte[] Node, int Offset, int Size)
    {
        public ReadOnlySpan<byte> Data => Node.AsSpan(Offset, Size);
    }

    private sealed record Chunk(long Logical, long Length, ulong Type, int StripeCount, List<long> OurStripes);

    private readonly IBlockDevice _dev;
    private readonly int _nodeSize, _sectorSize, _checksumType;
    private readonly ulong _deviceId;
    private readonly long _rootTree;
    private readonly List<Chunk> _chunks = new();
    private readonly Dictionary<long, (long Bytenr, long RootDir)> _trees = new();
    private readonly Dictionary<long, LinkedListNode<(long Logical, byte[] Data)>> _nodeMap = new();
    private readonly LinkedList<(long Logical, byte[] Data)> _nodeLru = new();
    private readonly object _lock = new();
    private readonly List<string> _warnings = new();

    public string TypeName => "btrfs";
    public string Label { get; }
    public IReadOnlyList<string> Warnings => _warnings;
    public FsNode Root { get; }

    public BtrfsFileSystem(IBlockDevice device)
    {
        _dev = device;
        byte[] sb = ReadSuperblock() ?? throw new InvalidDataException("No valid btrfs superblock was found.");

        _rootTree = (long)U64(sb, 0x50);
        long chunkRoot = (long)U64(sb, 0x58);
        ulong logRoot = U64(sb, 0x60);
        ulong deviceCount = U64(sb, 0x88);
        _sectorSize = (int)U32(sb, 0x90);
        _nodeSize = (int)U32(sb, 0x94);
        int sysChunkSize = (int)U32(sb, 0xA0);
        ulong incompat = U64(sb, 0xBC);
        _checksumType = U16(sb, 0xC4);
        _deviceId = U64(sb, 0xC9);
        Label = ReadText(sb.AsSpan(0x12B, 256));

        if (_nodeSize < 4096 || _nodeSize > 65536 || !BitOperations.IsPow2(_nodeSize) || _sectorSize < 512 || _sectorSize > 65536 || !BitOperations.IsPow2(_sectorSize) || sysChunkSize > 2048)
            throw new InvalidDataException("The btrfs superblock is damaged.");
        if ((incompat & 0x4000) != 0) throw new NotSupportedException("btrfs with the 'raid-stripe-tree' feature is not supported.");
        if ((incompat & 0x2000) != 0) throw new NotSupportedException("btrfs with the experimental 'extent-tree-v2' feature is not supported.");
        const ulong known = 0x1 | 0x2 | 0x4 | 0x8 | 0x10 | 0x20 | 0x40 | 0x80 | 0x100 | 0x200 | 0x400 | 0x800 | 0x1000 | 0x10000;
        if ((incompat & ~known) != 0)
            _warnings.Add($"This filesystem uses features this app does not know (0x{incompat & ~known:X}). Some content may be shown incorrectly.");
        if (deviceCount > 1)
            _warnings.Add($"This is one disk of a btrfs filesystem that spans {deviceCount} disks. Files stored on the other disks cannot be read.");
        if (logRoot != 0)
            _warnings.Add("This filesystem was not cleanly unmounted. The most recent changes may be missing.");

        // The superblock carries just enough of the chunk map to find the chunk tree, which holds the rest.
        int p = 0x32B, endOfArray = 0x32B + sysChunkSize;
        while (p + 17 + 48 <= endOfArray)
        {
            var key = ReadKey(sb, p);
            p += 17;
            int stripes = U16(sb, p + 44);
            int size = 48 + stripes * 32;
            if (key.Type != TypeChunkItem || stripes == 0 || p + size > endOfArray) throw new InvalidDataException("The btrfs system chunk list is damaged.");
            AddChunk((long)key.Offset, sb.AsSpan(p, size));
            p += size;
        }
        foreach (var item in Walk(chunkRoot, new Key(0, 0, 0)).ToList())
            if (item.Key.Type == TypeChunkItem) AddChunk((long)item.Key.Offset, item.Data);
        _chunks.Sort((a, b) => a.Logical.CompareTo(b.Logical));

        var top = Tree(FsTreeId);
        Root = MakeNode("", FsTreeId, top.RootDir);
        if (Root.Kind != FsNodeKind.Directory) throw new InvalidDataException("The root folder of this filesystem is damaged.");
    }

    private byte[]? ReadSuperblock()
    {
        byte[]? best = null;
        ulong bestGeneration = 0;
        foreach (long offset in new[] { 0x10000L, 0x4000000L, 0x4000000000L })
        {
            if (offset > _dev.Length - 4096) continue;
            var sb = new byte[4096];
            try { _dev.Read(offset, sb); } catch (IOException) { continue; }
            if (!sb.AsSpan(0x40, 8).SequenceEqual("_BHRfS_M"u8) || (long)U64(sb, 0x30) != offset) continue;
            int type = U16(sb, 0xC4);
            if (Checksums.Size(type) == 0) throw new NotSupportedException("This btrfs filesystem uses an unknown checksum type.");
            if (!Checksums.Matches(type, sb.AsSpan(32), sb)) continue;
            ulong generation = U64(sb, 0x48);
            if (best == null || generation > bestGeneration) { best = sb; bestGeneration = generation; }
        }
        return best;
    }

    private void AddChunk(long logical, ReadOnlySpan<byte> data)
    {
        if (data.Length < 48) throw new InvalidDataException("A btrfs chunk record is damaged.");
        int stripes = U16(data, 44);
        if (data.Length < 48 + stripes * 32) throw new InvalidDataException("A btrfs chunk record is damaged.");
        if (_chunks.Any(c => c.Logical == logical)) return;
        var ours = new List<long>();
        for (int i = 0; i < stripes; i++)
            if (U64(data, 48 + i * 32) == _deviceId) ours.Add((long)U64(data, 48 + i * 32 + 8));
        _chunks.Add(new Chunk(logical, (long)U64(data, 0), U64(data, 24), stripes, ours));
        _chunks.Sort((a, b) => a.Logical.CompareTo(b.Logical));
    }

    /// <summary>Translates a btrfs logical address into the places on this disk that hold it.</summary>
    private (List<long> Copies, long Available) Map(long logical)
    {
        int lo = 0, hi = _chunks.Count - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            if (_chunks[mid].Logical <= logical) { found = mid; lo = mid + 1; } else hi = mid - 1;
        }
        if (found < 0 || logical >= _chunks[found].Logical + _chunks[found].Length)
            throw new InvalidDataException("An address points outside the filesystem (damaged filesystem).");
        var c = _chunks[found];
        if ((c.Type & ProfileStriped) != 0 && c.StripeCount > 1)
            throw new NotSupportedException("This data is striped across several disks (RAID 0/10/5/6), which is not supported.");
        if (c.OurStripes.Count == 0)
            throw new NotSupportedException("This data is stored on another disk of this multi-disk btrfs filesystem.");
        long within = logical - c.Logical;
        return (c.OurStripes.Select(s => s + within).ToList(), c.Length - within);
    }

    /// <summary>
    /// Reads whole sectors of file data and checks each against its stored checksum, falling back to a
    /// second copy when there is one. Sectors without a checksum (files marked "nodatasum") are not checked.
    /// </summary>
    private void ReadData(long logical, Span<byte> buffer)
    {
        int s = _sectorSize, cs = Checksums.Size(_checksumType);
        bool aligned = logical % s == 0 && buffer.Length % s == 0;
        byte[]? sums = null;
        bool[]? has = null;
        if (aligned) sums = LoadChecksums(logical, buffer.Length / s, out has);

        long start = logical;
        while (buffer.Length > 0)
        {
            var (copies, available) = Map(logical);
            int n = (int)Math.Min(buffer.Length, available);
            _dev.Read(copies[0], buffer[..n]);
            if (sums != null)
            {
                for (int off = 0; off + s <= n; off += s)
                {
                    int k = (int)((logical + off - start) / s);
                    if (!has![k]) continue;
                    var expected = sums.AsSpan(k * cs, cs);
                    var sector = buffer.Slice(off, s);
                    if (Checksums.Matches(_checksumType, sector, expected)) continue;
                    bool repaired = false;
                    foreach (long other in copies.Skip(1))
                    {
                        try { _dev.Read(other + off, sector); } catch (IOException) { continue; }
                        if (Checksums.Matches(_checksumType, sector, expected)) { repaired = true; break; }
                    }
                    if (!repaired) throw new IOException("The content failed its integrity check: the data on the disk is corrupted.");
                }
            }
            buffer = buffer[n..];
            logical += n;
        }
    }

    private byte[]? LoadChecksums(long logical, int sectors, out bool[]? has)
    {
        has = null;
        long tree;
        try { tree = Tree(CsumTreeId).Bytenr; }
        catch (InvalidDataException) { return null; }

        int s = _sectorSize, cs = Checksums.Size(_checksumType);
        long end = logical + (long)sectors * s;
        var sums = new byte[sectors * cs];
        var present = new bool[sectors];
        void Apply(Item item)
        {
            long first = (long)item.Key.Offset;
            long from = Math.Max(first, logical), to = Math.Min(first + (long)(item.Size / cs) * s, end);
            for (long x = from; x < to; x += s)
            {
                int k = (int)((x - logical) / s);
                item.Data.Slice((int)((x - first) / s) * cs, cs).CopyTo(sums.AsSpan(k * cs));
                present[k] = true;
            }
        }
        // One checksum item covers a run of sectors, so the run containing our start may begin before it.
        if (FindAtOrBefore(tree, new Key(CsumObjectId, TypeExtentCsum, (ulong)logical)) is { } before
            && before.Key.ObjectId == CsumObjectId && before.Key.Type == TypeExtentCsum) Apply(before);
        foreach (var item in Walk(tree, new Key(CsumObjectId, TypeExtentCsum, (ulong)logical + 1)))
        {
            if (item.Key.ObjectId != CsumObjectId || item.Key.Type != TypeExtentCsum || (long)item.Key.Offset >= end) break;
            Apply(item);
        }
        has = present;
        return sums;
    }

    /// <summary>Finds the last item of a tree whose key is at or before <paramref name="target"/>.</summary>
    private Item? FindAtOrBefore(long bytenr, Key target)
    {
        for (int depth = 0; depth <= 8; depth++)
        {
            byte[] node = ReadNode(bytenr);
            int count = (int)U32(node, 0x60), level = node[0x64];
            int stride = level == 0 ? 25 : 33;
            if (count > (_nodeSize - HeaderSize) / stride) throw new InvalidDataException("A btrfs tree block is damaged.");
            int found = -1, lo = 0, hi = count - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >>> 1;
                if (ReadKey(node, HeaderSize + mid * stride).CompareTo(target) <= 0) { found = mid; lo = mid + 1; } else hi = mid - 1;
            }
            if (found < 0) return null;
            int at = HeaderSize + found * stride;
            if (level == 0)
            {
                long offset = HeaderSize + (long)U32(node, at + 17), size = U32(node, at + 21);
                if (offset + size > _nodeSize) throw new InvalidDataException("A btrfs tree block is damaged.");
                return new Item(ReadKey(node, at), node, (int)offset, (int)size);
            }
            bytenr = (long)U64(node, at + 17);
        }
        throw new InvalidDataException("A btrfs tree is too deep (damaged filesystem).");
    }

    private byte[] ReadNode(long logical)
    {
        lock (_lock)
        {
            if (_nodeMap.TryGetValue(logical, out var hit))
            {
                _nodeLru.Remove(hit); _nodeLru.AddFirst(hit);
                return hit.Value.Data;
            }
        }
        var (copies, available) = Map(logical);
        if (available < _nodeSize) throw new InvalidDataException("A btrfs tree block is damaged.");
        byte[]? good = null;
        foreach (long physical in copies)
        {
            var data = new byte[_nodeSize];
            try { _dev.Read(physical, data); } catch (IOException) { continue; }
            if ((long)U64(data, 0x30) != logical) continue;
            if (!Checksums.Matches(_checksumType, data.AsSpan(32), data)) continue;
            good = data;
            break;
        }
        if (good == null) throw new InvalidDataException("A btrfs tree block failed its integrity check (damaged filesystem).");
        lock (_lock)
        {
            if (!_nodeMap.ContainsKey(logical))
            {
                _nodeMap[logical] = _nodeLru.AddFirst((logical, good));
                if (_nodeMap.Count > MaxNodeCache)
                {
                    var last = _nodeLru.Last!;
                    _nodeLru.RemoveLast();
                    _nodeMap.Remove(last.Value.Logical);
                }
            }
        }
        return good;
    }

    /// <summary>Yields every item of a tree whose key is at or after <paramref name="from"/>, in key order.</summary>
    private IEnumerable<Item> Walk(long bytenr, Key from, int depth = 0)
    {
        if (depth > 8) throw new InvalidDataException("A btrfs tree is too deep (damaged filesystem).");
        byte[] node = ReadNode(bytenr);
        int count = (int)U32(node, 0x60), level = node[0x64];
        if (level == 0)
        {
            if (count > (_nodeSize - HeaderSize) / 25) throw new InvalidDataException("A btrfs tree block is damaged.");
            for (int i = 0; i < count; i++)
            {
                int at = HeaderSize + i * 25;
                var key = ReadKey(node, at);
                if (key.CompareTo(from) < 0) continue;
                long offset = HeaderSize + (long)U32(node, at + 17), size = U32(node, at + 21);
                if (offset + size > _nodeSize) throw new InvalidDataException("A btrfs tree block is damaged.");
                yield return new Item(key, node, (int)offset, (int)size);
            }
        }
        else
        {
            if (count == 0 || count > (_nodeSize - HeaderSize) / 33) throw new InvalidDataException("A btrfs tree block is damaged.");
            int start = 0, lo = 0, hi = count - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >>> 1;
                if (ReadKey(node, HeaderSize + mid * 33).CompareTo(from) <= 0) { start = mid; lo = mid + 1; } else hi = mid - 1;
            }
            for (int i = start; i < count; i++)
            {
                long child = (long)U64(node, HeaderSize + i * 33 + 17);
                foreach (var item in Walk(child, from, depth + 1)) yield return item;
            }
        }
    }

    private IEnumerable<Item> Items(long tree, ulong objectId, byte type)
    {
        foreach (var item in Walk(tree, new Key(objectId, type, 0)))
        {
            if (item.Key.ObjectId != objectId || item.Key.Type != type) yield break;
            yield return item;
        }
    }

    /// <summary>Finds a subvolume's tree and the inode number of its top folder.</summary>
    private (long Bytenr, long RootDir) Tree(long id)
    {
        lock (_lock) if (_trees.TryGetValue(id, out var known)) return known;
        var item = Items(_rootTree, (ulong)id, TypeRootItem).Cast<Item?>().FirstOrDefault()
            ?? throw new InvalidDataException($"Subvolume {id} is missing (damaged filesystem).");
        if (item.Size < 239) throw new InvalidDataException("A btrfs subvolume record is damaged.");
        var result = ((long)U64(item.Data, 176), (long)U64(item.Data, 168));
        if (result.Item2 == 0) result.Item2 = 256;
        lock (_lock) _trees[id] = result;
        return result;
    }

    private FsNode MakeNode(string name, long volume, long inode)
    {
        try
        {
            long tree = Tree(volume).Bytenr;
            var item = Items(tree, (ulong)inode, TypeInodeItem).Cast<Item?>().FirstOrDefault()
                ?? throw new InvalidDataException("The record for this item is missing (damaged filesystem).");
            if (item.Size < 160) throw new InvalidDataException("The record for this item is damaged.");
            var d = item.Data;
            uint mode = U32(d, 52);
            var kind = (mode & 0xF000) switch { 0x4000 => FsNodeKind.Directory, 0x8000 => FsNodeKind.File, 0xA000 => FsNodeKind.Symlink, _ => FsNodeKind.Other };
            DateTime? modified;
            try { modified = DateTime.UnixEpoch.AddSeconds((long)U64(d, 136)).AddTicks(U32(d, 144) / 100); }
            catch (ArgumentOutOfRangeException) { modified = null; }
            long size = (long)U64(d, 16);

            string? target = null, error = null;
            if (kind == FsNodeKind.Symlink)
            {
                try
                {
                    if (size > 4096) throw new InvalidDataException("Link target is too long (damaged filesystem).");
                    var bytes = new byte[size];
                    using var s = new BtrfsFileStream(this, LoadExtents(tree, inode), size);
                    s.ReadExactly(bytes);
                    target = Encoding.UTF8.GetString(bytes);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException) { error = ex.Message; }
            }
            return new FsNode
            {
                Name = name, Kind = kind, Id = inode, Volume = volume,
                Size = kind is FsNodeKind.File or FsNodeKind.Symlink ? size : 0,
                ModifiedUtc = modified, Mode = mode, Uid = U32(d, 44), Gid = U32(d, 48), LinkTarget = target, Error = error,
            };
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
        {
            return new FsNode { Name = name, Kind = FsNodeKind.Other, Id = inode, Volume = volume, Error = ex.Message };
        }
    }

    public IReadOnlyList<FsNode> ListDirectory(FsNode directory)
    {
        if (directory.Kind != FsNodeKind.Directory) throw new InvalidDataException("Not a folder.");
        if (directory.Volume == EmptyPlaceholder) return Array.Empty<FsNode>();
        long tree = Tree(directory.Volume).Bytenr;
        var entries = new List<(string Name, Key Location)>();
        foreach (var item in Items(tree, (ulong)directory.Id, TypeDirIndex))
        {
            var d = item.Data;
            int p = 0;
            while (p + 30 <= d.Length)
            {
                int dataLen = U16(d, p + 25), nameLen = U16(d, p + 27);
                if (p + 30 + nameLen + dataLen > d.Length) throw new InvalidDataException("A folder entry is damaged.");
                entries.Add((Encoding.UTF8.GetString(d.Slice(p + 30, nameLen)), ReadKey(d, p)));
                p += 30 + nameLen + dataLen;
            }
        }

        var result = new List<FsNode>(entries.Count);
        foreach (var (name, location) in entries)
        {
            if (location.Type == TypeRootItem)
            {
                // The entry is another subvolume or snapshot: continue in its own tree.
                long id = (long)location.ObjectId;
                try
                {
                    // A snapshot keeps the entry of a subvolume nested in its source, but not the subvolume
                    // itself. Linux shows such an entry as an empty folder; do the same.
                    bool linked = Walk(_rootTree, new Key((ulong)directory.Volume, TypeRootRef, (ulong)id))
                        .Take(1).Any(i => i.Key == new Key((ulong)directory.Volume, TypeRootRef, (ulong)id));
                    if (linked) result.Add(MakeNode(name, id, Tree(id).RootDir));
                    else result.Add(new FsNode { Name = name, Kind = FsNodeKind.Directory, Id = 2, Volume = EmptyPlaceholder, Mode = 0x4000 | 0x1ED, ModifiedUtc = directory.ModifiedUtc });
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
                {
                    result.Add(new FsNode { Name = name, Kind = FsNodeKind.Other, Id = 0, Volume = id, Error = ex.Message });
                }
            }
            else result.Add(MakeNode(name, directory.Volume, (long)location.ObjectId));
        }
        return result;
    }

    public Stream OpenFile(FsNode file)
    {
        if (file.Kind != FsNodeKind.File) throw new InvalidDataException("Not a regular file.");
        long tree = Tree(file.Volume).Bytenr;
        return new BtrfsFileStream(this, LoadExtents(tree, file.Id), file.Size);
    }

    internal sealed class Extent
    {
        public long FileOffset, Length, DiskBytenr, DiskBytes, ExtentOffset, RamBytes;
        public byte Compression;
        public bool Zero;
        public byte[]? Inline;
    }

    private List<Extent> LoadExtents(long tree, long inode)
    {
        var list = new List<Extent>();
        foreach (var item in Items(tree, (ulong)inode, TypeExtentData))
        {
            var d = item.Data;
            if (d.Length < 21) throw new InvalidDataException("A file extent record is damaged.");
            if (d[17] != 0) throw new NotSupportedException("This file is encrypted and cannot be read.");
            var e = new Extent { FileOffset = (long)item.Key.Offset, RamBytes = (long)U64(d, 8), Compression = d[16] };
            if (e.Compression > 3) throw new NotSupportedException("This file uses an unknown compression method.");
            byte type = d[20];
            if (type == 0)
            {
                e.Inline = d[21..].ToArray();
                e.Length = e.Compression == 0 ? e.Inline.Length : e.RamBytes;
            }
            else
            {
                if (d.Length < 53) throw new InvalidDataException("A file extent record is damaged.");
                e.DiskBytenr = (long)U64(d, 21);
                e.DiskBytes = (long)U64(d, 29);
                e.ExtentOffset = (long)U64(d, 37);
                e.Length = (long)U64(d, 45);
                e.Zero = type == 2 || e.DiskBytenr == 0; // preallocated or a hole
            }
            if (e.Length < 0 || e.RamBytes < 0 || e.ExtentOffset < 0) throw new InvalidDataException("A file extent record is damaged.");
            list.Add(e);
        }
        return list;
    }

    internal void ReadExtentData(Extent e, long offsetInExtent, Span<byte> buffer)
    {
        // Checksums cover whole sectors, so widen each read to sector boundaries.
        long start = e.DiskBytenr + e.ExtentOffset + offsetInExtent, s = _sectorSize;
        while (buffer.Length > 0)
        {
            int n = Math.Min(buffer.Length, 1 << 20);
            long from = start / s * s, to = (start + n + s - 1) / s * s;
            if (from == start && to == start + n) ReadData(start, buffer[..n]);
            else
            {
                var whole = new byte[to - from];
                ReadData(from, whole);
                whole.AsSpan((int)(start - from), n).CopyTo(buffer);
            }
            buffer = buffer[n..];
            start += n;
        }
    }

    /// <summary>Returns the full uncompressed content behind a compressed or inline extent.</summary>
    internal byte[] Decompress(Extent e)
    {
        byte[] source;
        if (e.Inline != null)
        {
            if (e.Compression == 0) return e.Inline;
            source = e.Inline;
        }
        else
        {
            if (e.DiskBytes <= 0 || e.DiskBytes > 64 * 1024 * 1024) throw new InvalidDataException("A compressed extent is damaged.");
            source = new byte[e.DiskBytes];
            ReadData(e.DiskBytenr, source);
        }
        if (e.RamBytes > 64 * 1024 * 1024) throw new InvalidDataException("A compressed extent is damaged.");
        var output = new byte[e.RamBytes];
        switch (e.Compression)
        {
            case 1:
                using (var z = new ZLibStream(new MemoryStream(source), CompressionMode.Decompress))
                    z.ReadAtLeast(output, output.Length, throwOnEndOfStream: false);
                break;
            case 2: Lzo.DecompressBtrfs(source, output, _sectorSize); break;
            case 3: Zstd.Decompress(source, source.Length, output); break;
        }
        return output;
    }

    public void Dispose() { }

    private static Key ReadKey(ReadOnlySpan<byte> b, int o) => new(U64(b, o), b[o + 8], U64(b, o + 9));
    private static ushort U16(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt16LittleEndian(b[o..]);
    private static uint U32(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b[o..]);
    private static ulong U64(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt64LittleEndian(b[o..]);
    private static string ReadText(ReadOnlySpan<byte> b)
    {
        int n = b.IndexOf((byte)0);
        return Encoding.UTF8.GetString(n >= 0 ? b[..n] : b);
    }
}

internal sealed class BtrfsFileStream : Stream
{
    private readonly BtrfsFileSystem _fs;
    private readonly List<BtrfsFileSystem.Extent> _extents;
    private readonly long _size;
    private long _position;
    private BtrfsFileSystem.Extent? _cachedExtent;
    private byte[]? _cachedData;

    public BtrfsFileStream(BtrfsFileSystem fs, List<BtrfsFileSystem.Extent> extents, long size) { _fs = fs; _extents = extents; _size = size; }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _size;
    public override long Position
    {
        get => _position;
        set { ArgumentOutOfRangeException.ThrowIfNegative(value); _position = value; }
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int total = 0;
        while (buffer.Length > 0 && _position < _size)
        {
            int lo = 0, hi = _extents.Count - 1, found = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >>> 1;
                if (_extents[mid].FileOffset <= _position) { found = mid; lo = mid + 1; } else hi = mid - 1;
            }
            var e = found >= 0 ? _extents[found] : null;
            long remaining = _size - _position;
            int n;
            if (e == null || _position >= e.FileOffset + e.Length)
            {
                // Between extents: a hole, which reads as zeros.
                long next = found + 1 < _extents.Count ? _extents[found + 1].FileOffset : _size;
                n = (int)Math.Min(Math.Min(Math.Max(next - _position, 1), remaining), buffer.Length);
                buffer[..n].Clear();
            }
            else
            {
                long within = _position - e.FileOffset;
                n = (int)Math.Min(Math.Min(e.Length - within, remaining), buffer.Length);
                if (e.Zero) buffer[..n].Clear();
                else if (e.Inline == null && e.Compression == 0) _fs.ReadExtentData(e, within, buffer[..n]);
                else
                {
                    if (_cachedExtent != e) { _cachedData = _fs.Decompress(e); _cachedExtent = e; }
                    long from = (e.Inline != null ? 0 : e.ExtentOffset) + within;
                    int have = (int)Math.Clamp(_cachedData!.Length - from, 0, n);
                    _cachedData.AsSpan((int)Math.Min(from, _cachedData.Length), have).CopyTo(buffer);
                    buffer[have..n].Clear();
                }
            }
            buffer = buffer[n..];
            _position += n;
            total += n;
        }
        return total;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long target = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => _position + offset, _ => _size + offset };
        if (target < 0) throw new IOException("Cannot seek before the start of the file.");
        return _position = target;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
