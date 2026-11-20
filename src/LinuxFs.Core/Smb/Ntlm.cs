using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace LinuxFs.Core.Smb;

/// <summary>The server side of NTLMv2 authentication, enough to prove the Windows client knows our one password.</summary>
internal static class Ntlm
{
    private const uint FlagKeyExchange = 0x40000000;
    private static readonly byte[] Signature = "NTLMSSP\0"u8.ToArray();

    /// <summary>Finds an NTLM message inside the SPNEGO wrapping the SMB client sends.</summary>
    public static ReadOnlySpan<byte> Find(ReadOnlySpan<byte> securityBuffer, out int messageType)
    {
        messageType = 0;
        int at = securityBuffer.IndexOf(Signature);
        if (at < 0 || at + 12 > securityBuffer.Length) return default;
        messageType = BinaryPrimitives.ReadInt32LittleEndian(securityBuffer[(at + 8)..]);
        return securityBuffer[at..];
    }

    public static byte[] BuildChallenge(byte[] serverChallenge, string serverName)
    {
        byte[] name = Encoding.Unicode.GetBytes(serverName);
        var info = new List<byte>();
        void Pair(ushort id, byte[] value)
        {
            info.AddRange(BitConverter.GetBytes(id)); info.AddRange(BitConverter.GetBytes((ushort)value.Length)); info.AddRange(value);
        }
        Pair(2, name); Pair(1, name); Pair(4, name); Pair(3, name);
        Pair(7, BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()));
        Pair(0, Array.Empty<byte>());

        var m = new byte[56 + name.Length + info.Count];
        Signature.CopyTo(m, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(m.AsSpan(8), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(m.AsSpan(12), (ushort)name.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(m.AsSpan(14), (ushort)name.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(m.AsSpan(16), 56);
        BinaryPrimitives.WriteUInt32LittleEndian(m.AsSpan(20), 0xE28A8215);
        serverChallenge.CopyTo(m, 24);
        BinaryPrimitives.WriteUInt16LittleEndian(m.AsSpan(40), (ushort)info.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(m.AsSpan(42), (ushort)info.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(m.AsSpan(44), (uint)(56 + name.Length));
        m[48] = 10; m[49] = 0; BinaryPrimitives.WriteUInt16LittleEndian(m.AsSpan(50), 26100); m[55] = 15; // version
        name.CopyTo(m, 56);
        info.CopyTo(m, 56 + name.Length);
        return m;
    }

    /// <summary>Checks the client's answer to our challenge. Returns the session key, or null if the user or password is wrong.</summary>
    public static byte[]? Verify(ReadOnlySpan<byte> authenticate, byte[] serverChallenge, string user, string password)
    {
        if (authenticate.Length < 64) return null;
        ReadOnlySpan<byte> Field(ReadOnlySpan<byte> m, int at)
        {
            int length = BinaryPrimitives.ReadUInt16LittleEndian(m[at..]);
            long offset = BinaryPrimitives.ReadUInt32LittleEndian(m[(at + 4)..]);
            return offset + length <= m.Length ? m.Slice((int)offset, length) : default;
        }
        var ntResponse = Field(authenticate, 20);
        string domain = Encoding.Unicode.GetString(Field(authenticate, 28));
        string sentUser = Encoding.Unicode.GetString(Field(authenticate, 36));
        var encryptedKey = Field(authenticate, 52);
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(authenticate[60..]);
        if (ntResponse.Length < 48 || !string.Equals(sentUser, user, StringComparison.OrdinalIgnoreCase)) return null;

        byte[] ntHash = Md4(Encoding.Unicode.GetBytes(password));
        byte[] v2Hash = HMACMD5.HashData(ntHash, Encoding.Unicode.GetBytes(sentUser.ToUpperInvariant() + domain));
        var proof = ntResponse[..16];
        var toSign = new byte[8 + ntResponse.Length - 16];
        serverChallenge.CopyTo(toSign, 0);
        ntResponse[16..].CopyTo(toSign.AsSpan(8));
        if (!CryptographicOperations.FixedTimeEquals(HMACMD5.HashData(v2Hash, toSign), proof)) return null;

        byte[] baseKey = HMACMD5.HashData(v2Hash, proof);
        if ((flags & FlagKeyExchange) != 0 && encryptedKey.Length == 16) return Rc4(baseKey, encryptedKey);
        return baseKey;
    }

    private static byte[] Rc4(byte[] key, ReadOnlySpan<byte> data)
    {
        var s = new byte[256];
        for (int i = 0; i < 256; i++) s[i] = (byte)i;
        for (int i = 0, j = 0; i < 256; i++)
        {
            j = (j + s[i] + key[i % key.Length]) & 255;
            (s[i], s[j]) = (s[j], s[i]);
        }
        var output = new byte[data.Length];
        for (int n = 0, i = 0, j = 0; n < data.Length; n++)
        {
            i = (i + 1) & 255;
            j = (j + s[i]) & 255;
            (s[i], s[j]) = (s[j], s[i]);
            output[n] = (byte)(data[n] ^ s[(s[i] + s[j]) & 255]);
        }
        return output;
    }

    /// <summary>MD4, which NTLM still uses to turn a password into a key. Not in .NET, so written here.</summary>
    internal static byte[] Md4(byte[] input)
    {
        int padded = (input.Length + 8) / 64 * 64 + 64;
        var data = new byte[padded];
        input.CopyTo(data, 0);
        data[input.Length] = 0x80;
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(padded - 8), (ulong)input.Length * 8);

        uint a = 0x67452301, b = 0xefcdab89, c = 0x98badcfe, d = 0x10325476;
        Span<uint> x = stackalloc uint[16];
        ReadOnlySpan<int> order2 = stackalloc int[] { 0, 4, 8, 12, 1, 5, 9, 13, 2, 6, 10, 14, 3, 7, 11, 15 };
        ReadOnlySpan<int> order3 = stackalloc int[] { 0, 8, 4, 12, 2, 10, 6, 14, 1, 9, 5, 13, 3, 11, 7, 15 };
        ReadOnlySpan<int> s1 = stackalloc int[] { 3, 7, 11, 19 }, s2 = stackalloc int[] { 3, 5, 9, 13 }, s3 = stackalloc int[] { 3, 9, 11, 15 };
        for (int block = 0; block < padded; block += 64)
        {
            for (int i = 0; i < 16; i++) x[i] = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(block + i * 4));
            uint aa = a, bb = b, cc = c, dd = d;
            for (int i = 0; i < 16; i++)
            {
                uint t = BitOperations.RotateLeft(a + ((b & c) | (~b & d)) + x[i], s1[i % 4]);
                a = d; d = c; c = b; b = t;
            }
            for (int i = 0; i < 16; i++)
            {
                uint t = BitOperations.RotateLeft(a + ((b & c) | (b & d) | (c & d)) + x[order2[i]] + 0x5A827999, s2[i % 4]);
                a = d; d = c; c = b; b = t;
            }
            for (int i = 0; i < 16; i++)
            {
                uint t = BitOperations.RotateLeft(a + (b ^ c ^ d) + x[order3[i]] + 0x6ED9EBA1, s3[i % 4]);
                a = d; d = c; c = b; b = t;
            }
            a += aa; b += bb; c += cc; d += dd;
        }
        var hash = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(hash, a);
        BinaryPrimitives.WriteUInt32LittleEndian(hash.AsSpan(4), b);
        BinaryPrimitives.WriteUInt32LittleEndian(hash.AsSpan(8), c);
        BinaryPrimitives.WriteUInt32LittleEndian(hash.AsSpan(12), d);
        return hash;
    }
}
