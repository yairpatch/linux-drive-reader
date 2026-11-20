using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;

namespace LinuxFs.Core.Btrfs;

/// <summary>The four checksum algorithms btrfs can be formatted with.</summary>
internal static class Checksums
{
    /// <summary>Bytes of checksum stored per block for a btrfs checksum type, or 0 if the type is unknown.</summary>
    public static int Size(int type) => type switch { 0 => 4, 1 => 8, 2 => 32, 3 => 32, _ => 0 };

    public static void Compute(int type, ReadOnlySpan<byte> data, Span<byte> output)
    {
        switch (type)
        {
            case 0: BinaryPrimitives.WriteUInt32LittleEndian(output, Crc32C(data)); break;
            case 1: BinaryPrimitives.WriteUInt64LittleEndian(output, XxHash64(data)); break;
            case 2: SHA256.HashData(data, output); break;
            case 3: Blake2b256(data, output); break;
            default: throw new NotSupportedException("Unknown btrfs checksum type.");
        }
    }

    public static bool Matches(int type, ReadOnlySpan<byte> data, ReadOnlySpan<byte> expected)
    {
        Span<byte> actual = stackalloc byte[32];
        int size = Size(type);
        Compute(type, data, actual);
        return actual[..size].SequenceEqual(expected[..size]);
    }

    /// <summary>CRC32C continued from <paramref name="crc"/> with no inversion before or after, as Linux's crc32c() computes it.</summary>
    public static uint Crc32CRaw(uint crc, ReadOnlySpan<byte> data)
    {
        while (data.Length >= 8) { crc = BitOperations.Crc32C(crc, BinaryPrimitives.ReadUInt64LittleEndian(data)); data = data[8..]; }
        foreach (byte b in data) crc = BitOperations.Crc32C(crc, b);
        return crc;
    }

    public static uint Crc32C(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        while (data.Length >= 8) { crc = BitOperations.Crc32C(crc, BinaryPrimitives.ReadUInt64LittleEndian(data)); data = data[8..]; }
        foreach (byte b in data) crc = BitOperations.Crc32C(crc, b);
        return ~crc;
    }

    private const ulong P1 = 11400714785074694791, P2 = 14029467366897019727, P3 = 1609587929392839161, P4 = 9650029242287828579, P5 = 2870177450012600261;

    public static ulong XxHash64(ReadOnlySpan<byte> d)
    {
        static ulong Round(ulong acc, ulong input) => BitOperations.RotateLeft(acc + input * P2, 31) * P1;
        static ulong Merge(ulong acc, ulong v) => (acc ^ Round(0, v)) * P1 + P4;
        ulong length = (ulong)d.Length, h;
        if (d.Length >= 32)
        {
            ulong v1 = unchecked(P1 + P2), v2 = P2, v3 = 0, v4 = unchecked(0 - P1);
            while (d.Length >= 32)
            {
                v1 = Round(v1, BinaryPrimitives.ReadUInt64LittleEndian(d));
                v2 = Round(v2, BinaryPrimitives.ReadUInt64LittleEndian(d[8..]));
                v3 = Round(v3, BinaryPrimitives.ReadUInt64LittleEndian(d[16..]));
                v4 = Round(v4, BinaryPrimitives.ReadUInt64LittleEndian(d[24..]));
                d = d[32..];
            }
            h = BitOperations.RotateLeft(v1, 1) + BitOperations.RotateLeft(v2, 7) + BitOperations.RotateLeft(v3, 12) + BitOperations.RotateLeft(v4, 18);
            h = Merge(Merge(Merge(Merge(h, v1), v2), v3), v4);
        }
        else h = P5;
        h += length;
        while (d.Length >= 8)
        {
            h = BitOperations.RotateLeft(h ^ Round(0, BinaryPrimitives.ReadUInt64LittleEndian(d)), 27) * P1 + P4;
            d = d[8..];
        }
        if (d.Length >= 4)
        {
            h = BitOperations.RotateLeft(h ^ BinaryPrimitives.ReadUInt32LittleEndian(d) * P1, 23) * P2 + P3;
            d = d[4..];
        }
        foreach (byte b in d) h = BitOperations.RotateLeft(h ^ b * P5, 11) * P1;
        h ^= h >> 33; h *= P2; h ^= h >> 29; h *= P3; h ^= h >> 32;
        return h;
    }

