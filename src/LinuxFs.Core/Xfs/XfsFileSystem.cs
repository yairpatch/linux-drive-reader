using System.Buffers.Binary;
using System.Text;
using LinuxFs.Core.Devices;

namespace LinuxFs.Core.Xfs;

/// <summary>Read-only XFS (on-disk versions 4 and 5). Everything on disk is big-endian.</summary>
public sealed class XfsFileSystem : IFileSystem
{
    internal readonly record struct Extent(long Offset, long Count, long DiskBlock, bool Unwritten);

    private const uint MagicDirBlock = 0x58443242, MagicDirData = 0x58443244, MagicDirBlock5 = 0x58444233, MagicDirData5 = 0x58444433;
    private const uint MagicBmap = 0x424D4150, MagicBmap5 = 0x424D4133, MagicSymlink5 = 0x58534C4D;
    private const int MaxDirectoryBlocks = 4_000_000;

    internal IBlockDevice Device { get; }
    internal int BlockSize { get; }
    private readonly int _inodeSize, _agBlkLog, _inoPbLog, _dirBlkLog;
    private readonly long _agBlocks, _agCount, _dataBlocks;
    private readonly bool _v5, _hasFtype;
    private readonly List<string> _warnings = new();

    public string TypeName => "xfs";
    public string Label { get; }
    public IReadOnlyList<string> Warnings => _warnings;
    public FsNode Root { get; }

    private readonly bool _logDirty;
    private readonly long _logStartBytes, _logBBs;

    /// <summary>How many log transactions were applied when opening; -1 if the log was not replayed.</summary>
    public int LogTransactionsApplied { get; } = -1;

