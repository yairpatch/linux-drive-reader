using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace LinuxFs.Core.Devices;

public sealed record DiskInfo(int Number, string Path, string Model, string Serial, long Size, int SectorSize, bool Removable, string Bus, bool IsSystemDisk);

[SupportedOSPlatform("windows")]
internal static class Native
{
    public const uint GenericRead = 0x80000000;
    public const uint ShareAll = 0x7;
    public const uint OpenExisting = 3;
    public const uint IoctlDiskGetDriveGeometryEx = 0x000700A0;
    public const uint IoctlDiskGetLengthInfo = 0x0007405C;
    public const uint IoctlStorageQueryProperty = 0x002D1400;
    public const uint IoctlVolumeGetVolumeDiskExtents = 0x00560000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[]? inBuf, int inSize, byte[]? outBuf, int outSize, out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetFilePointerEx(SafeFileHandle h, long distance, out long newPos, uint method);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ReadFile(SafeFileHandle h, ref byte buffer, int count, out int read, IntPtr overlapped);
}

/// <summary>Lists the physical disks Windows knows about. Works without administrator rights.</summary>
[SupportedOSPlatform("windows")]
public static class DiskEnumerator
{
    public static List<DiskInfo> Enumerate()
    {
        var systemDisks = GetSystemDiskNumbers();
        var result = new List<DiskInfo>();
        int misses = 0;
        for (int i = 0; i < 128 && misses < 16; i++)
        {
            string path = $@"\\.\PhysicalDrive{i}";
            using var h = Native.CreateFileW(path, 0, Native.ShareAll, IntPtr.Zero, Native.OpenExisting, 0, IntPtr.Zero);
            if (h.IsInvalid) { misses++; continue; }
            misses = 0;

            long size = 0; int sector = 512;
            var geo = new byte[256];
            if (Native.DeviceIoControl(h, Native.IoctlDiskGetDriveGeometryEx, null, 0, geo, geo.Length, out _, IntPtr.Zero))
            {
                sector = BinaryPrimitives.ReadInt32LittleEndian(geo.AsSpan(20));
                size = BinaryPrimitives.ReadInt64LittleEndian(geo.AsSpan(24));
                if (sector <= 0) sector = 512;
            }
            if (size <= 0) continue; // empty card reader slot and the like

            string model = "", serial = "", bus = ""; bool removable = false;
            var query = new byte[12];
            var desc = new byte[1024];
            if (Native.DeviceIoControl(h, Native.IoctlStorageQueryProperty, query, query.Length, desc, desc.Length, out int got, IntPtr.Zero) && got >= 32)
            {
                removable = desc[10] != 0;
                string vendor = ReadAscii(desc, BinaryPrimitives.ReadInt32LittleEndian(desc.AsSpan(12)));
                string product = ReadAscii(desc, BinaryPrimitives.ReadInt32LittleEndian(desc.AsSpan(16)));
                serial = ReadAscii(desc, BinaryPrimitives.ReadInt32LittleEndian(desc.AsSpan(24)));
                model = (vendor + " " + product).Trim();
                bus = BinaryPrimitives.ReadInt32LittleEndian(desc.AsSpan(28)) switch
                {
                    1 => "SCSI", 2 => "ATAPI", 3 => "ATA", 7 => "USB", 8 => "RAID", 11 => "SATA",
                    12 => "SD", 13 => "MMC", 14 or 15 => "Virtual", 17 => "NVMe", _ => "",
                };
            }
            if (model.Length == 0) model = "Disk";
            result.Add(new DiskInfo(i, path, model, serial, size, sector, removable, bus, systemDisks.Contains(i)));
        }
        return result;
    }

    private static string ReadAscii(byte[] buf, int offset)
    {
        if (offset <= 0 || offset >= buf.Length) return "";
        int end = Array.IndexOf(buf, (byte)0, offset);
        if (end < 0) end = buf.Length;
        return Encoding.ASCII.GetString(buf, offset, end - offset).Trim();
    }

    private static HashSet<int> GetSystemDiskNumbers()
    {
        var set = new HashSet<int>();
        try
        {
            string? rootPath = System.IO.Path.GetPathRoot(Environment.SystemDirectory);
            if (string.IsNullOrEmpty(rootPath)) return set;
            using var h = Native.CreateFileW($@"\\.\{rootPath[0]}:", 0, Native.ShareAll, IntPtr.Zero, Native.OpenExisting, 0, IntPtr.Zero);
            if (h.IsInvalid) return set;
            var buf = new byte[1024];
            if (!Native.DeviceIoControl(h, Native.IoctlVolumeGetVolumeDiskExtents, null, 0, buf, buf.Length, out _, IntPtr.Zero)) return set;
            int count = BinaryPrimitives.ReadInt32LittleEndian(buf);
            for (int i = 0; i < count && 8 + i * 24 + 4 <= buf.Length; i++)
                set.Add(BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(8 + i * 24)));
        }
        catch { /* best effort */ }
        return set;
    }
}

/// <summary>
/// Raw read-only access to a physical disk. Needs administrator rights.
/// Reads must be sector-aligned, so wrap this in a <see cref="CachedDevice"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PhysicalDiskDevice : IBlockDevice
{
    private readonly SafeFileHandle _handle;
    private readonly object _lock = new();
    public long Length { get; }
    public int SectorSize { get; }

    public PhysicalDiskDevice(string path)
    {
        _handle = Native.CreateFileW(path, Native.GenericRead, Native.ShareAll, IntPtr.Zero, Native.OpenExisting, 0, IntPtr.Zero);
        if (_handle.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            if (err == 5) throw new UnauthorizedAccessException("Administrator rights are needed to read this disk.");
            throw new IOException($"Cannot open {path}: {new Win32Exception(err).Message}");
        }
        var len = new byte[8];
        if (!Native.DeviceIoControl(_handle, Native.IoctlDiskGetLengthInfo, null, 0, len, 8, out _, IntPtr.Zero))
        {
            int err = Marshal.GetLastWin32Error();
            _handle.Dispose();
            throw new IOException($"Cannot read the size of {path}: {new Win32Exception(err).Message}");
        }
        Length = BinaryPrimitives.ReadInt64LittleEndian(len);
        SectorSize = 512;
        var geo = new byte[256];
        if (Native.DeviceIoControl(_handle, Native.IoctlDiskGetDriveGeometryEx, null, 0, geo, geo.Length, out _, IntPtr.Zero))
        {
            int s = BinaryPrimitives.ReadInt32LittleEndian(geo.AsSpan(20));
            if (s > 0) SectorSize = s;
        }
    }

    public void Read(long offset, Span<byte> buffer)
    {
        DeviceGuard.Check(Length, offset, buffer.Length);
        if (offset % SectorSize != 0 || buffer.Length % SectorSize != 0)
            throw new ArgumentException("Raw disk reads must be sector-aligned.");
        lock (_lock)
        {
            while (buffer.Length > 0)
            {
                if (!Native.SetFilePointerEx(_handle, offset, out _, 0))
                    throw new IOException("Disk seek failed: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
                int want = Math.Min(buffer.Length, 4 * 1024 * 1024);
                if (!Native.ReadFile(_handle, ref MemoryMarshal.GetReference(buffer), want, out int got, IntPtr.Zero))
                    throw new IOException($"Disk read failed at offset {offset}: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
                if (got <= 0) throw new EndOfStreamException("Unexpected end of disk.");
                buffer = buffer[got..];
                offset += got;
            }
        }
    }

    public void Dispose() => _handle.Dispose();
}
