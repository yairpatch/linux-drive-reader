using System.Buffers.Binary;
using System.Security.Cryptography;

namespace LinuxFs.Core.Crypto;

/// <summary>Decrypts disk sectors the way Linux dm-crypt encrypts them.</summary>
internal abstract class SectorCipher : IDisposable
{
    /// <summary>Decrypts whole units in place. <paramref name="firstSector"/> is the 512-byte sector number of the first unit.</summary>
    public abstract void Decrypt(Span<byte> data, long firstSector, int unitSize);
    public abstract void Dispose();

    /// <summary>Builds a cipher from a dm-crypt specification such as "aes-xts-plain64".</summary>
    public static SectorCipher Create(string spec, byte[] key)
    {
        var parts = spec.ToLowerInvariant().Split('-', 3);
        if (parts.Length != 3 || parts[0] != "aes")
            throw new NotSupportedException($"The cipher '{spec}' is not supported. Only AES (aes-xts-plain64, aes-cbc-essiv:sha256) is.");
        string mode = parts[1], iv = parts[2];
        if (mode == "xts" && iv is "plain64" or "plain")
        {
            if (key.Length is not (32 or 64)) throw new NotSupportedException("Unsupported AES-XTS key size.");
            return new Xts(key, iv == "plain");
        }
        if (mode == "cbc" && iv is "essiv:sha256" or "plain" or "plain64")
        {
            if (key.Length is not (16 or 24 or 32)) throw new NotSupportedException("Unsupported AES-CBC key size.");
            return new Cbc(key, iv);
        }
        throw new NotSupportedException($"The cipher mode '{spec}' is not supported.");
    }

    private sealed class Xts : SectorCipher
    {
        private readonly Aes _data = Aes.Create(), _tweak = Aes.Create();
        private readonly bool _narrow;
        private readonly object _lock = new();

        public Xts(byte[] key, bool narrow)
        {
            _data.Key = key.AsSpan(0, key.Length / 2).ToArray();
            _tweak.Key = key.AsSpan(key.Length / 2).ToArray();
            _narrow = narrow;
        }

        public override void Decrypt(Span<byte> data, long firstSector, int unitSize)
        {
            if (data.Length % unitSize != 0 || unitSize % 16 != 0) throw new ArgumentException("Encrypted reads must cover whole sectors.");
            var tweaks = new byte[data.Length];
            var work = new byte[data.Length];
            Span<byte> iv = stackalloc byte[16];
            lock (_lock)
            {
                for (int unit = 0; unit * unitSize < data.Length; unit++)
                {
                    long sector = firstSector + (long)unit * (unitSize / 512);
                    iv.Clear();
                    BinaryPrimitives.WriteUInt64LittleEndian(iv, _narrow ? (uint)sector : (ulong)sector);
                    var t = tweaks.AsSpan(unit * unitSize, unitSize);
                    _tweak.EncryptEcb(iv, t[..16], PaddingMode.None);
                    ulong lo = BinaryPrimitives.ReadUInt64LittleEndian(t), hi = BinaryPrimitives.ReadUInt64LittleEndian(t[8..]);
                    for (int b = 16; b < unitSize; b += 16)
                    {
                        // Multiply the tweak by x in GF(2^128).
                        ulong carry = hi >> 63;
                        hi = (hi << 1) | (lo >> 63);
                        lo = (lo << 1) ^ (carry * 0x87);
                        BinaryPrimitives.WriteUInt64LittleEndian(t[b..], lo);
                        BinaryPrimitives.WriteUInt64LittleEndian(t[(b + 8)..], hi);
                    }
                }
                for (int i = 0; i < data.Length; i++) work[i] = (byte)(data[i] ^ tweaks[i]);
                _data.DecryptEcb(work, data, PaddingMode.None);
                for (int i = 0; i < data.Length; i++) data[i] ^= tweaks[i];
            }
        }

        public override void Dispose() { _data.Dispose(); _tweak.Dispose(); }
    }

    private sealed class Cbc : SectorCipher
    {
        private readonly Aes _data = Aes.Create();
        private readonly Aes? _essiv;
        private readonly bool _narrow;
        private readonly object _lock = new();

        public Cbc(byte[] key, string ivMode)
        {
            _data.Key = key;
            _narrow = ivMode == "plain";
            if (ivMode == "essiv:sha256") { _essiv = Aes.Create(); _essiv.Key = SHA256.HashData(key); }
        }

        public override void Decrypt(Span<byte> data, long firstSector, int unitSize)
        {
            if (data.Length % unitSize != 0 || unitSize % 16 != 0) throw new ArgumentException("Encrypted reads must cover whole sectors.");
            var work = new byte[unitSize];
            Span<byte> iv = stackalloc byte[16], plainIv = stackalloc byte[16];
            lock (_lock)
            {
                for (int offset = 0; offset < data.Length; offset += unitSize)
                {
                    long sector = firstSector + offset / 512;
                    plainIv.Clear();
                    BinaryPrimitives.WriteUInt64LittleEndian(plainIv, _narrow ? (uint)sector : (ulong)sector);
                    if (_essiv != null) _essiv.EncryptEcb(plainIv, iv, PaddingMode.None); else plainIv.CopyTo(iv);
                    var unit = data.Slice(offset, unitSize);
                    _data.DecryptCbc(unit, iv, work, PaddingMode.None);
                    work.CopyTo(unit);
                }
            }
        }

        public override void Dispose() { _data.Dispose(); _essiv?.Dispose(); }
    }
}
