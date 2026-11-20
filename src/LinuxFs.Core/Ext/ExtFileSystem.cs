using System.Buffers.Binary;
using System.Text;
using LinuxFs.Core.Devices;

namespace LinuxFs.Core.Ext;

/// <summary>Read-only ext2 / ext3 / ext4.</summary>
public sealed class ExtFileSystem : IFileSystem
{
    private const uint IncompatCompression = 0x1, IncompatFiletype = 0x2, IncompatRecover = 0x4, IncompatJournalDev = 0x8,
        IncompatMetaBg = 0x10, IncompatExtents = 0x40, Incompat64Bit = 0x80, IncompatMmp = 0x100, IncompatFlexBg = 0x200,
        IncompatEaInode = 0x400, IncompatDirdata = 0x1000, IncompatCsumSeed = 0x2000, IncompatLargedir = 0x4000,
        IncompatInlineData = 0x8000, IncompatEncrypt = 0x10000, IncompatCasefold = 0x20000;
    private const uint ReadableIncompat = IncompatFiletype | IncompatRecover | IncompatMetaBg | IncompatExtents | Incompat64Bit |
        IncompatMmp | IncompatFlexBg | IncompatEaInode | IncompatCsumSeed | IncompatLargedir | IncompatInlineData |
        IncompatEncrypt | IncompatCasefold;

    internal const uint FlagExtents = 0x80000, FlagInlineData = 0x10000000, FlagEncrypt = 0x800;
    private const int MaxDirectoryBytes = 512 * 1024 * 1024;

    internal IBlockDevice Device { get; }
    internal int BlockSize { get; }
    private readonly uint _inodesCount, _inodesPerGroup, _blocksPerGroup, _firstDataBlock, _incompat, _roCompat, _firstMetaBg;
    private readonly int _inodeSize, _descSize, _descPerBlock;
    private readonly long _blocksCount;
    private readonly long[] _inodeTable;
    private readonly List<string> _warnings = new();

    public string TypeName { get; }
    public string Label { get; }
    public IReadOnlyList<string> Warnings => _warnings;
    public FsNode Root { get; }

    public ExtFileSystem(IBlockDevice device)
    {
        Device = device;
        if (device.Length < 2048) throw new InvalidDataException("Volume is too small to hold an ext filesystem.");
        var sb = new byte[1024];
        device.Read(1024, sb);
        if (U16(sb, 0x38) != 0xEF53) throw new InvalidDataException("Not an ext2/3/4 filesystem.");

        uint logBlock = U32(sb, 0x18);
        if (logBlock > 6) throw new InvalidDataException("Invalid ext block size.");
        BlockSize = 1024 << (int)logBlock;
        _inodesCount = U32(sb, 0x00);
        _firstDataBlock = U32(sb, 0x14);
        _blocksPerGroup = U32(sb, 0x20);
        _inodesPerGroup = U32(sb, 0x28);
        uint rev = U32(sb, 0x4C);
        _inodeSize = rev >= 1 ? U16(sb, 0x58) : 128;
        uint compat = U32(sb, 0x5C);
        _incompat = U32(sb, 0x60);
        _roCompat = U32(sb, 0x64);
        _firstMetaBg = U32(sb, 0x104);
        bool is64 = (_incompat & Incompat64Bit) != 0;
        _descSize = 32;
        if (is64)
        {
            int ds = U16(sb, 0xFE);
            if (ds >= 32 && ds <= BlockSize && (ds & (ds - 1)) == 0) _descSize = ds;
        }
        _descPerBlock = BlockSize / _descSize;
        _blocksCount = U32(sb, 0x04) | (is64 ? (long)U32(sb, 0x150) << 32 : 0);

        if (_blocksPerGroup == 0 || _inodesPerGroup == 0 || _inodeSize < 128 || _inodeSize > BlockSize || _blocksCount <= _firstDataBlock)
            throw new InvalidDataException("The ext superblock is damaged.");
        if ((_incompat & IncompatJournalDev) != 0)
            throw new NotSupportedException("This volume is an external ext journal, not a filesystem.");
        if ((_incompat & IncompatCompression) != 0)
            throw new NotSupportedException("Compressed ext filesystems are not supported.");
        if ((_incompat & IncompatDirdata) != 0)
            throw new NotSupportedException("ext filesystems with the 'dirdata' feature are not supported.");

        uint unknown = _incompat & ~(ReadableIncompat | IncompatCompression | IncompatJournalDev | IncompatDirdata);
        if (unknown != 0)
            _warnings.Add($"This filesystem uses features this app does not know (0x{unknown:X}). Some content may be shown incorrectly.");
        if ((_incompat & IncompatRecover) != 0)
            _warnings.Add("This filesystem was not cleanly unmounted. The most recent changes may be missing or shown incorrectly.");

        Label = ReadText(sb.AsSpan(0x78, 16));
        const uint ext4Incompat = ReadableIncompat & ~(IncompatFiletype | IncompatRecover | IncompatMetaBg);
        const uint ext4Ro = 0x8 | 0x10 | 0x20 | 0x40 | 0x200 | 0x400;
        TypeName = (_incompat & ext4Incompat) != 0 || (_roCompat & ext4Ro) != 0 ? "ext4" : (compat & 0x4) != 0 ? "ext3" : "ext2";

        long groups = (_blocksCount - _firstDataBlock + _blocksPerGroup - 1) / _blocksPerGroup;
        if (groups <= 0 || groups > int.MaxValue / 8) throw new InvalidDataException("The ext superblock is damaged.");
        _inodeTable = new long[groups];
        var block = new byte[BlockSize];
        for (long g = 0; g < groups; g++)
        {
            int within = (int)(g % _descPerBlock);
            if (within == 0) device.Read(DescriptorBlock(g / _descPerBlock) * BlockSize, block);
            var d = block.AsSpan(within * _descSize, _descSize);
            long table = U32(d, 8);
            if (_descSize >= 64) table |= (long)U32(d, 0x28) << 32;
            _inodeTable[g] = table;
        }

        Root = MakeNode("", 2);
        if (Root.Kind != FsNodeKind.Directory) throw new InvalidDataException("The root folder of this filesystem is damaged.");
    }

