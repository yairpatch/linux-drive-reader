using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using LinuxFs.Core.Btrfs;

namespace LinuxFs.Core.Crypto;

/// <summary>Argon2i and Argon2id (RFC 9106, version 1.3): the password-stretching functions LUKS2 uses.</summary>
internal static class Argon2
{
    public const int TypeI = 1, TypeId = 2;

    public static byte[] Hash(int type, byte[] password, byte[] salt, int passes, int memoryKiB, int lanes, int outputLength)
    {
        if (passes < 1 || lanes < 1 || lanes > 255 || memoryKiB < 8 || outputLength < 4) throw new InvalidDataException("Invalid Argon2 parameters.");
        int blocks = Math.Max(memoryKiB, 8 * lanes) / (4 * lanes) * (4 * lanes);
        int laneLength = blocks / lanes, segment = laneLength / 4;

        var seed = new List<byte>();
        void Add32(int v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(b, v); seed.AddRange(b.ToArray()); }
        Add32(lanes); Add32(outputLength); Add32(memoryKiB); Add32(passes); Add32(0x13); Add32(type);
        Add32(password.Length); seed.AddRange(password);
        Add32(salt.Length); seed.AddRange(salt);
        Add32(0); Add32(0);
        var h0 = new byte[72];
        Checksums.Blake2b(seed.ToArray(), h0.AsSpan(0, 64));

        var memory = new ulong[lanes][];
        for (int lane = 0; lane < lanes; lane++)
        {
            memory[lane] = new ulong[(long)laneLength * 128];
            BinaryPrimitives.WriteInt32LittleEndian(h0.AsSpan(68), lane);
            for (int i = 0; i < 2; i++)
            {
                BinaryPrimitives.WriteInt32LittleEndian(h0.AsSpan(64), i);
                MemoryMarshal.Cast<byte, ulong>(LongHash(h0, 1024)).CopyTo(memory[lane].AsSpan(i * 128, 128));
            }
        }

        for (int pass = 0; pass < passes; pass++)
            for (int slice = 0; slice < 4; slice++)
            {
                int p = pass, s = slice;
                Parallel.For(0, lanes, lane => FillSegment(memory, type, p, s, lane, lanes, laneLength, segment, blocks, passes));
            }

        var final = new ulong[128];
        for (int lane = 0; lane < lanes; lane++)
        {
            var last = memory[lane].AsSpan((laneLength - 1) * 128, 128);
            for (int i = 0; i < 128; i++) final[i] ^= last[i];
        }
        return LongHash(MemoryMarshal.AsBytes(final.AsSpan()).ToArray(), outputLength);
    }

    /// <summary>Blake2b stretched to any output length (H' in the specification).</summary>
    private static byte[] LongHash(byte[] input, int length)
    {
        var first = new byte[4 + input.Length];
        BinaryPrimitives.WriteInt32LittleEndian(first, length);
        input.CopyTo(first, 4);
        var output = new byte[length];
        if (length <= 64) { Checksums.Blake2b(first, output); return output; }

        int r = (length + 31) / 32 - 2;
        var v = new byte[64];
        Checksums.Blake2b(first, v);
        v.AsSpan(0, 32).CopyTo(output);
        for (int i = 1; i < r; i++)
        {
            var next = new byte[64];
            Checksums.Blake2b(v, next);
            v = next;
            v.AsSpan(0, 32).CopyTo(output.AsSpan(i * 32));
        }
        Checksums.Blake2b(v, output.AsSpan(r * 32));
        return output;
    }

