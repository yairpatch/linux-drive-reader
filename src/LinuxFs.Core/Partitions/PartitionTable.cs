using System.Buffers.Binary;
using System.Text;
using LinuxFs.Core.Detection;
using LinuxFs.Core.Devices;

namespace LinuxFs.Core.Partitions;

/// <param name="Number">Partition number as Linux would name it (sda1, sda5...). 0 = the whole disk.</param>
public sealed record PartitionInfo(int Number, long Offset, long Length, string Scheme, string TypeName, string? Name);

public static class PartitionTable
{
    public static List<PartitionInfo> Read(IBlockDevice dev)
    {
        var gpt = TryReadGpt(dev);
        if (gpt != null) return gpt;

        // A filesystem written straight onto the disk, with no partition table.
        if (FsDetector.Detect(dev).Type != FsDetector.Unknown) return Whole(dev);

        var mbr = TryReadMbr(dev);
        if (mbr != null && mbr.Count > 0) return mbr;
        return Whole(dev);
    }

    private static List<PartitionInfo> Whole(IBlockDevice dev) =>
        new() { new PartitionInfo(0, 0, dev.Length, "None", "Whole disk", null) };

    private static List<PartitionInfo>? TryReadGpt(IBlockDevice dev)
    {
        foreach (int sector in new[] { dev.SectorSize, 512, 4096 }.Distinct())
        {
            if (dev.Length < sector * 3L) continue;
            var hdr = new byte[92];
            dev.Read(sector, hdr);
            if (!hdr.AsSpan(0, 8).SequenceEqual("EFI PART"u8)) continue;

            long entriesLba = BinaryPrimitives.ReadInt64LittleEndian(hdr.AsSpan(72));
            int count = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(80)), 1024);
            int entrySize = BinaryPrimitives.ReadInt32LittleEndian(hdr.AsSpan(84));
            if (entrySize < 128 || entrySize > 4096 || entriesLba <= 0) continue;
            long tableOffset = entriesLba * sector;
            if (tableOffset > dev.Length - (long)count * entrySize) continue;

            var table = new byte[count * entrySize];
            dev.Read(tableOffset, table);
            var list = new List<PartitionInfo>();
            for (int i = 0; i < count; i++)
            {
                var e = table.AsSpan(i * entrySize, entrySize);
                var type = new Guid(e[..16]);
                if (type == Guid.Empty) continue;
                long first = BinaryPrimitives.ReadInt64LittleEndian(e[32..]);
                long last = BinaryPrimitives.ReadInt64LittleEndian(e[40..]);
                if (first <= 0 || last < first) continue;
                long offset = first * sector, length = (last - first + 1) * sector;
                if (offset > dev.Length - length) continue;
                string name = Encoding.Unicode.GetString(e.Slice(56, 72)).TrimEnd('\0');
                int nul = name.IndexOf('\0');
                if (nul >= 0) name = name[..nul];
                list.Add(new PartitionInfo(i + 1, offset, length, "GPT", GptTypeName(type), name.Length > 0 ? name : null));
            }
            return list;
        }
        return null;
    }

    private static List<PartitionInfo>? TryReadMbr(IBlockDevice dev)
    {
        if (dev.Length < 512) return null;
        int sector = dev.SectorSize;
        var s0 = new byte[512];
        dev.Read(0, s0);
        if (s0[510] != 0x55 || s0[511] != 0xAA) return null;

        var list = new List<PartitionInfo>();
        for (int i = 0; i < 4; i++)
        {
            var e = s0.AsSpan(446 + i * 16, 16);
            byte status = e[0], type = e[4];
            long start = BinaryPrimitives.ReadUInt32LittleEndian(e[8..]);
            long size = BinaryPrimitives.ReadUInt32LittleEndian(e[12..]);
            if (status != 0x00 && status != 0x80) return null; // not a partition table
            if (type == 0 || size == 0) continue;
            long offset = start * sector, length = size * sector;
            if (start == 0 || offset >= dev.Length) continue;
            length = Math.Min(length, dev.Length - offset);

            if (type is 0x05 or 0x0F or 0x85)
                ReadLogical(dev, sector, start, list);
            else
                list.Add(new PartitionInfo(i + 1, offset, length, "MBR", MbrTypeName(type), null));
        }
        return list.OrderBy(p => p.Number).ToList();
    }

    private static void ReadLogical(IBlockDevice dev, int sector, long extendedStart, List<PartitionInfo> list)
    {
        long ebr = extendedStart;
        int number = 5;
        var buf = new byte[512];
        for (int guard = 0; guard < 128; guard++)
        {
            long ebrOffset = ebr * sector;
            if (ebrOffset > dev.Length - 512) return;
            dev.Read(ebrOffset, buf);
            if (buf[510] != 0x55 || buf[511] != 0xAA) return;

            var e0 = buf.AsSpan(446, 16);
            byte type = e0[4];
            long start = BinaryPrimitives.ReadUInt32LittleEndian(e0[8..]);
            long size = BinaryPrimitives.ReadUInt32LittleEndian(e0[12..]);
            if (type != 0 && size != 0)
            {
                long offset = (ebr + start) * sector;
                if (offset < dev.Length)
                    list.Add(new PartitionInfo(number++, offset, Math.Min(size * sector, dev.Length - offset), "MBR", MbrTypeName(type), null));
            }

            var e1 = buf.AsSpan(462, 16);
            long next = BinaryPrimitives.ReadUInt32LittleEndian(e1[8..]);
            if (e1[4] == 0 || next == 0) return;
            ebr = extendedStart + next;
        }
    }

    private static string MbrTypeName(byte type) => type switch
    {
        0x83 => "Linux",
        0x82 => "Linux swap",
        0x8E => "Linux LVM",
        0xFD => "Linux RAID",
        0x07 => "NTFS / exFAT",
        0x0B or 0x0C => "FAT32",
        0x01 or 0x04 or 0x06 or 0x0E => "FAT",
        0xEF => "EFI System",
        0xEE => "GPT protective",
        _ => $"Type 0x{type:X2}",
    };

    private static string GptTypeName(Guid g) => g.ToString("D").ToUpperInvariant() switch
    {
        "0FC63DAF-8483-4772-8E79-3D69D8477DE4" => "Linux filesystem",
        "4F68BCE3-E8CD-4DB1-96E7-FBCAF984B709" => "Linux root (x86-64)",
        "933AC7E1-2EB4-4F13-B844-0E14E2AEF915" => "Linux home",
        "0657FD6D-A4AB-43C4-84E5-0933C84B4F4F" => "Linux swap",
        "E6D6D379-F507-44C2-A23C-238F2A3DF928" => "Linux LVM",
        "A19D880F-05FC-4D3B-A006-743F0F84911E" => "Linux RAID",
        "CA7D7CCB-63ED-4C53-861C-1742536059CC" => "Linux LUKS",
        "BC13C2FF-59E6-4262-A352-B275FD6F7172" => "Linux boot",
        "C12A7328-F81F-11D2-BA4B-00A0C93EC93B" => "EFI System",
        "EBD0A0A2-B9E5-4433-87C0-68B6B72699C7" => "Microsoft basic data",
        "E3C9E316-0B5C-4DB8-817D-F92DF00215AE" => "Microsoft reserved",
        "DE94BBA4-06D1-4D40-A16A-BFD50179D6AC" => "Windows recovery",
        "21686148-6449-6E6F-744E-656564454649" => "BIOS boot",
        _ => "Other",
    };
}