    private long GroupFirstBlock(long group) => group * _blocksPerGroup + _firstDataBlock;

    private bool GroupHasSuperblock(long group)
    {
        if ((_roCompat & 0x1) == 0 || group <= 1) return true;
        if ((group & 1) == 0) return false;
        foreach (long b in new long[] { 3, 5, 7 })
        {
            long n = b;
            while (n < group) n *= b;
            if (n == group) return true;
        }
        return false;
    }

    private long DescriptorBlock(long index)
    {
        if ((_incompat & IncompatMetaBg) == 0 || index < _firstMetaBg) return _firstDataBlock + 1 + index;
        long group = (long)_descPerBlock * index;
        long skip = GroupHasSuperblock(group) ? 1 : 0;
        if (BlockSize == 1024 && index == 0 && _firstDataBlock == 0) skip++;
        return GroupFirstBlock(group) + skip;
    }

    internal void ReadBlock(long block, Span<byte> buffer)
    {
        if (block <= 0 || block >= _blocksCount) throw new InvalidDataException("A block pointer points outside the filesystem (damaged filesystem).");
        Device.Read(block * BlockSize, buffer);
    }

    internal ExtInode ReadInode(long number)
    {
        if (number <= 0 || number > _inodesCount) throw new InvalidDataException($"Invalid inode number {number} (damaged filesystem).");
        long group = (number - 1) / _inodesPerGroup, index = (number - 1) % _inodesPerGroup;
        if (group >= _inodeTable.Length || _inodeTable[group] <= 0 || _inodeTable[group] >= _blocksCount)
            throw new InvalidDataException("Inode table is damaged.");
        var raw = new byte[_inodeSize];
        Device.Read(_inodeTable[group] * BlockSize + index * _inodeSize, raw);
        return new ExtInode(number, raw, (_incompat & IncompatLargedir) != 0);
    }

    public IReadOnlyList<FsNode> ListDirectory(FsNode directory)
    {
        var inode = ReadInode(directory.Id);
        if (!inode.IsDirectory) throw new InvalidDataException("Not a folder.");
        if ((inode.Flags & FlagEncrypt) != 0) throw new NotSupportedException("This folder is encrypted (fscrypt) and cannot be read.");

        var result = new List<FsNode>();
        if ((inode.Flags & FlagInlineData) != 0)
        {
            // The first 4 bytes of the in-inode area hold the parent's inode number.
            ParseEntries(inode.Raw.AsSpan(0x28 + 4, 56), result);
            ParseEntries(inode.InlineXattrData(), result);
            return result;
        }

        long size = inode.Size;
        if (size > MaxDirectoryBytes) throw new InvalidDataException("Folder is unreasonably large (damaged filesystem).");
        var map = CreateMap(inode);
        var block = new byte[BlockSize];
        long blocks = (size + BlockSize - 1) / BlockSize;
        for (long lb = 0; lb < blocks; lb++)
        {
            var (phys, _) = map.Map(lb);
            if (phys == 0) continue;
            ReadBlock(phys, block);
            ParseEntries(block, result);
        }
        return result;
    }

