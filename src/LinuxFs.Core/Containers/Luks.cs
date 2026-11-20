using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LinuxFs.Core.Crypto;
using LinuxFs.Core.Devices;

namespace LinuxFs.Core.Containers;

public sealed class WrongPasswordException : Exception
{
    public WrongPasswordException() : base("The password is not correct for this encrypted volume.") { }
}

/// <summary>An unlocked LUKS volume: holds the volume key and can decrypt the volume on any handle to the same disk.</summary>
public sealed class LuksKey
{
    private readonly byte[] _key;
    private readonly string _cipher;
    private readonly long _offset, _size;
    private readonly int _sectorSize;
    private readonly long _ivTweak;

    internal LuksKey(byte[] key, string cipher, long offset, long size, int sectorSize, long ivTweak)
    { _key = key; _cipher = cipher; _offset = offset; _size = size; _sectorSize = sectorSize; _ivTweak = ivTweak; }

    /// <summary>Returns the decrypted contents of the volume. Does not take ownership of <paramref name="encrypted"/>.</summary>
    public IBlockDevice Open(IBlockDevice encrypted)
    {
        long size = _size >= 0 ? _size : encrypted.Length - _offset;
        size = size / _sectorSize * _sectorSize;
        if (_offset < 0 || size <= 0 || _offset > encrypted.Length - size) throw new InvalidDataException("The encrypted area lies outside the volume.");
        return new CachedDevice(new DecryptedDevice(encrypted, SectorCipher.Create(_cipher, _key), _offset, size, _sectorSize, _ivTweak));
    }

    private sealed class DecryptedDevice : IBlockDevice
    {
        private readonly IBlockDevice _inner; private readonly SectorCipher _cipher;
        private readonly long _offset, _ivTweak; private readonly int _unit;
        public long Length { get; }
        public int SectorSize => _unit;

        public DecryptedDevice(IBlockDevice inner, SectorCipher cipher, long offset, long length, int unit, long ivTweak)
        { _inner = inner; _cipher = cipher; _offset = offset; Length = length; _unit = unit; _ivTweak = ivTweak; }

        public void Read(long offset, Span<byte> buffer)
        {
            if (offset < 0 || offset > Length - buffer.Length) throw new IOException("Read outside the encrypted volume.");
            long from = offset / _unit * _unit, to = (offset + buffer.Length + _unit - 1) / _unit * _unit;
            if (from == offset && to == offset + buffer.Length)
            {
                _inner.Read(_offset + from, buffer);
                _cipher.Decrypt(buffer, _ivTweak + from / 512, _unit);
                return;
            }
            var whole = new byte[to - from];
            _inner.Read(_offset + from, whole);
            _cipher.Decrypt(whole, _ivTweak + from / 512, _unit);
            whole.AsSpan((int)(offset - from), buffer.Length).CopyTo(buffer);
        }

        public void Dispose() => _cipher.Dispose();
    }
}

/// <summary>A LUKS1 or LUKS2 encrypted volume, before it is unlocked.</summary>
public sealed class LuksVolume
{
    private sealed class Slot
    {
        public string Id = "";
        public string Kdf = "pbkdf2", Hash = "sha256", AfHash = "sha256", Cipher = "";
        public int Iterations, Time, MemoryKiB, Lanes, KeySize, Stripes;
        public byte[] Salt = Array.Empty<byte>();
        public long AreaOffset;
    }
    private sealed class Digest
    {
        public List<string> Slots = new();
        public string Hash = "sha256";
        public int Iterations;
        public byte[] Salt = Array.Empty<byte>(), Value = Array.Empty<byte>();
    }

    private readonly IBlockDevice _device;
    private readonly List<Slot> _slots = new();
    private readonly List<Digest> _digests = new();
    private string _cipher = "";
    private long _dataOffset, _dataSize = -1, _ivTweak;
    private int _sectorSize = 512;

    public int Version { get; private set; }
    public string Cipher => _cipher;
    public string? Label { get; private set; }
    /// <summary>False when the volume can only be unlocked by a hardware token or TPM, not a password.</summary>
    public bool HasPasswordSlots => _slots.Count > 0;

    private LuksVolume(IBlockDevice device) => _device = device;

    public static bool IsLuks(IBlockDevice device)
    {
        if (device.Length < 4096) return false;
        var b = new byte[8];
        device.Read(0, b);
        return b[0] == 'L' && b[1] == 'U' && b[2] == 'K' && b[3] == 'S' && b[4] == 0xBA && b[5] == 0xBE;
    }

