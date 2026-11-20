using System.Buffers.Binary;
using System.Text;
using LinuxFs.Core.Devices;

namespace LinuxFs.Core.Detection;

public sealed record DetectedFs(string Type, string? Label, bool Supported, string? Note = null);

/// <summary>Recognises what is on a volume by its on-disk signature.</summary>
public static class FsDetector
{
    public const string Unknown = "Unknown";

    public static DetectedFs Detect(IBlockDevice dev)
    {
        int n = (int)Math.Min(dev.Length, 0x12000);
        if (n < 512) return new DetectedFs(Unknown, null, false);
        var b = new byte[n];
        try { dev.Read(0, b); }
        catch (IOException) { return new DetectedFs(Unknown, null, false, "Could not read the start of the volume."); }

        if (Has(b, 0, new byte[] { (byte)'L', (byte)'U', (byte)'K', (byte)'S', 0xBA, 0xBE }))
            return No("LUKS", "Encrypted volume (LUKS)");
        for (int s = 0; s < 4; s++)
            if (Has(b, s * 512, "LABELONE"u8)) return No("LVM", "LVM physical volume");

        if (n >= 2048 && U16(b, 1024 + 0x38) == 0xEF53)
        {
            uint compat = U32(b, 1024 + 0x5C), incompat = U32(b, 1024 + 0x60), ro = U32(b, 1024 + 0x64);
            string label = Text(b, 1024 + 0x78, 16);
            if ((incompat & 0x8) != 0) return new DetectedFs("ext journal", label, false, "External journal device");
            return new DetectedFs(ExtFlavor(compat, incompat, ro), label, true);
        }
        if (Has(b, 0x10040, "_BHRfS_M"u8))
        {
            string label = Text(b, 0x10000 + 0x12B, 256);
            return new DetectedFs("btrfs", label.Length > 0 ? label : null, true);
        }
        if (Has(b, 0, "XFSB"u8)) return No("xfs", null, Text(b, 108, 12));
        if (n >= 1028 && U32(b, 1024) == 0xF2F52010) return No("f2fs");
        if (Has(b, 4096 + 24, new byte[] { 0xC6, 0x85, 0x73, 0xF6, 0x4E, 0x1A, 0x45, 0xCA, 0x82, 0x65, 0xF5, 0x7F, 0x48, 0xBA, 0x6D, 0x81 })) return No("bcachefs");
        if (Has(b, 0x10034, "ReIsEr"u8)) return No("reiserfs");
        if (Has(b, 0x8000, "JFS1"u8)) return No("jfs");
        if (n >= 1028 && U32(b, 1024) == 0xE0F5E1E2) return No("erofs");
        if (n >= 1032 && U16(b, 1024 + 6) == 0x3434) return No("nilfs2");
        if (Has(b, 0, "hsqs"u8)) return No("squashfs");
        if (Has(b, 0x8001, "CD001"u8)) return No("iso9660");
        if (Has(b, 1024, "H+"u8) || Has(b, 1024, "HX"u8)) return No("hfs+");
        if (Has(b, 32, "NXSB"u8)) return No("apfs");
        if (n >= 4100 && (U32(b, 4096) == 0xA92B4EFC || U32(b, 0) == 0xA92B4EFC)) return No("Linux RAID", "Software RAID member");
        foreach (int page in new[] { 4096, 16384, 65536 })
            if (Has(b, page - 10, "SWAPSPACE2"u8) || Has(b, page - 10, "SWAP-SPACE"u8)) return No("swap", "Linux swap space");

        if (b[510] == 0x55 && b[511] == 0xAA)
        {
            if (Has(b, 3, "NTFS    "u8)) return No("NTFS", "Windows filesystem");
            if (Has(b, 3, "EXFAT   "u8)) return No("exFAT", "Windows filesystem");
            if (Has(b, 82, "FAT32"u8) || Has(b, 54, "FAT1"u8)) return No("FAT", "Windows filesystem");
        }
        return new DetectedFs(Unknown, null, false);
    }

    private static DetectedFs No(string type, string? note = null, string? label = null) =>
        new(type, string.IsNullOrEmpty(label) ? null : label, false, note ?? "Not supported yet");

    private static string ExtFlavor(uint compat, uint incompat, uint ro)
    {
        const uint ext4Incompat = 0x40 | 0x80 | 0x100 | 0x200 | 0x400 | 0x1000 | 0x2000 | 0x4000 | 0x8000 | 0x10000 | 0x20000;
        const uint ext4Ro = 0x8 | 0x10 | 0x20 | 0x40 | 0x200 | 0x400;
        if ((incompat & ext4Incompat) != 0 || (ro & ext4Ro) != 0) return "ext4";
        return (compat & 0x4) != 0 ? "ext3" : "ext2";
    }

    private static bool Has(byte[] b, int offset, ReadOnlySpan<byte> magic) =>
        offset >= 0 && offset + magic.Length <= b.Length && b.AsSpan(offset, magic.Length).SequenceEqual(magic);
    private static ushort U16(byte[] b, int o) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o));
    private static uint U32(byte[] b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o));
    private static string Text(byte[] b, int offset, int max)
    {
        if (offset + max > b.Length) return "";
        int len = b.AsSpan(offset, max).IndexOf((byte)0);
        if (len < 0) len = max;
        return Encoding.UTF8.GetString(b, offset, len);
    }
}
