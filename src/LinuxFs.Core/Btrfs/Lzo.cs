namespace LinuxFs.Core.Btrfs;

/// <summary>LZO1X decompression, as used by btrfs "compress=lzo".</summary>
internal static class Lzo
{
    private static InvalidDataException Bad() => new("Damaged LZO-compressed data.");

    /// <summary>Decompresses one LZO1X block and returns the number of bytes written.</summary>
    public static int Decompress(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        int ip = 0, op = 0, state = 0;
        if (src.Length < 3) throw Bad();

        void Literals(ReadOnlySpan<byte> s, Span<byte> d, int count)
        {
            if (count < 0 || ip + count > s.Length || op + count > d.Length) throw Bad();
            s.Slice(ip, count).CopyTo(d[op..]);
            ip += count; op += count;
        }
        int Extend(ReadOnlySpan<byte> s, int add)
        {
            int t = 0;
            while (true)
            {
                if (ip >= s.Length) throw Bad();
                if (s[ip] != 0) break;
                t += 255; ip++;
                if (t > 1 << 24) throw Bad();
            }
            return t + add + s[ip++];
        }

        if (src[ip] > 17)
        {
            int t = src[ip++] - 17;
            Literals(src, dst, t);
            state = t < 4 ? t : 4;
        }

        while (true)
        {
            if (ip >= src.Length) throw Bad();
            int t = src[ip++];
            int pos, len, next;
            if (t < 16)
            {
                if (state == 0)
                {
                    if (t == 0) t = Extend(src, 15);
                    Literals(src, dst, t + 3);
                    state = 4;
                    continue;
                }
                if (ip >= src.Length) throw Bad();
                next = t & 3;
                if (state != 4) { pos = op - 1 - (t >> 2) - (src[ip++] << 2); len = 2; }
                else { pos = op - (1 + 0x800) - (t >> 2) - (src[ip++] << 2); len = 3; }
            }
            else if (t >= 64)
            {
                if (ip >= src.Length) throw Bad();
                next = t & 3;
                pos = op - 1 - ((t >> 2) & 7) - (src[ip++] << 3);
                len = (t >> 5) + 1;
            }
            else if (t >= 32)
            {
                len = (t & 31) + 2;
                if (len == 2) len = Extend(src, 31) + 2;
                if (ip + 2 > src.Length) throw Bad();
                int v = src[ip] | src[ip + 1] << 8;
                ip += 2;
                pos = op - 1 - (v >> 2);
                next = v & 3;
            }
            else
            {
                pos = op - ((t & 8) << 11);
                len = (t & 7) + 2;
                if (len == 2) len = Extend(src, 7) + 2;
                if (ip + 2 > src.Length) throw Bad();
                int v = src[ip] | src[ip + 1] << 8;
                ip += 2;
                pos -= v >> 2;
                next = v & 3;
                if (pos == op) return op; // end-of-stream marker
                pos -= 0x4000;
            }

            if (pos < 0 || op + len > dst.Length) throw Bad();
            for (int i = 0; i < len; i++) dst[op++] = dst[pos++];
            Literals(src, dst, next);
            state = next;
        }
    }

    /// <summary>btrfs wraps LZO in its own framing: a total length, then one length-prefixed block per 4 KiB of data.</summary>
    public static void DecompressBtrfs(ReadOnlySpan<byte> src, Span<byte> dst, int sectorSize)
    {
        if (src.Length < 4) throw Bad();
        int total = (int)Math.Min(BitConverter.ToUInt32(src), (uint)src.Length);
        int pos = 4, outPos = 0;
        // Each block expands to at most one sector, but the last may hold more than the caller wants
        // (zero padding after the end of a small file), so expand into a spare sector and keep what fits.
        var sector = new byte[sectorSize];
        while (pos + 4 <= total && outPos < dst.Length)
        {
            int segment = (int)BitConverter.ToUInt32(src[pos..]);
            pos += 4;
            if (segment <= 0 || pos + segment > src.Length) throw Bad();
            int produced = Decompress(src.Slice(pos, segment), sector);
            int room = Math.Min(Math.Min(sectorSize, dst.Length - outPos), produced);
            sector.AsSpan(0, room).CopyTo(dst[outPos..]);
            // A short block mid-file still stands for a whole sector; the rest of it reads as zeros.
            outPos += Math.Min(sectorSize, dst.Length - outPos);
            pos += segment;
            int left = sectorSize - pos % sectorSize;
            if (left < 4) pos += left; // a block header never straddles a sector boundary
        }
    }
}
