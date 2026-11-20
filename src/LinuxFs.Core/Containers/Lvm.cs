using System.Buffers.Binary;
using System.Text;
using LinuxFs.Core.Devices;

namespace LinuxFs.Core.Containers;

/// <summary>A logical volume inside an LVM volume group.</summary>
public sealed class LvmVolume
{
    internal List<(long VolumeOffset, long Length, long DeviceOffset)> Segments = new();
    public required string Name { get; init; }
    public long Size { get; internal set; }
    /// <summary>False when the volume cannot be read from this disk alone; <see cref="Note"/> says why.</summary>
    public bool Supported { get; internal set; } = true;
    public string? Note { get; internal set; }

    /// <summary>Returns the volume's contents. Does not take ownership of <paramref name="physicalVolume"/>.</summary>
    public IBlockDevice Open(IBlockDevice physicalVolume)
    {
        if (!Supported) throw new NotSupportedException(Note ?? "This logical volume cannot be read.");
        return new MappedDevice(physicalVolume, Segments, Size);
    }

    private sealed class MappedDevice : IBlockDevice
    {
        private readonly IBlockDevice _inner;
        private readonly List<(long VolumeOffset, long Length, long DeviceOffset)> _segments;
        public long Length { get; }
        public int SectorSize => _inner.SectorSize;

        public MappedDevice(IBlockDevice inner, List<(long, long, long)> segments, long length) { _inner = inner; _segments = segments; Length = length; }

        public void Read(long offset, Span<byte> buffer)
        {
            if (offset < 0 || offset > Length - buffer.Length) throw new IOException("Read outside the logical volume. The filesystem may be damaged.");
            while (buffer.Length > 0)
            {
                var s = _segments.LastOrDefault(x => x.VolumeOffset <= offset);
                long within = offset - s.VolumeOffset;
                if (s.Length == 0 || within >= s.Length) throw new IOException("The logical volume has a gap in its layout.");
                int n = (int)Math.Min(buffer.Length, s.Length - within);
                _inner.Read(s.DeviceOffset + within, buffer[..n]);
                buffer = buffer[n..];
                offset += n;
            }
        }

        public void Dispose() { }
    }
}

/// <summary>An LVM2 volume group, read from one of its physical volumes.</summary>
public sealed class LvmGroup
{
    public required string Name { get; init; }
    public required IReadOnlyList<LvmVolume> Volumes { get; init; }

    public static bool IsPhysicalVolume(IBlockDevice device) => FindLabel(device) >= 0;

    private static long FindLabel(IBlockDevice device)
    {
        if (device.Length < 8192) return -1;
        var b = new byte[2048];
        device.Read(0, b);
        for (int s = 0; s < 4; s++)
            if (b.AsSpan(s * 512, 8).SequenceEqual("LABELONE"u8) && b.AsSpan(s * 512 + 24, 8).SequenceEqual("LVM2 001"u8)) return s * 512;
        return -1;
    }

    public static LvmGroup Read(IBlockDevice device)
    {
        long label = FindLabel(device);
        if (label < 0) throw new InvalidDataException("Not an LVM physical volume.");
        var sector = new byte[512];
        device.Read(label, sector);
        int p = (int)BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(20));
        if (p < 32 || p + 40 > 512) throw new InvalidDataException("The LVM label is damaged.");
        string ourId = Encoding.ASCII.GetString(sector, p, 32);
        p += 40;
        // Two zero-terminated lists follow: where the data is, then where the metadata is.
        while (p + 16 <= 512 && BinaryPrimitives.ReadUInt64LittleEndian(sector.AsSpan(p)) != 0) p += 16;
        p += 16;
        var areas = new List<long>();
        while (p + 16 <= 512 && BinaryPrimitives.ReadUInt64LittleEndian(sector.AsSpan(p)) != 0)
        {
            areas.Add((long)BinaryPrimitives.ReadUInt64LittleEndian(sector.AsSpan(p)));
            p += 16;
        }

