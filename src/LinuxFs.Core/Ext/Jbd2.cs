using System.Buffers.Binary;
using LinuxFs.Core.Btrfs;

namespace LinuxFs.Core.Ext;

/// <summary>
/// Reads the ext3/ext4 journal (jbd2) and works out what replaying it would write, without writing anything:
/// the same scan, revoke and replay passes the Linux kernel runs at mount time.
/// </summary>
internal static class Jbd2
{
    public sealed class Result
    {
        /// <summary>Filesystem block number to its content after replay.</summary>
        public Dictionary<long, byte[]> Blocks { get; } = new();
        public int Transactions { get; set; }
        public List<string> Problems { get; } = new();
    }

    private const uint Magic = 0xC03B3998;
    private const uint Descriptor = 1, Commit = 2, SuperV1 = 3, SuperV2 = 4, Revoke = 5;
    private const uint FeatRevoke = 0x1, Feat64Bit = 0x2, FeatAsyncCommit = 0x4, FeatCsumV2 = 0x8, FeatCsumV3 = 0x10, FeatFastCommit = 0x20;
    private const uint TagEscape = 1, TagSameUuid = 2, TagLast = 8;

    private readonly record struct Tag(long Block, long Position, uint Flags, uint Checksum);

    public static Result Replay(ExtFileSystem fs, long journalInode, long filesystemBlocks)
    {
        var inode = fs.ReadInode(journalInode);
        var map = fs.CreateMap(inode);
        int bs = fs.BlockSize;
        long journalBlocks = Math.Max(1, inode.Size / bs);

        byte[] Read(long j)
        {
            if (j < 0 || j >= journalBlocks) throw new InvalidDataException("The journal points outside itself.");
            var (physical, _) = map.Map(j);
            if (physical == 0) throw new InvalidDataException("The journal has a hole in it.");
            var b = new byte[bs];
            fs.ReadBlock(physical, b);
            return b;
        }

        var sb = Read(0);
        uint type = BE32(sb, 4);
        if (BE32(sb, 0) != Magic || type is not (SuperV1 or SuperV2))
            throw new InvalidDataException("The journal's header is damaged.");
        if (BE32(sb, 12) != bs) throw new NotSupportedException("The journal uses a different block size from the filesystem.");
        long maxLen = Math.Min(BE32(sb, 16), journalBlocks), first = BE32(sb, 20);
        uint sequence = BE32(sb, 24);
        long start = BE32(sb, 28);
        var result = new Result();
        if (start == 0) return result; // nothing to replay
        if (first < 1 || first >= maxLen || start < first || start >= maxLen) throw new InvalidDataException("The journal's header is damaged.");

        uint incompat = type == SuperV2 ? BE32(sb, 0x28) : 0;
        const uint known = FeatRevoke | Feat64Bit | FeatAsyncCommit | FeatCsumV2 | FeatCsumV3 | FeatFastCommit;
        if ((incompat & ~known) != 0) throw new NotSupportedException($"The journal uses features this app does not know (0x{incompat & ~known:X}).");
        bool is64 = (incompat & Feat64Bit) != 0, v3 = (incompat & FeatCsumV3) != 0, v2 = (incompat & FeatCsumV2) != 0, csum = v2 || v3;
        if ((incompat & FeatFastCommit) != 0)
            result.Problems.Add("This journal also has \"fast commit\" entries, which are not applied. The very latest changes may be missing.");
        uint seed = csum ? Checksums.Crc32CRaw(~0u, sb.AsSpan(0x30, 16)) : 0;
        int tagSize = v3 ? 16 : 12 + (v2 ? 2 : 0) - (is64 ? 0 : 4);
        int tail = csum ? 4 : 0;
        long Next(long p) => p + 1 >= maxLen ? first : p + 1;

        // Pass 1: walk the log from its start, collecting every transaction that has its commit block.
        var committed = new List<(uint Sequence, List<Tag> Tags)>();
        var revoked = new Dictionary<long, uint>();
        var tags = new List<Tag>();
        var revokes = new List<long>();
        long pos = start;
        uint expect = sequence;
        for (long steps = 0; steps < maxLen; steps++)
        {
            byte[] b;
            try { b = Read(pos); }
            catch (Exception ex) when (ex is IOException or InvalidDataException) { break; }
            if (BE32(b, 0) != Magic || BE32(b, 8) != expect) break;
            uint blockType = BE32(b, 4);
            if (blockType == Descriptor)
            {
                if (csum && !TailOk(b, seed)) break;
                long p = pos;
                bool last = false;
                for (int off = 12; !last && off + tagSize <= bs - tail; )
                {
                    long block = BE32(b, off);
                    uint flags, sum;
                    if (v3) { flags = BE32(b, off + 4); if (is64) block |= (long)BE32(b, off + 8) << 32; sum = BE32(b, off + 12); }
                    else { sum = BE16(b, off + 4); flags = BE16(b, off + 6); if (is64) block |= (long)BE32(b, off + 8) << 32; }
                    off += tagSize;
                    if ((flags & TagSameUuid) == 0) off += 16;
                    p = Next(p);
                    tags.Add(new Tag(block, p, flags, sum));
                    last = (flags & TagLast) != 0;
                }
                pos = Next(p);
            }
            else if (blockType == Commit)
            {
                if (csum && !CommitOk(b, seed)) break;
                committed.Add((expect, tags));
                foreach (long r in revokes) revoked[r] = revoked.TryGetValue(r, out uint s) ? Math.Max(s, expect) : expect;
                tags = new List<Tag>();
                revokes = new List<long>();
                expect++;
                pos = Next(pos);
            }
            else if (blockType == Revoke)
            {
                int used = (int)Math.Min(BE32(b, 12), (uint)(bs - tail)), size = is64 ? 8 : 4;
                for (int off = 16; off + size <= used; off += size)
                    revokes.Add(is64 ? (long)BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(off)) : BE32(b, off));
                pos = Next(pos);
            }
            else break;
        }

