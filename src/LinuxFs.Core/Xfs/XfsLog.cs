using System.Buffers.Binary;
using LinuxFs.Core.Devices;

namespace LinuxFs.Core.Xfs;

/// <summary>
/// XFS log recovery, computed in memory. Finds the committed transactions between the log's tail and head
/// and applies their logged metadata buffers and inodes the way the Linux kernel does at mount time,
/// including buffer cancellation and the rule that metadata already newer on disk is left alone.
/// </summary>
internal sealed class XfsLog
{
    public sealed class Result
    {
        /// <summary>512-byte sector number (from the start of the filesystem) to its content after recovery.</summary>
        public Dictionary<long, byte[]> Sectors { get; } = new();
        public int Transactions { get; set; }
        public List<string> Problems { get; } = new();
    }

    private const int BB = 512, CycleChunk = 32768, ChunksPerHeader = 64;
    private const uint HeaderMagic = 0xFEEDBABE;
    private const byte ClientTransaction = 0x69, ClientLog = 0xAA;
    private const byte FlagStart = 0x01, FlagCommit = 0x02, FlagContinue = 0x04, FlagWasContinued = 0x08, FlagEnd = 0x10, FlagUnmount = 0x20;
    private const ushort ItemBuf = 0x123C, ItemInode = 0x123B, ItemBui = 0x1244, ItemBud = 0x1245, ItemXmi = 0x1248, ItemXmd = 0x1249;
    private const ushort BlfInodeBuf = 0x1, BlfCancel = 0x2, BlfDquot = 0x4 | 0x8 | 0x10;
    private const uint IlogCore = 0x1, IlogDData = 0x2, IlogDExt = 0x4, IlogDBroot = 0x8, IlogAData = 0x40, IlogAExt = 0x80, IlogABroot = 0x100;

    private sealed class Item { public List<byte[]> Regions = new(); public int Total; }
    private sealed class Transaction { public long Lsn; public byte[] Header = Array.Empty<byte>(); public List<Item> Items = new(); }

    private readonly IBlockDevice _dev;
    private readonly long _logStart, _logBBs;
    private readonly bool _v5;
    private readonly int _inodeSize;
    private readonly Result _result = new();

    private XfsLog(IBlockDevice dev, long logStart, long logBBs, bool v5, int inodeSize)
    { _dev = dev; _logStart = logStart; _logBBs = logBBs; _v5 = v5; _inodeSize = inodeSize; }

    public static Result Recover(IBlockDevice dev, long logStartBytes, long logBBs, bool v5, int inodeSize)
    {
        var log = new XfsLog(dev, logStartBytes, logBBs, v5, inodeSize);
        log.Run();
        return log._result;
    }

    // ------------------------------------------------------------ reading the log

    private byte[] ReadBB(long n)
    {
        var b = new byte[BB];
        _dev.Read(_logStart + ((n % _logBBs) + _logBBs) % _logBBs * BB, b);
        return b;
    }

    private uint CycleOf(long n)
    {
        var b = ReadBB(n);
        return BE32(b, 0) == HeaderMagic ? BE32(b, 4) : BE32(b, 0);
    }

    /// <summary>The block after the newest one written: where the cycle number written into every block drops.</summary>
    private long FindHead()
    {
        uint first = CycleOf(0);
        if (CycleOf(_logBBs - 1) == first) return 0;
        long lo = 0, hi = _logBBs - 1;
        while (hi - lo > 1)
        {
            long mid = (lo + hi) / 2;
            if (CycleOf(mid) == first) lo = mid; else hi = mid;
        }
        return hi;
    }