    /// <summary>
    /// Opens the filesystem. If it was not cleanly unmounted and <paramref name="replayLog"/> is set, the
    /// committed changes waiting in its log are applied in memory first, as Linux would at mount time.
    /// </summary>
    public static XfsFileSystem Open(IBlockDevice device, bool replayLog = true)
    {
        var fs = new XfsFileSystem(device);
        if (!replayLog || !fs._logDirty) return fs;
        XfsLog.Result result;
        try { result = XfsLog.Recover(device, fs._logStartBytes, fs._logBBs, fs._v5, fs._inodeSize); }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException)
        {
            fs._warnings.Add("The changes waiting in its log could not be applied: " + ex.Message);
            return fs;
        }
        var replayed = new XfsFileSystem(new OverlayDevice(device, 512, result.Sectors), result.Transactions);
        replayed._warnings.AddRange(result.Problems);
        return replayed;
    }

    public XfsFileSystem(IBlockDevice device) : this(device, -1) { }

    private XfsFileSystem(IBlockDevice device, int logApplied)
    {
        Device = device;
        LogTransactionsApplied = logApplied;
        if (device.Length < 4096) throw new InvalidDataException("Volume is too small to hold an XFS filesystem.");
        var sb = new byte[512];
        device.Read(0, sb);
        if (!sb.AsSpan(0, 4).SequenceEqual("XFSB"u8)) throw new InvalidDataException("Not an XFS filesystem.");

        BlockSize = (int)U32(sb, 4);
        _dataBlocks = (long)U64(sb, 8);
        long logStart = (long)U64(sb, 48);
        ulong rootInode = U64(sb, 56);
        _agBlocks = U32(sb, 84);
        _agCount = U32(sb, 88);
        long logBlocks = U32(sb, 96);
        int version = U16(sb, 100);
        _inodeSize = U16(sb, 104);
        _agBlkLog = sb[124];
        _inoPbLog = sb[123];
        _dirBlkLog = sb[192];
        uint features2 = U32(sb, 200);
        Label = ReadText(sb.AsSpan(108, 12));

        if (BlockSize < 512 || BlockSize > 65536 || (BlockSize & (BlockSize - 1)) != 0 || _inodeSize < 256 || _inodeSize > BlockSize ||
            _agBlocks == 0 || _agCount == 0 || _agBlkLog > 32 || _inoPbLog > 16 || _dirBlkLog > 8 || (long)BlockSize << _dirBlkLog > 65536 || _dataBlocks <= 0)
            throw new InvalidDataException("The XFS superblock is damaged.");
        if (sb[126] != 0) throw new InvalidDataException("This XFS filesystem was never finished being created (mkfs was interrupted).");

        int major = version & 0xF;
        if (major != 4 && major != 5) throw new NotSupportedException($"XFS on-disk version {major} is too old to be supported.");
        _v5 = major == 5;
        if (!_v5 && (version & 0x2000) == 0) throw new NotSupportedException("XFS with version 1 directories is too old to be supported.");
        if (_v5)
        {
            uint incompat = U32(sb, 216);
            _hasFtype = (incompat & 0x1) != 0;
            const uint known = 0x1 | 0x2 | 0x4 | 0x8 | 0x10 | 0x20 | 0x40 | 0x80;
            if ((incompat & 0x10) != 0) _warnings.Add("This filesystem is flagged as needing repair (xfs_repair). Some content may be missing or shown incorrectly.");
            if ((incompat & ~known) != 0) _warnings.Add($"This filesystem uses features this app does not know (0x{incompat & ~known:X}). Some content may be shown incorrectly.");
        }
        else _hasFtype = (version & 0x8000) != 0 && (features2 & 0x200) != 0;

        if (logStart == 0)
        {
            if (logBlocks > 0 && logApplied < 0)
                _warnings.Add("This filesystem keeps its log on a separate device, so changes waiting there cannot be checked or applied.");
        }
        else
        {
            _logStartBytes = DiskBlock(logStart) * BlockSize;
            _logBBs = logBlocks * BlockSize / 512;
            _logDirty = logApplied < 0 && LogIsDirty(logStart, logBlocks);
            if (_logDirty)
                _warnings.Add("This filesystem was not cleanly unmounted. The most recent changes may be missing or shown incorrectly.");
        }

        Root = MakeNode("", rootInode);
        if (Root.Kind != FsNodeKind.Directory) throw new InvalidDataException("The root folder of this filesystem is damaged.");
    }

    // ------------------------------------------------------------- the log

    /// <summary>
    /// XFS writes metadata changes to a log first. A cleanly unmounted filesystem ends its log with an
    /// "unmount" record; anything else means changes are waiting in the log that this reader does not apply.
    /// </summary>
    private bool LogIsDirty(long logStart, long logBlocks)
    {
        try
        {
            long start = DiskBlock(logStart) * BlockSize, sectors = logBlocks * BlockSize / 512;
            if (sectors < 4) return false;
            var b = new byte[512];
            uint Cycle(long sector)
            {
                Device.Read(start + sector * 512, b.AsSpan(0, 8));
                return U32(b, 0) == 0xFEEDBABE ? U32(b, 4) : U32(b, 0);
            }
            // The newest part of the log carries the highest cycle number; the head is where it drops.
            uint first = Cycle(0);
            long head = sectors;
            if (Cycle(sectors - 1) != first)
            {
                long lo = 0, hi = sectors - 1;
                while (hi - lo > 1)
                {
                    long mid = (lo + hi) / 2;
                    if (Cycle(mid) == first) lo = mid; else hi = mid;
                }
                head = hi;
            }
            for (int back = 1; back <= 8192 && back <= sectors; back++)
            {
                long at = ((head - back) % sectors + sectors) % sectors;
                Device.Read(start + at * 512, b.AsSpan(0, 4));
                if (U32(b, 0) != 0xFEEDBABE) continue;
                Device.Read(start + at * 512, b);
                uint logVersion = U32(b, 8), operations = U32(b, 40), recordSize = U32(b, 320);
                long headerSectors = (logVersion & 2) != 0 && recordSize > 32768 ? (recordSize + 32767) / 32768 : 1;
                Device.Read(start + (at + headerSectors) % sectors * 512, b.AsSpan(0, 12));
                bool unmountRecord = operations == 1 && (b[9] & 0x20) != 0;
                return !unmountRecord;
            }
            return true; // no log record near the head at all
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException) { return false; }
    }

    // -------------------------------------------------------------- inodes

    private long DiskBlock(long fsBlock)
    {
        long ag = fsBlock >> _agBlkLog, block = fsBlock & ((1L << _agBlkLog) - 1);
        if (fsBlock < 0 || ag >= _agCount || block >= _agBlocks) throw new InvalidDataException("A block pointer points outside the filesystem (damaged filesystem).");
        long disk = ag * _agBlocks + block;
        if (disk >= _dataBlocks) throw new InvalidDataException("A block pointer points outside the filesystem (damaged filesystem).");
        return disk;
    }

    private sealed class Inode
    {
        public required byte[] Raw;
        public required int ForkStart, ForkSize;
        public ushort Mode => U16(Raw, 2);
        public int Format => Raw[5];
        public long Size => (long)U64(Raw, 56);
        public ushort Flags => U16(Raw, 90);
        public ulong Flags2 => Raw[4] >= 3 ? U64(Raw, 120) : 0;
        public long ExtentCount => (Flags2 & 0x10) != 0 ? (long)U64(Raw, 24) : U32(Raw, 76);
        public ReadOnlySpan<byte> Fork => Raw.AsSpan(ForkStart, ForkSize);
    }

    private Inode ReadInode(ulong number)
    {
        int shift = _agBlkLog + _inoPbLog;
        long ag = (long)(number >> shift);
        long block = (long)(number >> _inoPbLog) & ((1L << _agBlkLog) - 1);
        long index = (long)number & ((1L << _inoPbLog) - 1);
        if (number == 0 || ag >= _agCount || block >= _agBlocks) throw new InvalidDataException($"Invalid inode number {number} (damaged filesystem).");
        var raw = new byte[_inodeSize];
        Device.Read((ag * _agBlocks + block) * BlockSize + index * _inodeSize, raw);
        if (U16(raw, 0) != 0x494E) throw new InvalidDataException("An inode record is damaged.");
        int core = raw[4] >= 3 ? 176 : 100;
        int forkOffset = raw[82] * 8;
        int forkSize = forkOffset != 0 ? forkOffset : _inodeSize - core;
        if (core + forkSize > _inodeSize) throw new InvalidDataException("An inode record is damaged.");
        return new Inode { Raw = raw, ForkStart = core, ForkSize = forkSize };
    }

    private FsNode MakeNode(string name, ulong number)
    {
        try
        {
            var inode = ReadInode(number);
            uint mode = inode.Mode;
            var kind = (mode & 0xF000) switch { 0x4000 => FsNodeKind.Directory, 0x8000 => FsNodeKind.File, 0xA000 => FsNodeKind.Symlink, _ => FsNodeKind.Other };
            long seconds; uint nanos;
            if ((inode.Flags2 & 0x8) != 0)
            {
                // "bigtime": one 64-bit count of nanoseconds starting in December 1901.
                ulong t = U64(inode.Raw, 40);
                seconds = (long)(t / 1_000_000_000) - 2147483648L;
                nanos = (uint)(t % 1_000_000_000);
            }
            else { seconds = (int)U32(inode.Raw, 40); nanos = U32(inode.Raw, 44); }
            DateTime? modified;
            try { modified = DateTime.UnixEpoch.AddSeconds(seconds).AddTicks(nanos / 100); }
            catch (ArgumentOutOfRangeException) { modified = null; }

            string? target = null, error = null;
            if (kind == FsNodeKind.Symlink)
            {
                try { target = ReadLink(inode); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException) { error = ex.Message; }
            }
            return new FsNode
            {
                Name = name, Kind = kind, Id = (long)number,
                Size = kind is FsNodeKind.File or FsNodeKind.Symlink ? inode.Size : 0,
                ModifiedUtc = modified, Mode = mode, Uid = U32(inode.Raw, 8), Gid = U32(inode.Raw, 12), LinkTarget = target, Error = error,
            };
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
        {
            return new FsNode { Name = name, Kind = FsNodeKind.Other, Id = (long)number, Error = ex.Message };
        }
    }

    // ------------------------------------------------------------- extents

    private static Extent Unpack(ReadOnlySpan<byte> record, Func<long, long> toDisk)
    {
        ulong hi = U64(record, 0), lo = U64(record, 8);
        long offset = (long)((hi & 0x7FFFFFFFFFFFFFFF) >> 9);
        long start = (long)(((hi & 0x1FF) << 43) | (lo >> 21));
        long count = (long)(lo & 0x1FFFFF);
        return new Extent(offset, count, toDisk(start), hi >> 63 != 0);
    }

    private List<Extent> LoadExtents(Inode inode)
    {
        var list = new List<Extent>();
        var fork = inode.Fork;
        if (inode.Format == 2)
        {
            long n = inode.ExtentCount;
            if (n < 0 || n * 16 > fork.Length) throw new InvalidDataException("A file's extent list is damaged.");
            for (int i = 0; i < n; i++) list.Add(Unpack(fork.Slice(i * 16, 16), DiskBlock));
        }
        else if (inode.Format == 3)
        {
            // The root of the extent tree sits in the inode: keys first, then the same number of pointers.
            int level = U16(fork, 0), records = U16(fork, 2), max = (fork.Length - 4) / 16;
            if (level == 0 || level > 8 || records > max) throw new InvalidDataException("A file's extent tree is damaged.");
            for (int i = 0; i < records; i++) LoadTreeBlock((long)U64(fork, 4 + max * 8 + i * 8), level - 1, list);
        }
        else throw new InvalidDataException("This item's data is stored in a form that is not understood.");

        list.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        foreach (var e in list)
            if (e.Count <= 0 || e.DiskBlock + e.Count > _dataBlocks) throw new InvalidDataException("A file extent points outside the filesystem.");
        return list;
    }

    private void LoadTreeBlock(long fsBlock, int expectedLevel, List<Extent> list)
    {
        var block = new byte[BlockSize];
        Device.Read(DiskBlock(fsBlock) * BlockSize, block);
        uint magic = U32(block, 0);
        if (magic != MagicBmap && magic != MagicBmap5) throw new InvalidDataException("A file's extent tree is damaged.");
        int header = magic == MagicBmap5 ? 72 : 24;
        int level = U16(block, 4), records = U16(block, 6), max = (BlockSize - header) / 16;
        if (level != expectedLevel || records > max) throw new InvalidDataException("A file's extent tree is damaged.");
        if (list.Count > 50_000_000) throw new InvalidDataException("A file's extent tree is unreasonably large.");
        if (level == 0)
            for (int i = 0; i < records; i++) list.Add(Unpack(block.AsSpan(header + i * 16, 16), DiskBlock));
        else
            for (int i = 0; i < records; i++) LoadTreeBlock((long)U64(block, header + max * 8 + i * 8), level - 1, list);
    }

    internal static int Find(List<Extent> extents, long block)
    {
        int lo = 0, hi = extents.Count - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            if (extents[mid].Offset <= block) { found = mid; lo = mid + 1; } else hi = mid - 1;
        }
        return found;
    }

    /// <summary>Reads whole blocks at a logical position of a file or folder; false if any of them is a hole.</summary>
    private bool ReadLogical(List<Extent> extents, long block, Span<byte> buffer)
    {
        for (int done = 0; done < buffer.Length; done += BlockSize, block++)
        {
            int i = Find(extents, block);
            if (i < 0 || block >= extents[i].Offset + extents[i].Count || extents[i].Unwritten) return false;
            Device.Read((extents[i].DiskBlock + block - extents[i].Offset) * BlockSize, buffer.Slice(done, BlockSize));
        }
        return true;
    }

    // --------------------------------------------------------- directories

    public IReadOnlyList<FsNode> ListDirectory(FsNode directory)
    {
        var inode = ReadInode((ulong)directory.Id);
        if ((inode.Mode & 0xF000) != 0x4000) throw new InvalidDataException("Not a folder.");
        var entries = new List<(string Name, ulong Inode)>();

        if (inode.Format == 1)
        {
            // Small folders live entirely inside the inode.
            var d = inode.Fork;
            long size = inode.Size;
            if (size < 6 || size > d.Length) throw new InvalidDataException("A folder is damaged.");
            d = d[..(int)size];
            int count = d[0], wide = d[1] != 0 ? 8 : 4;
            int p = 2 + wide;
            for (int i = 0; i < count; i++)
            {
                if (p + 3 > d.Length) throw new InvalidDataException("A folder is damaged.");
                int nameLen = d[p];
                p += 3;
                int need = nameLen + (_hasFtype ? 1 : 0) + wide;
                if (nameLen == 0 || p + need > d.Length) throw new InvalidDataException("A folder is damaged.");
                string name = Encoding.UTF8.GetString(d.Slice(p, nameLen));
                p += nameLen + (_hasFtype ? 1 : 0);
                ulong number = wide == 8 ? U64(d, p) : U32(d, p);
                p += wide;
                entries.Add((name, number));
            }
        }
        else
        {
            var extents = LoadExtents(inode);
            int perDirBlock = 1 << _dirBlkLog, dirBlockSize = BlockSize << _dirBlkLog;
            long leafStart = (32L << 30) / BlockSize; // entries live below this offset; lookup indexes above it
            var buffer = new byte[dirBlockSize];
            long last = -1, visited = 0;
            foreach (var e in extents)
            {
                if (e.Offset >= leafStart) break;
                long end = Math.Min(e.Offset + e.Count, leafStart);
                for (long db = e.Offset / perDirBlock; db * perDirBlock < end; db++)
                {
                    if (db <= last) continue;
                    last = db;
                    if (++visited > MaxDirectoryBlocks) throw new InvalidDataException("Folder is unreasonably large (damaged filesystem).");
                    if (ReadLogical(extents, db * perDirBlock, buffer)) ParseDirBlock(buffer, entries);
                }
            }
        }

        var result = new List<FsNode>(entries.Count);
        foreach (var (name, number) in entries)
            if (name != "." && name != "..") result.Add(MakeNode(name, number));
        return result;
    }

    private void ParseDirBlock(byte[] b, List<(string, ulong)> entries)
    {
        uint magic = U32(b, 0);
        int p, end = b.Length;
        if (magic is MagicDirBlock5 or MagicDirData5) p = 64;
        else if (magic is MagicDirBlock or MagicDirData) p = 16;
        else throw new InvalidDataException("A folder block is damaged.");
        if (magic is MagicDirBlock or MagicDirBlock5)
        {
            // Single-block folders keep their lookup table at the end of the block.
            long leafCount = U32(b, b.Length - 8);
            long dataEnd = b.Length - 8 - leafCount * 8;
            if (dataEnd < p) throw new InvalidDataException("A folder block is damaged.");
            end = (int)dataEnd;
        }
        while (p + 8 <= end)
        {
            if (U16(b, p) == 0xFFFF)
            {
                int free = U16(b, p + 2);
                if (free < 8 || free % 8 != 0) break;
                p += free;
                continue;
            }
            int nameLen = b[p + 8];
            int size = (8 + 1 + nameLen + (_hasFtype ? 1 : 0) + 2 + 7) & ~7;
            if (nameLen == 0 || p + size > end) break;
            entries.Add((Encoding.UTF8.GetString(b, p + 9, nameLen), U64(b, p)));
            p += size;
        }
    }

    // ------------------------------------------------------ files and links

    private string ReadLink(Inode inode)
    {
        long size = inode.Size;
        if (size < 0 || size > 4096) throw new InvalidDataException("Link target is too long (damaged filesystem).");
        if (inode.Format == 1)
        {
            if (size > inode.ForkSize) throw new InvalidDataException("A link is damaged.");
            return Encoding.UTF8.GetString(inode.Fork[..(int)size]);
        }
        var data = new byte[size];
        int done = 0;
        var block = new byte[BlockSize];
        foreach (var e in LoadExtents(inode))
        {
            for (long i = 0; i < e.Count && done < size; i++)
            {
                Device.Read((e.DiskBlock + i) * BlockSize, block);
                int from = 0, have = BlockSize;
                if (_v5)
                {
                    if (U32(block, 0) != MagicSymlink5) throw new InvalidDataException("A link is damaged.");
                    from = 56;
                    have = (int)Math.Min(U32(block, 8), (uint)(BlockSize - 56));
                }
                int n = (int)Math.Min(have, size - done);
                block.AsSpan(from, n).CopyTo(data.AsSpan(done));
                done += n;
            }
        }
        if (done < size) throw new InvalidDataException("A link is damaged.");
        return Encoding.UTF8.GetString(data);
    }

    public Stream OpenFile(FsNode file)
    {
        var inode = ReadInode((ulong)file.Id);
        if ((inode.Mode & 0xF000) != 0x8000) throw new InvalidDataException("Not a regular file.");
        if ((inode.Flags & 0x1) != 0) throw new NotSupportedException("This file is stored on a separate XFS 'realtime' device, which is not supported.");
        return new XfsFileStream(this, LoadExtents(inode), inode.Size);
    }

    public void Dispose() { }

    internal static ushort U16(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt16BigEndian(b[o..]);
    internal static uint U32(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt32BigEndian(b[o..]);
    internal static ulong U64(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt64BigEndian(b[o..]);
    private static string ReadText(ReadOnlySpan<byte> b)
    {
        int n = b.IndexOf((byte)0);
        return Encoding.UTF8.GetString(n >= 0 ? b[..n] : b);
    }
}

internal sealed class XfsFileStream : Stream
{
    private readonly XfsFileSystem _fs;
    private readonly List<XfsFileSystem.Extent> _extents;
    private readonly long _size;
    private long _position;

    public XfsFileStream(XfsFileSystem fs, List<XfsFileSystem.Extent> extents, long size) { _fs = fs; _extents = extents; _size = size; }

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
            long block = _position / bs;
            int within = (int)(_position % bs);
            int i = XfsFileSystem.Find(_extents, block);
            long run; long disk = -1;
            if (i >= 0 && block < _extents[i].Offset + _extents[i].Count)
            {
                var e = _extents[i];
                run = e.Offset + e.Count - block;
                if (!e.Unwritten) disk = e.DiskBlock + (block - e.Offset);
            }
            else run = (i + 1 < _extents.Count ? _extents[i + 1].Offset : long.MaxValue) - block; // a hole

            run = Math.Clamp(run, 1, 1 << 18);
            int n = (int)Math.Min(Math.Min(run * bs - within, _size - _position), buffer.Length);
            if (disk < 0) buffer[..n].Clear();
            else _fs.Device.Read(disk * bs + within, buffer[..n]);
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