    private void ParseEntries(ReadOnlySpan<byte> region, List<FsNode> result)
    {
        bool hasType = (_incompat & IncompatFiletype) != 0;
        int pos = 0;
        while (pos + 8 <= region.Length)
        {
            uint ino = U32(region, pos);
            int recLen = U16(region, pos + 4);
            if (recLen == 0 && region.Length == 65536) recLen = 65536;
            int nameLen = hasType ? region[pos + 6] : U16(region, pos + 6) & 0xFF;
            if (recLen < 8 || pos + recLen > region.Length) break;
            if (ino != 0 && nameLen > 0 && 8 + nameLen <= recLen)
            {
                string name = Encoding.UTF8.GetString(region.Slice(pos + 8, nameLen));
                if (name != "." && name != "..") result.Add(MakeNode(name, ino));
            }
            pos += recLen;
        }
    }

    private FsNode MakeNode(string name, long number)
    {
        ExtInode inode;
        try { inode = ReadInode(number); }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return new FsNode { Name = name, Kind = FsNodeKind.Other, Id = number, Error = ex.Message };
        }

        var kind = (inode.Mode & 0xF000) switch
        {
            0x4000 => FsNodeKind.Directory,
            0x8000 => FsNodeKind.File,
            0xA000 => FsNodeKind.Symlink,
            _ => FsNodeKind.Other,
        };
        string? target = null, error = null;
        if (kind == FsNodeKind.Symlink)
        {
            try { target = ReadLink(inode); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException) { error = ex.Message; }
        }
        return new FsNode
        {
            Name = name, Kind = kind, Id = number,
            Size = kind is FsNodeKind.File or FsNodeKind.Symlink ? inode.Size : 0,
            ModifiedUtc = inode.ModifiedUtc, Mode = inode.Mode, Uid = inode.Uid, Gid = inode.Gid,
            LinkTarget = target, Error = error,
        };
    }

    private string ReadLink(ExtInode inode)
    {
        long size = inode.Size;
        if (size < 0 || size > 4096) throw new InvalidDataException("Link target is too long (damaged filesystem).");
        if ((inode.Flags & FlagEncrypt) != 0) throw new NotSupportedException("Encrypted link.");
        byte[] data;
        if ((inode.Flags & FlagInlineData) != 0) data = inode.InlineData();
        else if (size < 60 && (inode.Flags & FlagExtents) == 0 && inode.DataSectors(BlockSize) == 0) data = inode.Raw.AsSpan(0x28, (int)size).ToArray();
        else
        {
            data = new byte[size];
            using var s = new ExtFileStream(this, inode, CreateMap(inode));
            s.ReadExactly(data);
        }
        int nul = Array.IndexOf(data, (byte)0);
        return Encoding.UTF8.GetString(data, 0, nul >= 0 ? nul : data.Length);
    }

    public Stream OpenFile(FsNode file)
    {
        var inode = ReadInode(file.Id);
        if ((inode.Mode & 0xF000) != 0x8000) throw new InvalidDataException("Not a regular file.");
        if ((inode.Flags & FlagEncrypt) != 0) throw new NotSupportedException("This file is encrypted (fscrypt) and cannot be read.");
        if ((inode.Flags & FlagInlineData) != 0) return new MemoryStream(inode.InlineData(), writable: false);
        return new ExtFileStream(this, inode, CreateMap(inode));
    }

    internal BlockMap CreateMap(ExtInode inode)
    {
        if ((inode.Flags & FlagExtents) == 0) return new IndirectMap(this, inode);
        var list = new List<(long Start, long Length, long Physical)>();
        LoadExtents(inode.Raw.AsSpan(0x28, 60), 0, list);
        list.Sort((a, b) => a.Start.CompareTo(b.Start));
        return new ExtentMap(list);
    }

    private void LoadExtents(ReadOnlySpan<byte> node, int level, List<(long, long, long)> list)
    {
        if (U16(node, 0) != 0xF30A) throw new InvalidDataException("File extent tree is damaged.");
        int entries = U16(node, 2), depth = U16(node, 6);
        if (level > 6 || depth > 6 || 12 + entries * 12 > node.Length) throw new InvalidDataException("File extent tree is damaged.");
        byte[]? child = null;
        for (int i = 0; i < entries; i++)
        {
            var e = node.Slice(12 + i * 12, 12);
            if (depth == 0)
            {
                long start = U32(e, 0);
                int len = U16(e, 4);
                long phys = ((long)U16(e, 6) << 32) | U32(e, 8);
                bool unwritten = len > 32768;
                if (unwritten) len -= 32768;
                if (len == 0) continue;
                if (!unwritten && (phys <= 0 || phys + len > _blocksCount)) throw new InvalidDataException("File extent points outside the filesystem.");
                if (list.Count > 50_000_000) throw new InvalidDataException("File extent tree is unreasonably large.");
                list.Add((start, len, unwritten ? 0 : phys)); // unwritten extents read as zeros
            }
            else
            {
                long block = ((long)U16(e, 8) << 32) | U32(e, 4);
                child ??= new byte[BlockSize];
                ReadBlock(block, child);
                LoadExtents(child, level + 1, list);
            }
        }
    }

    public void Dispose() { }

    internal static ushort U16(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt16LittleEndian(b[o..]);
    internal static uint U32(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b[o..]);
    private static string ReadText(ReadOnlySpan<byte> b)
    {
        int n = b.IndexOf((byte)0);
        return Encoding.UTF8.GetString(n >= 0 ? b[..n] : b);
    }
}