        // Pass 2: write each logged block, oldest transaction first, unless a later transaction revoked it.
        Span<byte> seqBytes = stackalloc byte[4];
        foreach (var (seq, list) in committed)
        {
            foreach (var tag in list)
            {
                if (revoked.TryGetValue(tag.Block, out uint until) && seq <= until) continue;
                // Block 0 is allowed: it holds the superblock on filesystems with 4 KiB blocks.
                if (tag.Block < 0 || tag.Block >= filesystemBlocks) { result.Problems.Add("The journal refers to a block outside the filesystem; it was skipped."); continue; }
                byte[] data;
                try { data = Read(tag.Position); }
                catch (Exception ex) when (ex is IOException or InvalidDataException) { result.Problems.Add("Part of the journal could not be read; some changes were skipped."); continue; }
                if (csum)
                {
                    BinaryPrimitives.WriteUInt32BigEndian(seqBytes, seq);
                    uint c = Checksums.Crc32CRaw(Checksums.Crc32CRaw(seed, seqBytes), data);
                    if (v3 ? c != tag.Checksum : (c & 0xFFFF) != tag.Checksum)
                    {
                        result.Problems.Add("A block in the journal failed its integrity check and was skipped.");
                        continue;
                    }
                }
                if ((tag.Flags & TagEscape) != 0) BinaryPrimitives.WriteUInt32BigEndian(data, Magic);
                result.Blocks[tag.Block] = data;
            }
        }
        result.Transactions = committed.Count;
        return result;
    }

    private static bool TailOk(byte[] b, uint seed)
    {
        uint stored = BE32(b, b.Length - 4);
        var copy = (byte[])b.Clone();
        copy.AsSpan(copy.Length - 4).Clear();
        return Checksums.Crc32CRaw(seed, copy) == stored;
    }

    private static bool CommitOk(byte[] b, uint seed)
    {
        uint stored = BE32(b, 16);
        var copy = (byte[])b.Clone();
        copy.AsSpan(16, 4).Clear();
        return Checksums.Crc32CRaw(seed, copy) == stored;
    }

    private static uint BE32(byte[] b, int o) => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(o));
    private static uint BE16(byte[] b, int o) => BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(o));
}