    private void Run()
    {
        long head = FindHead();
        // The newest record header before the head says where the log's tail is.
        long last = -1;
        byte[]? lastHeader = null;
        for (long back = 1; back <= Math.Min(_logBBs, 4096); back++)
        {
            var b = ReadBB(head - back);
            if (BE32(b, 0) == HeaderMagic) { last = ((head - back) % _logBBs + _logBBs) % _logBBs; lastHeader = b; break; }
        }
        if (lastHeader == null) throw new InvalidDataException("No log record was found near the end of the log.");
        long tail = (long)(BE64(lastHeader, 24) & 0xFFFFFFFF);
        if (tail >= _logBBs) throw new InvalidDataException("The log's tail pointer is damaged.");

        var open = new Dictionary<uint, Transaction>();
        var committed = new List<Transaction>();
        long pos = tail;
        for (int guard = 0; guard < 1_000_000; guard++)
        {
            if (pos == head && guard > 0) break;
            if (pos == head && tail == head) break;
            var h = ReadBB(pos);
            if (BE32(h, 0) != HeaderMagic) break;
            uint cycle = BE32(h, 4);
            int version = (int)BE32(h, 8), length = (int)BE32(h, 12), ops = (int)BE32(h, 40);
            long lsn = (long)BE64(h, 16);
            int size = version >= 2 ? (int)BE32(h, 320) : CycleChunk;
            int headerBlocks = version >= 2 && size > CycleChunk ? (size + CycleChunk - 1) / CycleChunk : 1;
            int dataBlocks = (length + BB - 1) / BB;
            if (length < 0 || dataBlocks > 2048 || headerBlocks > 8) break;

            var headers = new List<byte[]> { h };
            for (int i = 1; i < headerBlocks; i++) headers.Add(ReadBB(pos + i));
            var data = new byte[dataBlocks * BB];
            bool torn = false;
            for (int i = 0; i < dataBlocks; i++)
            {
                long at = pos + headerBlocks + i;
                var block = ReadBB(at);
                // Blocks written after the log wrapped carry the next cycle number.
                uint expected = at >= _logBBs ? NextCycle(cycle) : cycle;
                if (BE32(block, 0) != expected) { torn = true; break; }
                int chunk = i / ChunksPerHeader, slot = i % ChunksPerHeader;
                var source = headers[chunk];
                // The first word of every block was replaced by the cycle number; the original is kept in the header.
                Buffer.BlockCopy(source, chunk == 0 ? 44 + slot * 4 : 4 + slot * 4, block, 0, 4);
                Buffer.BlockCopy(block, 0, data, i * BB, BB);
            }
            if (torn) break; // a record that was only partly written before the crash: the log ends here

            ParseRecord(data, length, ops, lsn, open, committed);
            pos = (pos + headerBlocks + dataBlocks) % _logBBs;
        }

        Apply(committed);
        _result.Transactions = committed.Count;
    }

    private static uint NextCycle(uint c) { c++; return c == HeaderMagic ? c + 1 : c; }

    private void ParseRecord(byte[] data, int length, int ops, long lsn, Dictionary<uint, Transaction> open, List<Transaction> committed)
    {
        int p = 0;
        for (int n = 0; n < ops && p + 12 <= length; n++)
        {
            uint tid = BE32(data, p);
            int len = (int)BE32(data, p + 4);
            byte client = data[p + 8], flags = data[p + 9];
            p += 12;
            if (len < 0 || p + len > data.Length) break;
            var payload = data.AsSpan(p, len).ToArray();
            p += len;
            if (client == ClientLog) continue; // the unmount record
            if (client != ClientTransaction) continue;

            if (!open.TryGetValue(tid, out var t))
            {
                if ((flags & FlagStart) != 0) open[tid] = new Transaction { Lsn = lsn };
                continue; // otherwise the transaction began before the tail and is already on disk
            }
            int f = flags & ~FlagEnd;
            if ((f & FlagWasContinued) != 0) f &= ~FlagContinue;
            switch (f)
            {
                case 0:
                case FlagContinue:
                    AddRegion(t, payload);
                    break;
                case FlagWasContinued:
                    if (t.Items.Count == 0) t.Header = Concat(t.Header, payload);
                    else { var item = t.Items[^1]; item.Regions[^1] = Concat(item.Regions[^1], payload); }
                    break;
                case FlagCommit:
                    committed.Add(t);
                    open.Remove(tid);
                    break;
            }
        }
    }