    private static readonly ulong[] Iv =
    {
        0x6a09e667f3bcc908, 0xbb67ae8584caa73b, 0x3c6ef372fe94f82b, 0xa54ff53a5f1d36f1,
        0x510e527fade682d1, 0x9b05688c2b3e6c1f, 0x1f83d9abfb41bd6b, 0x5be0cd19137e2179,
    };
    private static readonly byte[] Sigma =
    {
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
        14, 10, 4, 8, 9, 15, 13, 6, 1, 12, 0, 2, 11, 7, 5, 3,
        11, 8, 12, 0, 5, 2, 15, 13, 10, 14, 3, 6, 7, 1, 9, 4,
        7, 9, 3, 1, 13, 12, 11, 14, 2, 6, 5, 10, 4, 0, 15, 8,
        9, 0, 5, 7, 2, 4, 10, 15, 14, 1, 11, 12, 6, 8, 3, 13,
        2, 12, 6, 10, 0, 11, 8, 3, 4, 13, 7, 5, 15, 14, 1, 9,
        12, 5, 1, 15, 14, 13, 4, 10, 0, 7, 6, 3, 9, 2, 8, 11,
        13, 11, 7, 14, 12, 1, 3, 9, 5, 0, 15, 4, 8, 6, 2, 10,
        6, 15, 14, 9, 11, 3, 0, 8, 12, 2, 13, 7, 1, 4, 10, 5,
        10, 2, 8, 4, 7, 6, 1, 5, 15, 11, 9, 14, 3, 12, 13, 0,
    };

    /// <summary>BLAKE2b with a 256-bit result and no key.</summary>
    public static void Blake2b256(ReadOnlySpan<byte> data, Span<byte> output) => Blake2b(data, output[..32]);

    /// <summary>BLAKE2b with no key; the result is as long as <paramref name="output"/> (1 to 64 bytes).</summary>
    public static void Blake2b(ReadOnlySpan<byte> data, Span<byte> output)
    {
        if (output.Length is < 1 or > 64) throw new ArgumentException("BLAKE2b output must be 1 to 64 bytes.");
        Span<ulong> h = stackalloc ulong[8];
        Iv.CopyTo(h);
        h[0] ^= 0x01010000UL ^ (uint)output.Length;
        Span<byte> last = stackalloc byte[128];
        ulong t = 0;
        while (data.Length > 128)
        {
            t += 128;
            Blake2Compress(h, data[..128], t, false);
            data = data[128..];
        }
        last.Clear();
        data.CopyTo(last);
        Blake2Compress(h, last, t + (ulong)data.Length, true);
        Span<byte> full = stackalloc byte[64];
        for (int i = 0; i < 8; i++) BinaryPrimitives.WriteUInt64LittleEndian(full[(i * 8)..], h[i]);
        full[..output.Length].CopyTo(output);
    }

    private static void Blake2Compress(Span<ulong> h, ReadOnlySpan<byte> block, ulong t, bool final)
    {
        Span<ulong> m = stackalloc ulong[16];
        Span<ulong> v = stackalloc ulong[16];
        for (int i = 0; i < 16; i++) m[i] = BinaryPrimitives.ReadUInt64LittleEndian(block[(i * 8)..]);
        h.CopyTo(v);
        Iv.CopyTo(v[8..]);
        v[12] ^= t;
        if (final) v[14] = ~v[14];
        for (int round = 0; round < 12; round++)
        {
            ReadOnlySpan<byte> s = Sigma.AsSpan(round % 10 * 16, 16);
            G(v, 0, 4, 8, 12, m[s[0]], m[s[1]]);
            G(v, 1, 5, 9, 13, m[s[2]], m[s[3]]);
            G(v, 2, 6, 10, 14, m[s[4]], m[s[5]]);
            G(v, 3, 7, 11, 15, m[s[6]], m[s[7]]);
            G(v, 0, 5, 10, 15, m[s[8]], m[s[9]]);
            G(v, 1, 6, 11, 12, m[s[10]], m[s[11]]);
            G(v, 2, 7, 8, 13, m[s[12]], m[s[13]]);
            G(v, 3, 4, 9, 14, m[s[14]], m[s[15]]);
        }
        for (int i = 0; i < 8; i++) h[i] ^= v[i] ^ v[i + 8];
    }

    private static void G(Span<ulong> v, int a, int b, int c, int d, ulong x, ulong y)
    {
        v[a] += v[b] + x; v[d] = BitOperations.RotateRight(v[d] ^ v[a], 32);
        v[c] += v[d]; v[b] = BitOperations.RotateRight(v[b] ^ v[c], 24);
        v[a] += v[b] + y; v[d] = BitOperations.RotateRight(v[d] ^ v[a], 16);
        v[c] += v[d]; v[b] = BitOperations.RotateRight(v[b] ^ v[c], 63);
    }
}