    public static LuksVolume Open(IBlockDevice device)
    {
        if (!IsLuks(device)) throw new InvalidDataException("Not a LUKS volume.");
        var header = new byte[4096];
        device.Read(0, header);
        var v = new LuksVolume(device) { Version = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(6)) };
        if (v.Version == 1) v.ParseV1(header);
        else if (v.Version == 2) v.ParseV2(header);
        else throw new NotSupportedException($"LUKS version {v.Version} is not supported.");
        return v;
    }

    private void ParseV1(byte[] h)
    {
        string name = Ascii(h, 8, 32), mode = Ascii(h, 40, 32), hash = Ascii(h, 72, 32);
        _cipher = name + "-" + mode;
        _dataOffset = BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(104)) * 512L;
        int keyBytes = (int)BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(108));
        if (keyBytes is < 16 or > 64) throw new InvalidDataException("The LUKS header is damaged.");
        _digests.Add(new Digest
        {
            Hash = hash, Value = h.AsSpan(112, 20).ToArray(), Salt = h.AsSpan(132, 32).ToArray(),
            Iterations = (int)BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(164)),
        });
        for (int i = 0; i < 8; i++)
        {
            var s = h.AsSpan(208 + i * 48, 48);
            if (BinaryPrimitives.ReadUInt32BigEndian(s) != 0x00AC71F3) continue;
            _slots.Add(new Slot
            {
                Id = i.ToString(), Kdf = "pbkdf2", Hash = hash, AfHash = hash, Cipher = _cipher, KeySize = keyBytes,
                Iterations = (int)BinaryPrimitives.ReadUInt32BigEndian(s[4..]), Salt = s.Slice(8, 32).ToArray(),
                AreaOffset = BinaryPrimitives.ReadUInt32BigEndian(s[40..]) * 512L, Stripes = (int)BinaryPrimitives.ReadUInt32BigEndian(s[44..]),
            });
            _digests[0].Slots.Add(i.ToString());
        }
    }

    private void ParseV2(byte[] h)
    {
        long headerSize = (long)BinaryPrimitives.ReadUInt64BigEndian(h.AsSpan(8));
        if (headerSize < 16384 || headerSize > 4 * 1024 * 1024 || headerSize > _device.Length) throw new InvalidDataException("The LUKS2 header is damaged.");
        string label = Ascii(h, 24, 48);
        Label = label.Length > 0 ? label : null;
        var json = new byte[headerSize - 4096];
        _device.Read(4096, json);
        int end = Array.IndexOf(json, (byte)0);
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json.AsMemory(0, end < 0 ? json.Length : end)); }
        catch (JsonException) { throw new InvalidDataException("The LUKS2 header is damaged."); }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.TryGetProperty("config", out var config) && config.TryGetProperty("requirements", out var req) &&
                req.TryGetProperty("mandatory", out var mandatory) && mandatory.GetArrayLength() > 0)
                throw new NotSupportedException("This LUKS2 volume needs a feature that is not supported (" +
                    string.Join(", ", mandatory.EnumerateArray().Select(m => m.GetString())) + ").");

            var segments = root.GetProperty("segments").EnumerateObject().ToList();
            if (segments.Count != 1 || Text(segments[0].Value, "type") != "crypt")
                throw new NotSupportedException("This LUKS2 volume has an unusual layout (for example, re-encryption in progress) that is not supported.");
            var seg = segments[0].Value;
            if (seg.TryGetProperty("integrity", out _)) throw new NotSupportedException("LUKS2 volumes with integrity protection are not supported.");
            _cipher = Text(seg, "encryption");
            _dataOffset = long.Parse(Text(seg, "offset"));
            string size = Text(seg, "size");
            _dataSize = size == "dynamic" ? -1 : long.Parse(size);
            _ivTweak = long.Parse(Text(seg, "iv_tweak"));
            _sectorSize = seg.GetProperty("sector_size").GetInt32();
            if (_sectorSize is not (512 or 1024 or 2048 or 4096)) throw new InvalidDataException("The LUKS2 header is damaged.");

            foreach (var d in root.GetProperty("digests").EnumerateObject())
            {
                if (Text(d.Value, "type") != "pbkdf2") continue;
                if (!d.Value.GetProperty("segments").EnumerateArray().Any(s => s.GetString() == segments[0].Name)) continue;
                _digests.Add(new Digest
                {
                    Slots = d.Value.GetProperty("keyslots").EnumerateArray().Select(s => s.GetString()!).ToList(),
                    Hash = Text(d.Value, "hash"), Iterations = d.Value.GetProperty("iterations").GetInt32(),
                    Salt = Convert.FromBase64String(Text(d.Value, "salt")), Value = Convert.FromBase64String(Text(d.Value, "digest")),
                });
            }

            foreach (var k in root.GetProperty("keyslots").EnumerateObject())
            {
                var e = k.Value;
                if (Text(e, "type") != "luks2") continue;
                var af = e.GetProperty("af"); var area = e.GetProperty("area"); var kdf = e.GetProperty("kdf");
                if (Text(af, "type") != "luks1" || Text(area, "type") != "raw") continue;
                var slot = new Slot
                {
                    Id = k.Name, Kdf = Text(kdf, "type"), Salt = Convert.FromBase64String(Text(kdf, "salt")),
                    KeySize = e.GetProperty("key_size").GetInt32(), Stripes = af.GetProperty("stripes").GetInt32(), AfHash = Text(af, "hash"),
                    Cipher = Text(area, "encryption"), AreaOffset = long.Parse(Text(area, "offset")),
                };
                if (slot.Kdf == "pbkdf2") { slot.Hash = Text(kdf, "hash"); slot.Iterations = kdf.GetProperty("iterations").GetInt32(); }
                else
                {
                    slot.Time = kdf.GetProperty("time").GetInt32();
                    slot.MemoryKiB = kdf.GetProperty("memory").GetInt32();
                    slot.Lanes = kdf.GetProperty("cpus").GetInt32();
                }
                if (slot.KeySize is < 16 or > 64 || slot.Stripes is < 1 or > 100000) continue;
                _slots.Add(slot);
            }
        }
        if (_digests.Count == 0) throw new InvalidDataException("The LUKS2 header has no usable key digest.");
    }

    private static string Text(JsonElement e, string name) => e.GetProperty(name).GetString() ?? "";
    private static string Ascii(byte[] b, int offset, int max)
    {
        int n = Array.IndexOf(b, (byte)0, offset, max);
        return Encoding.ASCII.GetString(b, offset, n < 0 ? max : n - offset);
    }

    private static HashAlgorithmName HashName(string name) => name.ToLowerInvariant() switch
    {
        "sha1" => HashAlgorithmName.SHA1,
        "sha256" => HashAlgorithmName.SHA256,
        "sha384" => HashAlgorithmName.SHA384,
        "sha512" => HashAlgorithmName.SHA512,
        _ => throw new NotSupportedException($"The hash '{name}' used by this encrypted volume is not supported."),
    };

    /// <summary>
    /// Tries the password against every key slot. This is deliberately slow (up to several seconds and
    /// around 1 GB of memory for a default LUKS2 volume): that is how LUKS resists password guessing.
    /// </summary>
    public LuksKey Unlock(string password)
    {
        if (_slots.Count == 0) throw new NotSupportedException("This encrypted volume has no password slot (it may be unlocked by a TPM or hardware token only).");
        byte[] pw = Encoding.UTF8.GetBytes(password);
        Exception? unsupported = null;
        foreach (var slot in _slots)
        {
            byte[] candidate;
            try
            {
                byte[] derived = slot.Kdf switch
                {
                    "pbkdf2" => Rfc2898DeriveBytes.Pbkdf2(pw, slot.Salt, slot.Iterations, HashName(slot.Hash), slot.KeySize),
                    "argon2i" => Argon2.Hash(Argon2.TypeI, pw, slot.Salt, slot.Time, slot.MemoryKiB, slot.Lanes, slot.KeySize),
                    "argon2id" => Argon2.Hash(Argon2.TypeId, pw, slot.Salt, slot.Time, slot.MemoryKiB, slot.Lanes, slot.KeySize),
                    _ => throw new NotSupportedException($"The key derivation '{slot.Kdf}' is not supported."),
                };
                int length = slot.KeySize * slot.Stripes;
                var material = new byte[(length + 511) / 512 * 512];
                _device.Read(slot.AreaOffset, material);
                using (var cipher = SectorCipher.Create(slot.Cipher, derived)) cipher.Decrypt(material, 0, 512);
                candidate = AntiForensicMerge(material, slot.KeySize, slot.Stripes, HashName(slot.AfHash));
            }
            catch (NotSupportedException ex) { unsupported = ex; continue; }
            catch (OutOfMemoryException)
            {
                unsupported = new NotSupportedException($"Unlocking this volume needs about {slot.MemoryKiB / 1024} MB of free memory, which is not available.");
                continue;
            }

            foreach (var digest in _digests.Where(d => d.Slots.Contains(slot.Id)))
            {
                byte[] check = Rfc2898DeriveBytes.Pbkdf2(candidate, digest.Salt, digest.Iterations, HashName(digest.Hash), digest.Value.Length);
                if (CryptographicOperations.FixedTimeEquals(check, digest.Value))
                    return new LuksKey(candidate, _cipher, _dataOffset, _dataSize, _sectorSize, _ivTweak);
            }
        }
        if (unsupported != null) throw unsupported;
        throw new WrongPasswordException();
    }

    /// <summary>LUKS stores each key spread over thousands of "stripes" so that erasing any part destroys it.</summary>
    private static byte[] AntiForensicMerge(byte[] material, int keySize, int stripes, HashAlgorithmName hash)
    {
        var d = new byte[keySize];
        for (int s = 0; s < stripes - 1; s++)
        {
            for (int i = 0; i < keySize; i++) d[i] ^= material[s * keySize + i];
            d = Diffuse(d, hash);
        }
        for (int i = 0; i < keySize; i++) d[i] ^= material[(stripes - 1) * keySize + i];
        return d;
    }

    private static byte[] Diffuse(byte[] data, HashAlgorithmName hash)
    {
        using var h = IncrementalHash.CreateHash(hash);
        int size = h.HashLengthInBytes;
        var output = new byte[data.Length];
        Span<byte> counter = stackalloc byte[4], digest = stackalloc byte[64];
        for (int block = 0, offset = 0; offset < data.Length; block++, offset += size)
        {
            int n = Math.Min(size, data.Length - offset);
            BinaryPrimitives.WriteInt32BigEndian(counter, block);
            h.AppendData(counter);
            h.AppendData(data.AsSpan(offset, n));
            h.GetHashAndReset(digest);
            digest[..n].CopyTo(output.AsSpan(offset));
        }
        return output;
    }
}