internal sealed class ExtInode
{
    public long Number { get; }
    public byte[] Raw { get; }
    private readonly bool _largeDir;

    public ExtInode(long number, byte[] raw, bool largeDir) { Number = number; Raw = raw; _largeDir = largeDir; }

    public ushort Mode => ExtFileSystem.U16(Raw, 0);
    public uint Flags => ExtFileSystem.U32(Raw, 0x20);
    public bool IsDirectory => (Mode & 0xF000) == 0x4000;
    public uint Uid => ExtFileSystem.U16(Raw, 2) | (uint)ExtFileSystem.U16(Raw, 0x78) << 16;
    public uint Gid => ExtFileSystem.U16(Raw, 0x18) | (uint)ExtFileSystem.U16(Raw, 0x7A) << 16;
    private int ExtraSize => Raw.Length > 130 ? ExtFileSystem.U16(Raw, 0x80) : 0;

    public long Size
    {
        get
        {
            long size = ExtFileSystem.U32(Raw, 4);
            bool regular = (Mode & 0xF000) == 0x8000;
            if (regular || (IsDirectory && _largeDir)) size |= (long)ExtFileSystem.U32(Raw, 0x6C) << 32;
            return size < 0 ? 0 : size;
        }
    }

    /// <summary>512-byte sectors used by file data, not counting an extended-attribute block.</summary>
    public long DataSectors(int blockSize)
    {
        long sectors = ExtFileSystem.U32(Raw, 0x1C);
        if (ExtFileSystem.U32(Raw, 0x68) != 0) sectors -= blockSize / 512;
        return Math.Max(0, sectors);
    }

    public DateTime? ModifiedUtc
    {
        get
        {
            long seconds = (int)ExtFileSystem.U32(Raw, 0x10);
            long ticks = 0;
            if (Raw.Length >= 0x8C && 128 + ExtraSize >= 0x8C)
            {
                uint extra = ExtFileSystem.U32(Raw, 0x88);
                seconds += (long)(extra & 3) << 32;
                ticks = (extra >> 2) / 100;
            }
            try { return DateTime.UnixEpoch.AddSeconds(seconds).AddTicks(ticks); }
            catch (ArgumentOutOfRangeException) { return null; }
        }
    }

    public uint BlockPointer(int index) => ExtFileSystem.U32(Raw, 0x28 + index * 4);

    /// <summary>The value of the in-inode "system.data" attribute: the overflow part of inline data.</summary>
    public ReadOnlySpan<byte> InlineXattrData()
    {
        int start = 128 + ExtraSize;
        if (Raw.Length <= 128 || start + 4 > Raw.Length || ExtFileSystem.U32(Raw, start) != 0xEA020000) return default;
        int first = start + 4, p = first;
        while (p + 16 <= Raw.Length && ExtFileSystem.U32(Raw, p) != 0)
        {
            int nameLen = Raw[p], index = Raw[p + 1];
            int valueOffset = ExtFileSystem.U16(Raw, p + 2);
            long valueSize = ExtFileSystem.U32(Raw, p + 8);
            if (p + 16 + nameLen > Raw.Length) break;
            if (index == 7 && nameLen == 4 && Raw.AsSpan(p + 16, 4).SequenceEqual("data"u8))
            {
                if (valueSize == 0) return default;
                if (first + valueOffset + valueSize > Raw.Length) throw new InvalidDataException("Inline file data is damaged.");
                return Raw.AsSpan(first + valueOffset, (int)valueSize);
            }
            p += (16 + nameLen + 3) & ~3;
        }
        return default;
    }