    private static void AddRegion(Transaction t, byte[] data)
    {
        if (data.Length == 0) return;
        if (t.Items.Count == 0 && t.Header.Length < 16) { t.Header = Concat(t.Header, data); return; } // the transaction header
        var item = t.Items.Count > 0 ? t.Items[^1] : null;
        if (item == null || (item.Total != 0 && item.Regions.Count == item.Total)) { item = new Item(); t.Items.Add(item); }
        if (item.Total == 0)
        {
            if (data.Length < 4) return;
            bool little = IsLittle(data);
            item.Total = little ? BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2)) : BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(2));
            if (item.Total is <= 0 or > 1000) { item.Total = 1; }
        }
        item.Regions.Add(data);
    }

    /// <summary>Log items are written in the byte order of the computer that wrote them; their type numbers start with 0x12.</summary>
    private static bool IsLittle(ReadOnlySpan<byte> item) => item.Length >= 2 && item[1] == 0x12;

    private static byte[] Concat(byte[] a, byte[] b)
    {
        var c = new byte[a.Length + b.Length];
        a.CopyTo(c, 0); b.CopyTo(c, a.Length);
        return c;
    }

    // ------------------------------------------------------------ applying

    private static ushort Type(Item i) => i.Regions.Count == 0 || i.Regions[0].Length < 2 ? (ushort)0
        : IsLittle(i.Regions[0]) ? BinaryPrimitives.ReadUInt16LittleEndian(i.Regions[0]) : BinaryPrimitives.ReadUInt16BigEndian(i.Regions[0]);

    private static (long Blkno, int Len, ushort Flags) BufInfo(byte[] f)
    {
        bool le = IsLittle(f);
        ushort flags = le ? BinaryPrimitives.ReadUInt16LittleEndian(f.AsSpan(4)) : BinaryPrimitives.ReadUInt16BigEndian(f.AsSpan(4));
        int len = le ? BinaryPrimitives.ReadUInt16LittleEndian(f.AsSpan(6)) : BinaryPrimitives.ReadUInt16BigEndian(f.AsSpan(6));
        long blkno = le ? BinaryPrimitives.ReadInt64LittleEndian(f.AsSpan(8)) : BinaryPrimitives.ReadInt64BigEndian(f.AsSpan(8));
        return (blkno, len, flags);
    }

    private void Apply(List<Transaction> committed)
    {
        // First pass: count how often each buffer is cancelled (freed) anywhere in the log.
        var cancels = new Dictionary<(long, int), int>();
        foreach (var t in committed)
            foreach (var item in t.Items)
                if (Type(item) == ItemBuf && item.Regions[0].Length >= 24 && (BufInfo(item.Regions[0]).Flags & BlfCancel) != 0)
                {
                    var (blkno, len, _) = BufInfo(item.Regions[0]);
                    cancels[(blkno, len)] = cancels.GetValueOrDefault((blkno, len)) + 1;
                }

        var intents = new HashSet<(ushort, ulong)>();
        foreach (var t in committed)
        {
            // The kernel's order within a transaction: buffers, then inodes and the rest, then inode buffers, then cancellations.
            var ordered = t.Items.Where(i => Type(i) == ItemBuf && !IsSpecialBuf(i))
                .Concat(t.Items.Where(i => Type(i) != ItemBuf))
                .Concat(t.Items.Where(i => Type(i) == ItemBuf && IsSpecialBuf(i) && (BufInfo(i.Regions[0]).Flags & BlfCancel) == 0))
                .Concat(t.Items.Where(i => Type(i) == ItemBuf && (BufInfo(i.Regions[0]).Flags & BlfCancel) != 0));
            foreach (var item in ordered)
            {
                try
                {
                    switch (Type(item))
                    {
                        case ItemBuf: ApplyBuffer(item, t.Lsn, cancels); break;
                        case ItemInode: ApplyInode(item, t.Lsn); break;
                        case ItemBui: case ItemXmi: intents.Add((Type(item), IntentId(item))); break;
                        case ItemBud: intents.Remove((ItemBui, IntentId(item))); break;
                        case ItemXmd: intents.Remove((ItemXmi, IntentId(item))); break;
                    }
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
                {
                    _result.Problems.Add("Part of the log could not be applied: " + ex.Message);
                }
            }
        }
        if (intents.Count > 0)
            _result.Problems.Add("Some file changes that were in progress when Linux stopped were not finished. A few files may show their previous contents.");
    }

    private static bool IsSpecialBuf(Item i) => i.Regions[0].Length >= 24 && (BufInfo(i.Regions[0]).Flags & (BlfCancel | BlfInodeBuf)) != 0;

    private static ulong IntentId(Item i)
    {
        var f = i.Regions[0];
        if (f.Length < 16) return 0;
        return IsLittle(f) ? BinaryPrimitives.ReadUInt64LittleEndian(f.AsSpan(8)) : BinaryPrimitives.ReadUInt64BigEndian(f.AsSpan(8));
    }

    private byte[] ReadCurrent(long sector, int count)
    {
        var b = new byte[count * BB];
        _dev.Read(sector * BB, b);
        for (int i = 0; i < count; i++)
            if (_result.Sectors.TryGetValue(sector + i, out var s)) s.CopyTo(b, i * BB);
        return b;
    }

    private void Store(long sector, byte[] content)
    {
        for (int i = 0; i * BB < content.Length; i++) _result.Sectors[sector + i] = content.AsSpan(i * BB, BB).ToArray();
    }

    private void ApplyBuffer(Item item, long lsn, Dictionary<(long, int), int> cancels)
    {
        var f = item.Regions[0];
        if (f.Length < 24) return;
        var (blkno, len, flags) = BufInfo(f);
        var key = (blkno, len);
        if ((flags & BlfCancel) != 0)
        {
            if (cancels.TryGetValue(key, out int n)) { if (n <= 1) cancels.Remove(key); else cancels[key] = n - 1; }
            return;
        }
        if (cancels.ContainsKey(key)) return;              // freed again later in the log
        if ((flags & (BlfInodeBuf | BlfDquot)) != 0) return; // only unlinked-list pointers or quota: not needed to read files
        if (len <= 0 || blkno < 0 || (blkno + len) * BB > _dev.Length) throw new InvalidDataException("A logged buffer lies outside the filesystem.");

        if (_v5)
        {
            var original = new byte[BB];
            _dev.Read(blkno * BB, original);
            long onDisk = MetadataLsn(original);
            if (onDisk > 0 && onDisk != -1 && onDisk >= lsn) return; // the disk already has a newer version
        }

        bool le = IsLittle(f);
        int mapWords = (int)(le ? BinaryPrimitives.ReadUInt32LittleEndian(f.AsSpan(16)) : BinaryPrimitives.ReadUInt32BigEndian(f.AsSpan(16)));
        mapWords = Math.Clamp(mapWords, 0, (f.Length - 20) / 4);
        bool Bit(int n) => n / 32 < mapWords && ((le ? BinaryPrimitives.ReadUInt32LittleEndian(f.AsSpan(20 + n / 32 * 4)) : BinaryPrimitives.ReadUInt32BigEndian(f.AsSpan(20 + n / 32 * 4))) & (1u << (n % 32))) != 0;

        var buffer = ReadCurrent(blkno, len);
        int bit = 0, chunks = mapWords * 32;
        for (int r = 1; r < item.Regions.Count; r++)
        {
            while (bit < chunks && !Bit(bit)) bit++;
            if (bit >= chunks) break;
            int run = 0;
            while (bit + run < chunks && Bit(bit + run)) run++;
            int offset = bit * 128, n = Math.Min(Math.Min(run * 128, item.Regions[r].Length), buffer.Length - offset);
            if (n > 0) Buffer.BlockCopy(item.Regions[r], 0, buffer, offset, n);
            bit += run;
        }
        Store(blkno, buffer);
    }

    /// <summary>The log sequence number stamped in a version 5 metadata block, or 0 if the block type is not one that has it.</summary>
    private static long MetadataLsn(byte[] b)
    {
        long At(int o) => (long)BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(o));
        switch (BE32(b, 0))
        {
            case 0x58444233: case 0x58444433: case 0x58444633: return At(16);  // directory data and free-index blocks
            case 0x41423342: case 0x41423343: case 0x49414233: case 0x46494233: case 0x52334643: case 0x524D4233: return At(24); // short-form btrees
            case 0x424D4133: return At(32);   // file mapping btree
            case 0x58534C4D: return At(32);   // symlink block
            case 0x58414746: return At(208);  // AG free-space header
            case 0x58414749: return At(320);  // AG inode header
            case 0x5841464C: return At(24);   // AG free list
            case 0x58465342: return At(240);  // superblock
        }
        switch (BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(8)))
        {
            case 0x3DF1: case 0x3DFF: case 0x3EBE: case 0x3BEE: return At(24); // directory and attribute btree blocks
        }
        if (BinaryPrimitives.ReadUInt16BigEndian(b) == 0x494E && b[4] >= 3) return At(112); // inode cluster
        return 0;
    }

    private void ApplyInode(Item item, long lsn)
    {
        var f = item.Regions[0];
        if (f.Length < 52 || item.Regions.Count < 2) return;
        bool le = IsLittle(f);
        uint U32(ReadOnlySpan<byte> s, int o) => le ? BinaryPrimitives.ReadUInt32LittleEndian(s[o..]) : BinaryPrimitives.ReadUInt32BigEndian(s[o..]);
        ushort U16(ReadOnlySpan<byte> s, int o) => le ? BinaryPrimitives.ReadUInt16LittleEndian(s[o..]) : BinaryPrimitives.ReadUInt16BigEndian(s[o..]);
        long I64(ReadOnlySpan<byte> s, int o) => le ? BinaryPrimitives.ReadInt64LittleEndian(s[o..]) : BinaryPrimitives.ReadInt64BigEndian(s[o..]);

        uint fields = U32(f, 4);
        int asize = U16(f, 8), dsize = U16(f, 10);
        bool wide = f.Length >= 56; // the 64-bit layout pads before ilf_ino
        long blkno = I64(f, wide ? 40 : 36);
        int len = (int)U32(f, wide ? 48 : 44), boffset = (int)U32(f, wide ? 52 : 48);
        if (len <= 0 || blkno < 0 || boffset < 0 || boffset + _inodeSize > len * BB || (blkno + len) * BB > _dev.Length)
            throw new InvalidDataException("A logged inode lies outside the filesystem.");

        var core = item.Regions[1];
        if (core.Length < 96 || U16(core, 0) != 0x494E) throw new InvalidDataException("A logged inode is damaged.");
        int version = core[4];

        var original = new byte[_inodeSize];
        _dev.Read(blkno * BB + boffset, original);
        bool diskValid = BinaryPrimitives.ReadUInt16BigEndian(original) == 0x494E;
        if (diskValid && version >= 3 && original[4] >= 3)
        {
            long onDisk = BinaryPrimitives.ReadInt64BigEndian(original.AsSpan(112));
            if (onDisk > 0 && onDisk != -1 && onDisk >= lsn) return;
        }
        else if (diskValid && version < 3 && BinaryPrimitives.ReadUInt16BigEndian(original.AsSpan(30)) > U16(core, 30)) return;

        var cluster = ReadCurrent(blkno, len);
        var dinode = cluster.AsSpan(boffset, _inodeSize);
        int coreSize = version >= 3 ? 176 : 100;
        WriteCore(core, cluster, boffset, le, version); // the core is always logged along with an inode

        int literal = _inodeSize - coreSize, forkOff = dinode[82] * 8;
        int dataFork = forkOff > 0 ? forkOff : literal;
        int r = 2;
        if ((fields & (IlogDData | IlogDExt | IlogDBroot)) != 0 && r < item.Regions.Count)
        {
            var region = item.Regions[r++];
            var fork = dinode.Slice(coreSize, Math.Min(dataFork, literal));
            if ((fields & IlogDBroot) != 0) BtreeRootToDisk(region, fork, _v5);
            else { fork.Clear(); region.AsSpan(0, Math.Min(Math.Min(dsize, region.Length), fork.Length)).CopyTo(fork); }
        }
        if ((fields & (IlogAData | IlogAExt | IlogABroot)) != 0 && r < item.Regions.Count && forkOff > 0 && forkOff < literal)
        {
            var region = item.Regions[r++];
            var fork = dinode.Slice(coreSize + forkOff, literal - forkOff);
            if ((fields & IlogABroot) != 0) BtreeRootToDisk(region, fork, _v5);
            else { fork.Clear(); region.AsSpan(0, Math.Min(Math.Min(asize, region.Length), fork.Length)).CopyTo(fork); }
        }
        Store(blkno, cluster);
    }

    /// <summary>
    /// Writes a logged inode core (in the writing computer's byte order) into an on-disk inode (big-endian).
    /// The on-disk unlinked-list pointer is kept, as the kernel does.
    /// </summary>
    private static void WriteCore(byte[] log, byte[] buffer, int at, bool le, int version)
    {
        Span<byte> D(int o) => buffer.AsSpan(at + o);
        int size = Math.Min(log.Length, version >= 3 ? 176 : 96);
        ulong Get(int o, int w) => w switch
        {
            1 => log[o],
            2 => le ? BinaryPrimitives.ReadUInt16LittleEndian(log.AsSpan(o)) : BinaryPrimitives.ReadUInt16BigEndian(log.AsSpan(o)),
            4 => le ? BinaryPrimitives.ReadUInt32LittleEndian(log.AsSpan(o)) : BinaryPrimitives.ReadUInt32BigEndian(log.AsSpan(o)),
            _ => le ? BinaryPrimitives.ReadUInt64LittleEndian(log.AsSpan(o)) : BinaryPrimitives.ReadUInt64BigEndian(log.AsSpan(o)),
        };
        void Put(int o, int w)
        {
            if (o + w > size) return;
            ulong v = Get(o, w);
            switch (w)
            {
                case 1: buffer[at + o] = (byte)v; break;
                case 2: BinaryPrimitives.WriteUInt16BigEndian(D(o), (ushort)v); break;
                case 4: BinaryPrimitives.WriteUInt32BigEndian(D(o), (uint)v); break;
                default: BinaryPrimitives.WriteUInt64BigEndian(D(o), v); break;
            }
        }
        void Raw(int o, int w) { if (o + w <= size) log.AsSpan(o, w).CopyTo(D(o)); }

        bool bigtime = version >= 3 && size >= 128 && (Get(120, 8) & 0x8) != 0;
        void Time(int o) { if (bigtime) Put(o, 8); else { Put(o, 4); Put(o + 4, 4); } }

        Put(0, 2); Put(2, 2); Put(4, 1); Put(5, 1); Put(6, 2); Put(8, 4); Put(12, 4); Put(16, 4); Put(20, 2); Put(22, 2);
        if (version >= 3) Put(24, 8); else { Raw(24, 6); Put(30, 2); }
        Time(32); Time(40); Time(48);
        Put(56, 8); Put(64, 8); Put(72, 4); Put(76, 4); Put(80, 2); Put(82, 1); Put(83, 1); Put(84, 4); Put(88, 2); Put(90, 2); Put(92, 4);
        // 96: di_next_unlinked stays as it is on disk.
        if (version >= 3)
        {
            Put(104, 8); Put(112, 8); Put(120, 8); Put(128, 4); Raw(132, 12); Time(144); Put(152, 8); Raw(160, 16);
        }
    }

    /// <summary>Converts a logged in-memory btree root block to the compact form stored in an inode fork.</summary>
    private static void BtreeRootToDisk(byte[] src, Span<byte> fork, bool v5)
    {
        int header = v5 ? 72 : 24;
        if (src.Length < header || fork.Length < 4) return;
        ushort level = BinaryPrimitives.ReadUInt16BigEndian(src.AsSpan(4)), records = BinaryPrimitives.ReadUInt16BigEndian(src.AsSpan(6));
        int srcMax = (src.Length - header) / 16, dstMax = (fork.Length - 4) / 16;
        if (records > srcMax || records > dstMax) throw new InvalidDataException("A logged file mapping is damaged.");
        fork.Clear();
        BinaryPrimitives.WriteUInt16BigEndian(fork, level);
        BinaryPrimitives.WriteUInt16BigEndian(fork[2..], records);
        src.AsSpan(header, records * 8).CopyTo(fork[4..]);
        src.AsSpan(header + srcMax * 8, records * 8).CopyTo(fork[(4 + dstMax * 8)..]);
    }

    private static uint BE32(byte[] b, int o) => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(o));
    private static ulong BE64(byte[] b, int o) => BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(o));
}