        foreach (long area in areas)
        {
            string? text = ReadMetadata(device, area);
            if (text == null) continue;
            var config = new ConfigParser(text).ParseDocument();
            var group = config.FirstOrDefault(kv => kv.Value is Dictionary<string, object>);
            if (group.Value is not Dictionary<string, object> vg) continue;
            return Build(group.Key, vg, ourId);
        }
        throw new InvalidDataException("This LVM physical volume has no readable metadata.");
    }

    private static string? ReadMetadata(IBlockDevice device, long area)
    {
        if (area <= 0 || area > device.Length - 512) return null;
        var h = new byte[512];
        device.Read(area, h);
        if (!h.AsSpan(4, 16).SequenceEqual(" LVM2 x[5A%r0N*>"u8)) return null;
        long start = (long)BinaryPrimitives.ReadUInt64LittleEndian(h.AsSpan(24)), size = (long)BinaryPrimitives.ReadUInt64LittleEndian(h.AsSpan(32));
        long offset = (long)BinaryPrimitives.ReadUInt64LittleEndian(h.AsSpan(40)), length = (long)BinaryPrimitives.ReadUInt64LittleEndian(h.AsSpan(48));
        if (length <= 0 || length > 16 * 1024 * 1024 || offset < 512 || offset >= size || start > device.Length - size) return null;
        var text = new byte[length];
        // The metadata area is a ring: text that runs off the end continues just after the header.
        long first = Math.Min(length, size - offset);
        device.Read(start + offset, text.AsSpan(0, (int)first));
        if (first < length) device.Read(start + 512, text.AsSpan((int)first));
        int nul = Array.IndexOf(text, (byte)0);
        return Encoding.UTF8.GetString(text, 0, nul < 0 ? text.Length : nul);
    }

    private static LvmGroup Build(string name, Dictionary<string, object> vg, string ourId)
    {
        long extent = Number(vg, "extent_size") * 512;
        if (extent <= 0) throw new InvalidDataException("The LVM metadata is damaged.");

        string? ourPv = null;
        long peStart = 0;
        if (vg.TryGetValue("physical_volumes", out var pvs) && pvs is Dictionary<string, object> pvMap)
            foreach (var (pvName, value) in pvMap)
                if (value is Dictionary<string, object> pv && pv.TryGetValue("id", out var id) && (id as string ?? "").Replace("-", "") == ourId)
                { ourPv = pvName; peStart = Number(pv, "pe_start") * 512; }

        var volumes = new List<LvmVolume>();
        if (vg.TryGetValue("logical_volumes", out var lvs) && lvs is Dictionary<string, object> lvMap)
            foreach (var (lvName, value) in lvMap)
            {
                if (value is not Dictionary<string, object> lv) continue;
                if (lv.TryGetValue("status", out var status) && status is List<object> flags && !flags.Contains("VISIBLE")) continue;
                var volume = new LvmVolume { Name = lvName };
                foreach (var (key, segValue) in lv)
                {
                    if (!key.StartsWith("segment") || segValue is not Dictionary<string, object> seg) continue;
                    long start = Number(seg, "start_extent") * extent, length = Number(seg, "extent_count") * extent;
                    volume.Size = Math.Max(volume.Size, start + length);
                    string type = seg.TryGetValue("type", out var t) ? t as string ?? "" : "";
                    if (type != "striped" || Number(seg, "stripe_count") != 1 || !seg.TryGetValue("stripes", out var st) || st is not List<object> stripes || stripes.Count != 2)
                    {
                        volume.Supported = false;
                        volume.Note = type is "striped" or "" ? "Striped across several disks; not supported" : $"LVM '{type}' volumes are not supported";
                        continue;
                    }
                    if (ourPv == null || stripes[0] as string != ourPv)
                    {
                        volume.Supported = false;
                        volume.Note ??= "Stored partly on another disk of this volume group";
                        continue;
                    }
                    volume.Segments.Add((start, length, peStart + Convert.ToInt64(stripes[1]) * extent));
                }
                volume.Segments.Sort((a, b) => a.VolumeOffset.CompareTo(b.VolumeOffset));
                volumes.Add(volume);
            }
        return new LvmGroup { Name = name, Volumes = volumes.OrderBy(v => v.Name, StringComparer.Ordinal).ToList() };
    }

    private static long Number(Dictionary<string, object> d, string key) => d.TryGetValue(key, out var v) && v is long n ? n : 0;

    /// <summary>Parses LVM's text metadata: nested "name { ... }" sections holding "key = value" lines.</summary>
    private sealed class ConfigParser
    {
        private readonly string _s;
        private int _p;
        public ConfigParser(string text) => _s = text;

        public Dictionary<string, object> ParseDocument() => ParseSection(topLevel: true, 0);

        private void SkipSpace()
        {
            while (_p < _s.Length)
            {
                if (char.IsWhiteSpace(_s[_p])) _p++;
                else if (_s[_p] == '#') { while (_p < _s.Length && _s[_p] != '\n') _p++; }
                else break;
            }
        }

        private Dictionary<string, object> ParseSection(bool topLevel, int depth)
        {
            if (depth > 16) throw new InvalidDataException("The LVM metadata is damaged.");
            var result = new Dictionary<string, object>();
            while (true)
            {
                SkipSpace();
                if (_p >= _s.Length) { if (topLevel) return result; throw new InvalidDataException("The LVM metadata is damaged."); }
                if (_s[_p] == '}') { _p++; return result; }
                int start = _p;
                while (_p < _s.Length && (char.IsLetterOrDigit(_s[_p]) || _s[_p] is '_' or '-' or '.' or '+')) _p++;
                if (_p == start) throw new InvalidDataException("The LVM metadata is damaged.");
                string name = _s[start.._p];
                SkipSpace();
                if (_p >= _s.Length) throw new InvalidDataException("The LVM metadata is damaged.");
                if (_s[_p] == '{') { _p++; result[name] = ParseSection(false, depth + 1); }
                else if (_s[_p] == '=') { _p++; result[name] = ParseValue(); }
                else throw new InvalidDataException("The LVM metadata is damaged.");
            }
        }

        private object ParseValue()
        {
            SkipSpace();
            if (_p >= _s.Length) throw new InvalidDataException("The LVM metadata is damaged.");
            if (_s[_p] == '[')
            {
                _p++;
                var list = new List<object>();
                while (true)
                {
                    SkipSpace();
                    if (_p >= _s.Length) throw new InvalidDataException("The LVM metadata is damaged.");
                    if (_s[_p] == ']') { _p++; return list; }
                    if (_s[_p] == ',') { _p++; continue; }
                    list.Add(ParseValue());
                }
            }
            if (_s[_p] == '"')
            {
                var sb = new StringBuilder();
                _p++;
                while (_p < _s.Length && _s[_p] != '"')
                {
                    if (_s[_p] == '\\' && _p + 1 < _s.Length) _p++;
                    sb.Append(_s[_p++]);
                }
                _p++;
                return sb.ToString();
            }
            int start = _p;
            while (_p < _s.Length && !char.IsWhiteSpace(_s[_p]) && _s[_p] is not (',' or ']' or '}')) _p++;
            string token = _s[start.._p];
            return long.TryParse(token, out long n) ? n : token;
        }
    }
}