    /// <summary>The whole content of a file stored inside its inode.</summary>
    public byte[] InlineData()
    {
        long size = Size;
        var extra = InlineXattrData();
        if (size > 60 + extra.Length) throw new InvalidDataException("Inline file data is damaged.");
        var data = new byte[size];
        int head = (int)Math.Min(size, 60);
        Raw.AsSpan(0x28, head).CopyTo(data);
        if (size > 60) extra[..(int)(size - 60)].CopyTo(data.AsSpan(60));
        return data;
    }
}

/// <summary>Translates a file's block numbers into block numbers on the volume.</summary>
internal abstract class BlockMap
{
    /// <summary>Returns the physical block (0 = hole, reads as zeros) and how many blocks follow contiguously.</summary>
    public abstract (long Physical, long Count) Map(long logicalBlock);
}

internal sealed class ExtentMap : BlockMap
{
    private readonly List<(long Start, long Length, long Physical)> _extents;
    public ExtentMap(List<(long, long, long)> extents) => _extents = extents;

    public override (long, long) Map(long lb)
    {
        int lo = 0, hi = _extents.Count - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            if (_extents[mid].Start <= lb) { found = mid; lo = mid + 1; } else hi = mid - 1;
        }
        if (found >= 0)
        {
            var e = _extents[found];
            if (lb < e.Start + e.Length)
                return (e.Physical == 0 ? 0 : e.Physical + (lb - e.Start), e.Start + e.Length - lb);
        }
        long next = found + 1 < _extents.Count ? _extents[found + 1].Start : long.MaxValue;
        return (0, Math.Max(1, next - lb));
    }
}

/// <summary>The classic ext2/ext3 scheme: 12 direct pointers, then single, double and triple indirect blocks.</summary>
internal sealed class IndirectMap : BlockMap
{
    private readonly ExtFileSystem _fs;
    private readonly ExtInode _inode;
    private readonly byte[] _cache;
    private long _cachedBlock = -1;
    private readonly object _lock = new();

    public IndirectMap(ExtFileSystem fs, ExtInode inode) { _fs = fs; _inode = inode; _cache = new byte[fs.BlockSize]; }

    public override (long, long) Map(long lb)
    {
        long p = _fs.BlockSize / 4;
        lock (_lock)
        {
            if (lb < 12) return (_inode.BlockPointer((int)lb), 1);
            lb -= 12;
            if (lb < p) return (Pointer(_inode.BlockPointer(12), lb), 1);
            lb -= p;
            if (lb < p * p) return (Pointer(Pointer(_inode.BlockPointer(13), lb / p), lb % p), 1);
            lb -= p * p;
            if (lb / (p * p) >= p) return (0, 1);
            long l1 = Pointer(_inode.BlockPointer(14), lb / (p * p));
            return (Pointer(Pointer(l1, lb / p % p), lb % p), 1);
        }
    }

    private long Pointer(long block, long index)
    {
        if (block == 0) return 0;
        // Deeper levels evict each other from this one-block cache; the device cache below absorbs that.
        if (_cachedBlock != block) { _cachedBlock = -1; _fs.ReadBlock(block, _cache); _cachedBlock = block; }
        return ExtFileSystem.U32(_cache, (int)index * 4);
    }
}

internal sealed class ExtFileStream : Stream
{
    private readonly ExtFileSystem _fs;
    private readonly BlockMap _map;
    private readonly long _size;
    private long _position;

    public ExtFileStream(ExtFileSystem fs, ExtInode inode, BlockMap map) { _fs = fs; _map = map; _size = inode.Size; }

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
        int total = 0, bs = _fs.BlockSize;
        while (buffer.Length > 0 && _position < _size)
        {
            long lb = _position / bs;
            int within = (int)(_position % bs);
            var (phys, count) = _map.Map(lb);
            count = Math.Clamp(count, 1, 1 << 18);
            int n = (int)Math.Min(Math.Min(count * bs - within, _size - _position), buffer.Length);
            if (phys == 0) buffer[..n].Clear();
            else
            {
                if (phys + count > _fs.Device.Length / bs + 1) throw new InvalidDataException("File data lies outside the volume (damaged filesystem).");
                _fs.Device.Read(phys * bs + within, buffer[..n]);
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