    private static void FillSegment(ulong[][] memory, int type, int pass, int slice, int lane, int lanes, int laneLength, int segment, int blocks, int passes)
    {
        bool independent = type == TypeI || (type == TypeId && pass == 0 && slice < 2);
        var tmp = new ulong[256];
        ulong[]? input = null, addresses = null, zero = null;
        if (independent)
        {
            input = new ulong[128]; addresses = new ulong[128]; zero = new ulong[128];
            input[0] = (ulong)pass; input[1] = (ulong)lane; input[2] = (ulong)slice;
            input[3] = (ulong)blocks; input[4] = (ulong)passes; input[5] = (ulong)type;
        }
        void NextAddresses()
        {
            input![6]++;
            Compress(zero, input, addresses, false, tmp);
            Compress(zero, addresses, addresses, false, tmp);
        }

        int start = pass == 0 && slice == 0 ? 2 : 0;
        if (independent && start != 0) NextAddresses();
        var mine = memory[lane];
        for (int i = start; i < segment; i++)
        {
            int current = slice * segment + i;
            int previous = current == 0 ? laneLength - 1 : current - 1;
            ulong random;
            if (independent)
            {
                if (i % 128 == 0) NextAddresses();
                random = addresses![i % 128];
            }
            else random = mine[(long)previous * 128];

            int refLane = pass == 0 && slice == 0 ? lane : (int)((random >> 32) % (ulong)lanes);
            bool same = refLane == lane;
            long area;
            if (pass == 0)
                area = slice == 0 ? i - 1 : same ? (long)slice * segment + i - 1 : (long)slice * segment + (i == 0 ? -1 : 0);
            else
                area = same ? laneLength - segment + i - 1 : laneLength - segment + (i == 0 ? -1 : 0);
            ulong x = random & 0xFFFFFFFF;
            x = (x * x) >> 32;
            x = ((ulong)area * x) >> 32;
            long relative = area - 1 - (long)x;
            long startPosition = pass == 0 || slice == 3 ? 0 : (long)(slice + 1) * segment;
            long reference = (startPosition + relative) % laneLength;

            Compress(mine.AsSpan(previous * 128, 128), memory[refLane].AsSpan((int)reference * 128, 128), mine.AsSpan(current * 128, 128), pass > 0, tmp);
        }
    }

    private static void Compress(ReadOnlySpan<ulong> x, ReadOnlySpan<ulong> y, Span<ulong> destination, bool xorIntoDestination, ulong[] tmp)
    {
        Span<ulong> r = tmp.AsSpan(0, 128), z = tmp.AsSpan(128, 128);
        for (int i = 0; i < 128; i++) { r[i] = x[i] ^ y[i]; z[i] = r[i]; }
        for (int i = 0; i < 128; i += 16)
            Round(z, i, i + 1, i + 2, i + 3, i + 4, i + 5, i + 6, i + 7, i + 8, i + 9, i + 10, i + 11, i + 12, i + 13, i + 14, i + 15);
        for (int i = 0; i < 16; i += 2)
            Round(z, i, i + 1, i + 16, i + 17, i + 32, i + 33, i + 48, i + 49, i + 64, i + 65, i + 80, i + 81, i + 96, i + 97, i + 112, i + 113);
        if (xorIntoDestination) for (int i = 0; i < 128; i++) destination[i] ^= r[i] ^ z[i];
        else for (int i = 0; i < 128; i++) destination[i] = r[i] ^ z[i];
    }

    private static void Round(Span<ulong> v, int i0, int i1, int i2, int i3, int i4, int i5, int i6, int i7, int i8, int i9, int i10, int i11, int i12, int i13, int i14, int i15)
    {
        G(ref v[i0], ref v[i4], ref v[i8], ref v[i12]);
        G(ref v[i1], ref v[i5], ref v[i9], ref v[i13]);
        G(ref v[i2], ref v[i6], ref v[i10], ref v[i14]);
        G(ref v[i3], ref v[i7], ref v[i11], ref v[i15]);
        G(ref v[i0], ref v[i5], ref v[i10], ref v[i15]);
        G(ref v[i1], ref v[i6], ref v[i11], ref v[i12]);
        G(ref v[i2], ref v[i7], ref v[i8], ref v[i13]);
        G(ref v[i3], ref v[i4], ref v[i9], ref v[i14]);
    }

    private static void G(ref ulong a, ref ulong b, ref ulong c, ref ulong d)
    {
        a = a + b + 2 * (ulong)(uint)a * (uint)b; d = BitOperations.RotateRight(d ^ a, 32);
        c = c + d + 2 * (ulong)(uint)c * (uint)d; b = BitOperations.RotateRight(b ^ c, 24);
        a = a + b + 2 * (ulong)(uint)a * (uint)b; d = BitOperations.RotateRight(d ^ a, 16);
        c = c + d + 2 * (ulong)(uint)c * (uint)d; b = BitOperations.RotateRight(b ^ c, 63);
    }
}
