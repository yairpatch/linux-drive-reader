using System.Numerics;

namespace LinuxFs.Core.Btrfs;

/// <summary>Zstandard decompression (RFC 8878), written for btrfs "compress=zstd". No dictionary support.</summary>
internal sealed class Zstd
{
    private static InvalidDataException Bad(string what = "") => new("Damaged zstd-compressed data" + (what.Length > 0 ? $" ({what})." : "."));

    private sealed class FseTable { public int Log; public byte[] Symbol = null!; public byte[] Bits = null!; public ushort[] Base = null!; }
    private sealed class HufTable { public int MaxBits; public byte[] Symbol = null!; public byte[] Bits = null!; }

    private static readonly short[] LlDefault = { 4, 3, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 1, 1, 1, 2, 2, 2, 2, 2, 2, 2, 2, 2, 3, 2, 1, 1, 1, 1, 1, -1, -1, -1, -1 };
    private static readonly short[] MlDefault = { 1, 4, 3, 2, 2, 2, 2, 2, 2, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, -1, -1, -1, -1, -1, -1, -1 };
    private static readonly short[] OfDefault = { 1, 1, 1, 1, 1, 1, 2, 2, 2, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, -1, -1, -1, -1, -1 };
    private static readonly int[] LlBase = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 18, 20, 22, 24, 28, 32, 40, 48, 64, 128, 256, 512, 1024, 2048, 4096, 8192, 16384, 32768, 65536 };
    private static readonly byte[] LlBits = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 3, 3, 4, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 };
    private static readonly int[] MlBase = { 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 37, 39, 41, 43, 47, 51, 59, 67, 83, 99, 131, 259, 515, 1027, 2051, 4099, 8195, 16387, 32771, 65539 };
    private static readonly byte[] MlBits = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 3, 3, 4, 4, 5, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 };
    private static readonly FseTable LlPredefined = BuildFse(LlDefault, LlDefault.Length, 6);
    private static readonly FseTable MlPredefined = BuildFse(MlDefault, MlDefault.Length, 6);
    private static readonly FseTable OfPredefined = BuildFse(OfDefault, OfDefault.Length, 5);

    private readonly byte[] _src;
    private HufTable? _huf;
    private FseTable? _ll, _of, _ml;
    private readonly long[] _rep = { 1, 4, 8 };

    private Zstd(byte[] src) => _src = src;

    /// <summary>Decompresses the first frame in <paramref name="src"/> into <paramref name="dst"/>; returns bytes written.</summary>
    public static int Decompress(byte[] src, int length, byte[] dst) => new Zstd(src).Frame(length, dst);

    private int Frame(int end, byte[] dst)
    {
        if (end < 6 || _src[0] != 0x28 || _src[1] != 0xB5 || _src[2] != 0x2F || _src[3] != 0xFD) throw Bad("no frame header");
        int fhd = _src[4], pos = 5;
        bool single = (fhd & 0x20) != 0;
        if ((fhd & 0x08) != 0) throw Bad("reserved bit");
        if (!single) pos++;
        int dict = fhd & 3;
        pos += dict == 0 ? 0 : dict == 1 ? 1 : dict == 2 ? 2 : 4;
        int fcs = fhd >> 6;
        pos += fcs == 0 ? (single ? 1 : 0) : fcs == 1 ? 2 : fcs == 2 ? 4 : 8;

        int outPos = 0;
        while (true)
        {
            if (pos + 3 > end) throw Bad("truncated");
            int header = _src[pos] | _src[pos + 1] << 8 | _src[pos + 2] << 16;
            pos += 3;
            bool last = (header & 1) != 0;
            int type = (header >> 1) & 3, size = header >> 3;
            switch (type)
            {
                case 0:
                    if (pos + size > end || outPos + size > dst.Length) throw Bad("raw block");
                    Buffer.BlockCopy(_src, pos, dst, outPos, size);
                    pos += size; outPos += size;
                    break;
                case 1:
                    if (pos + 1 > end || outPos + size > dst.Length) throw Bad("RLE block");
                    dst.AsSpan(outPos, size).Fill(_src[pos]);
                    pos++; outPos += size;
                    break;
                case 2:
                    if (pos + size > end) throw Bad("compressed block");
                    Block(pos, pos + size, dst, ref outPos);
                    pos += size;
                    break;
                default: throw Bad("reserved block type");
            }
            if (last) return outPos;
        }
    }

    // ------------------------------------------------------------ bit input

    private static ulong GetBits(byte[] d, int start, int len, long off, int n)
    {
        if (n == 0) return 0;
        if (off < 0)
        {
            int skip = (int)Math.Min(n, -off);
            return skip == n ? 0 : GetBits(d, start, len, 0, n - skip) << skip;
        }
        long byteIdx = off >> 3;
        int shift = (int)(off & 7);
        ulong v = 0;
        for (int i = 0; i < 8; i++)
        {
            long bi = byteIdx + i;
            if (bi < len) v |= (ulong)d[start + bi] << (8 * i);
        }
        return (v >> shift) & ((1UL << n) - 1);
    }

    /// <summary>Reads a bitstream from its end towards its start, as zstd's entropy coders write them.</summary>
    private struct BackBits
    {
        private readonly byte[] _d; private readonly int _start, _len;
        public long Offset;
        public BackBits(byte[] d, int start, int len)
        {
            if (len <= 0 || d[start + len - 1] == 0) throw Bad("bitstream end");
            _d = d; _start = start; _len = len;
            Offset = (len - 1) * 8L + BitOperations.Log2(d[start + len - 1]);
        }
        public int Read(int n) { Offset -= n; return (int)GetBits(_d, _start, _len, Offset, n); }
    }

    // ------------------------------------------------------------------ FSE

    private static FseTable BuildFse(short[] freq, int count, int log)
    {
        int size = 1 << log, high = size;
        var t = new FseTable { Log = log, Symbol = new byte[size], Bits = new byte[size], Base = new ushort[size] };
        var next = new int[count];
        for (int s = 0; s < count; s++)
            if (freq[s] == -1) { t.Symbol[--high] = (byte)s; next[s] = 1; }
        int step = (size >> 1) + (size >> 3) + 3, mask = size - 1, pos = 0;
        for (int s = 0; s < count; s++)
        {
            if (freq[s] <= 0) continue;
            next[s] = freq[s];
            for (int i = 0; i < freq[s]; i++)
            {
                t.Symbol[pos] = (byte)s;
                do pos = (pos + step) & mask; while (pos >= high);
            }
        }
        if (pos != 0) throw Bad("FSE table");
        for (int i = 0; i < size; i++)
        {
            int state = next[t.Symbol[i]]++;
            int bits = log - BitOperations.Log2((uint)state);
            t.Bits[i] = (byte)bits;
            t.Base[i] = (ushort)((state << bits) - size);
        }
        return t;
    }

    private FseTable ReadFse(ref int pos, int end, int maxLog, int maxSymbols)
    {
        long bit = pos * 8L;
        int Read(int n) { int v = (int)GetBits(_src, 0, end, bit, n); bit += n; return v; }

        int log = 5 + Read(4);
        if (log > maxLog) throw Bad("FSE accuracy");
        int remaining = 1 << log, sym = 0;
        var freq = new short[maxSymbols];
        while (remaining > 0 && sym < maxSymbols)
        {
            int bits = BitOperations.Log2((uint)(remaining + 1)) + 1;
            int val = Read(bits);
            int lowerMask = (1 << (bits - 1)) - 1;
            int threshold = (1 << bits) - 1 - (remaining + 1);
            if ((val & lowerMask) < threshold) { bit--; val &= lowerMask; }
            else if (val > lowerMask) val -= threshold;
            int proba = val - 1;
            remaining -= proba < 0 ? 1 : proba;
            freq[sym++] = (short)proba;
            if (proba == 0)
            {
                int repeat = Read(2);
                while (true)
                {
                    for (int i = 0; i < repeat && sym < maxSymbols; i++) freq[sym++] = 0;
                    if (repeat == 3) repeat = Read(2); else break;
                }
            }
        }
        if (remaining != 0 || bit > end * 8L) throw Bad("FSE header");
        pos = (int)((bit + 7) / 8);
        return BuildFse(freq, sym, log);
    }

    // -------------------------------------------------------------- Huffman

    private HufTable ReadHuffman(ref int pos, int end)
    {
        if (pos >= end) throw Bad("Huffman header");
        int header = _src[pos++];
        var weights = new byte[256];
        int n;
        if (header >= 128)
        {
            n = header - 127;
            if (pos + (n + 1) / 2 > end) throw Bad("Huffman weights");
            for (int i = 0; i < n; i++)
            {
                int b = _src[pos + i / 2];
                weights[i] = (byte)(i % 2 == 0 ? b >> 4 : b & 15);
            }
            pos += (n + 1) / 2;
        }
        else
        {
            int cEnd = pos + header;
            if (cEnd > end) throw Bad("Huffman weights");
            int p = pos;
            var fse = ReadFse(ref p, cEnd, 7, 256);
            var br = new BackBits(_src, p, cEnd - p);
            int s1 = br.Read(fse.Log), s2 = br.Read(fse.Log);
            n = 0;
            while (true)
            {
                if (n > 253) throw Bad("Huffman weights");
                weights[n++] = fse.Symbol[s1];
                s1 = fse.Base[s1] + br.Read(fse.Bits[s1]);
                if (br.Offset < 0) { weights[n++] = fse.Symbol[s2]; break; }
                weights[n++] = fse.Symbol[s2];
                s2 = fse.Base[s2] + br.Read(fse.Bits[s2]);
                if (br.Offset < 0) { weights[n++] = fse.Symbol[s1]; break; }
            }
            pos = cEnd;
        }

        int sum = 0;
        for (int i = 0; i < n; i++)
        {
            if (weights[i] > 12) throw Bad("Huffman weight");
            if (weights[i] > 0) sum += 1 << (weights[i] - 1);
        }
        if (sum == 0) throw Bad("Huffman weights");
        int maxBits = BitOperations.Log2((uint)sum) + 1;
        int left = (1 << maxBits) - sum;
        if (maxBits > 12 || !BitOperations.IsPow2(left)) throw Bad("Huffman tree");
        weights[n++] = (byte)(BitOperations.Log2((uint)left) + 1);

        int size = 1 << maxBits;
        var t = new HufTable { MaxBits = maxBits, Symbol = new byte[size], Bits = new byte[size] };
        var count = new int[maxBits + 2];
        var index = new int[maxBits + 2];
        for (int i = 0; i < n; i++) if (weights[i] > 0) count[maxBits + 1 - weights[i]]++;
        index[maxBits] = 0;
        for (int len = maxBits; len >= 1; len--) index[len - 1] = index[len] + count[len] * (1 << (maxBits - len));
        if (index[0] != size) throw Bad("Huffman tree");
        for (int i = 0; i < n; i++)
        {
            if (weights[i] == 0) continue;
            int len = maxBits + 1 - weights[i], span = 1 << (maxBits - len);
            t.Symbol.AsSpan(index[len], span).Fill((byte)i);
            t.Bits.AsSpan(index[len], span).Fill((byte)len);
            index[len] += span;
        }
        return t;
    }

    private void HuffmanStream(HufTable t, int start, int len, Span<byte> output)
    {
        var br = new BackBits(_src, start, len);
        int mask = (1 << t.MaxBits) - 1;
        int state = br.Read(t.MaxBits);
        for (int i = 0; i < output.Length; i++)
        {
            output[i] = t.Symbol[state];
            int bits = t.Bits[state];
            state = ((state << bits) & mask) | br.Read(bits);
        }
        if (br.Offset < -t.MaxBits) throw Bad("Huffman stream");
    }

    // ---------------------------------------------------------------- block

    private void Block(int pos, int end, byte[] dst, ref int outPos)
    {
        if (pos >= end) throw Bad("empty block");
        int b0 = _src[pos], type = b0 & 3, format = (b0 >> 2) & 3;
        byte[] literals;
        if (type < 2)
        {
            int regen, header;
            if (format is 0 or 2) { regen = b0 >> 3; header = 1; }
            else if (format == 1) { if (pos + 2 > end) throw Bad(); regen = (b0 >> 4) | _src[pos + 1] << 4; header = 2; }
            else { if (pos + 3 > end) throw Bad(); regen = (b0 >> 4) | _src[pos + 1] << 4 | _src[pos + 2] << 12; header = 3; }
            pos += header;
            literals = new byte[regen];
            if (type == 0)
            {
                if (pos + regen > end) throw Bad("raw literals");
                Buffer.BlockCopy(_src, pos, literals, 0, regen);
                pos += regen;
            }
            else
            {
                if (pos + 1 > end) throw Bad("RLE literals");
                Array.Fill(literals, _src[pos++]);
            }
        }
        else
        {
            int header = format <= 1 ? 3 : format == 2 ? 4 : 5;
            int bits = format <= 1 ? 10 : format == 2 ? 14 : 18;
            if (pos + header > end) throw Bad("literals header");
            ulong h = 0;
            for (int i = 0; i < header; i++) h |= (ulong)_src[pos + i] << (8 * i);
            int regen = (int)((h >> 4) & ((1UL << bits) - 1));
            int comp = (int)((h >> (4 + bits)) & ((1UL << bits) - 1));
            pos += header;
            int lend = pos + comp;
            if (lend > end) throw Bad("literals size");
            if (type == 2) _huf = ReadHuffman(ref pos, lend);
            var huf = _huf ?? throw Bad("missing Huffman table");
            literals = new byte[regen];
            if (format == 0) HuffmanStream(huf, pos, lend - pos, literals);
            else
            {
                if (pos + 6 > lend) throw Bad("jump table");
                int s1 = _src[pos] | _src[pos + 1] << 8, s2 = _src[pos + 2] | _src[pos + 3] << 8, s3 = _src[pos + 4] | _src[pos + 5] << 8;
                pos += 6;
                int s4 = lend - pos - s1 - s2 - s3;
                int q = (regen + 3) / 4;
                if (s4 <= 0 || 3 * q > regen) throw Bad("literal streams");
                HuffmanStream(huf, pos, s1, literals.AsSpan(0, q));
                HuffmanStream(huf, pos + s1, s2, literals.AsSpan(q, q));
                HuffmanStream(huf, pos + s1 + s2, s3, literals.AsSpan(2 * q, q));
                HuffmanStream(huf, pos + s1 + s2 + s3, s4, literals.AsSpan(3 * q));
            }
            pos = lend;
        }

        if (pos >= end) throw Bad("missing sequences");
        int count = _src[pos++];
        if (count >= 128)
        {
            if (count < 255) { if (pos >= end) throw Bad(); count = ((count - 128) << 8) + _src[pos++]; }
            else { if (pos + 2 > end) throw Bad(); count = _src[pos] + (_src[pos + 1] << 8) + 0x7F00; pos += 2; }
        }
        int litPos = 0;
        if (count > 0)
        {
            if (pos >= end) throw Bad("sequence modes");
            int modes = _src[pos++];
            _ll = SequenceTable(modes >> 6, ref pos, end, _ll, LlPredefined, 9, 36);
            _of = SequenceTable((modes >> 4) & 3, ref pos, end, _of, OfPredefined, 8, 32);
            _ml = SequenceTable((modes >> 2) & 3, ref pos, end, _ml, MlPredefined, 9, 53);
            FseTable ll = _ll, of = _of, ml = _ml;
            if (pos >= end) throw Bad("sequence data");

            var br = new BackBits(_src, pos, end - pos);
            int sLl = br.Read(ll.Log), sOf = br.Read(of.Log), sMl = br.Read(ml.Log);
            for (int i = 0; i < count; i++)
            {
                int ofCode = of.Symbol[sOf], llCode = ll.Symbol[sLl], mlCode = ml.Symbol[sMl];
                if (ofCode > 31 || llCode > 35 || mlCode > 52) throw Bad("sequence code");
                long offsetValue = (1L << ofCode) + (ofCode > 24
                    ? ((long)br.Read(ofCode - 16) << 16) | (uint)br.Read(16)
                    : br.Read(ofCode));
                int matchLength = MlBase[mlCode] + br.Read(MlBits[mlCode]);
                int litLength = LlBase[llCode] + br.Read(LlBits[llCode]);
                if (i < count - 1)
                {
                    sLl = ll.Base[sLl] + br.Read(ll.Bits[sLl]);
                    sMl = ml.Base[sMl] + br.Read(ml.Bits[sMl]);
                    sOf = of.Base[sOf] + br.Read(of.Bits[sOf]);
                }
                if (br.Offset < 0) throw Bad("sequence bitstream");

                long offset;
                if (offsetValue > 3)
                {
                    offset = offsetValue - 3;
                    _rep[2] = _rep[1]; _rep[1] = _rep[0]; _rep[0] = offset;
                }
                else
                {
                    int idx = (int)offsetValue - 1 + (litLength == 0 ? 1 : 0);
                    if (idx == 0) offset = _rep[0];
                    else
                    {
                        offset = idx < 3 ? _rep[idx] : _rep[0] - 1;
                        if (offset <= 0) throw Bad("repeat offset");
                        if (idx > 1) _rep[2] = _rep[1];
                        _rep[1] = _rep[0]; _rep[0] = offset;
                    }
                }

                if (litPos + litLength > literals.Length || outPos + litLength + (long)matchLength > dst.Length) throw Bad("sequence overflows");
                Buffer.BlockCopy(literals, litPos, dst, outPos, litLength);
                litPos += litLength; outPos += litLength;
                if (offset > outPos) throw Bad("offset before start");
                int from = outPos - (int)offset;
                for (int k = 0; k < matchLength; k++) dst[outPos++] = dst[from++];
            }
        }
        int rest = literals.Length - litPos;
        if (outPos + rest > dst.Length) throw Bad("literals overflow");
        Buffer.BlockCopy(literals, litPos, dst, outPos, rest);
        outPos += rest;
    }

    private FseTable SequenceTable(int mode, ref int pos, int end, FseTable? previous, FseTable predefined, int maxLog, int maxSymbols)
    {
        switch (mode)
        {
            case 0: return predefined;
            case 1:
                if (pos >= end) throw Bad("RLE table");
                return new FseTable { Log = 0, Symbol = new[] { _src[pos++] }, Bits = new byte[1], Base = new ushort[1] };
            case 2: return ReadFse(ref pos, end, maxLog, maxSymbols);
            default: return previous ?? throw Bad("repeat table without a previous one");
        }
    }
}
