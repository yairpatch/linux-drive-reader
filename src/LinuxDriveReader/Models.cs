using System.Collections.ObjectModel;
using System.Windows.Media;
using LinuxFs.Core;
using LinuxFs.Core.Devices;

namespace LinuxDriveReader;

/// <summary>A physical disk or a disk image file in the left-hand tree.</summary>
public sealed class DiskItem
{
    public required string Title { get; init; }
    public required string Detail { get; init; }
    public required Func<IBlockDevice> Open { get; init; }
    public string? ImagePath { get; init; }
    public ObservableCollection<object> Volumes { get; } = new();
}

public sealed class VolumeItem
{
    public required DiskItem Disk { get; init; }
    public required VolumeInfo Info { get; init; }
    public bool Supported => Info.FileSystem.Supported;
    public double Opacity => Supported ? 1.0 : 0.55;

    public string Title
    {
        get
        {
            string what = Info.Partition.Number == 0 ? "Whole disk" : $"Partition {Info.Partition.Number}";
            string label = Info.FileSystem.Label ?? Info.Partition.Name ?? "";
            return label.Length > 0 ? $"{what} — {label}" : what;
        }
    }

    public string Detail
    {
        get
        {
            var fs = Info.FileSystem;
            string type = fs.Type == LinuxFs.Core.Detection.FsDetector.Unknown ? Info.Partition.TypeName : fs.Type;
            string note = fs.Supported ? "" : fs.Type == LinuxFs.Core.Detection.FsDetector.Unknown ? " · no known filesystem" : $" · {fs.Note ?? "not supported yet"}";
            return $"{type} · {Format.Size(Info.Partition.Length)}{note}";
        }
    }
}

public sealed class NoteItem
{
    public required string Text { get; init; }
}

/// <summary>One row of the file list.</summary>
public sealed class EntryRow
{
    private static readonly Brush FolderBrush = Frozen(0xE0, 0xA8, 0x00), FileBrush = Frozen(0x60, 0x60, 0x60), LinkBrush = Frozen(0x1E, 0x6F, 0xC8);
    private static Brush Frozen(byte r, byte g, byte b) { var br = new SolidColorBrush(Color.FromRgb(r, g, b)); br.Freeze(); return br; }

    public EntryRow(FsNode node) => Node = node;
    public FsNode Node { get; }
    public string Name => Node.Name;
    public string Icon => Node.Kind switch { FsNodeKind.Directory => "", FsNodeKind.Symlink => "", FsNodeKind.File => "", _ => "" };
    public Brush IconBrush => Node.Kind switch { FsNodeKind.Directory => FolderBrush, FsNodeKind.Symlink => LinkBrush, _ => FileBrush };
    public string SizeText => Node.Kind == FsNodeKind.File ? Format.Size(Node.Size) : "";
    public string ModifiedText => Node.ModifiedUtc is { } t ? t.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "";
    public string TypeText => Node.Error != null ? "Unreadable" : Node.Kind == FsNodeKind.Symlink ? "Link → " + Node.LinkTarget : Node.TypeDescription;
    public string Permissions => Node.Error != null ? "" : Node.Permissions;
}

public static class Format
{
    public static string Size(long bytes)
    {
        string[] units = { "bytes", "KB", "MB", "GB", "TB", "PB" };
        double value = bytes;
        int u = 0;
        while (value >= 1024 && u < units.Length - 1) { value /= 1024; u++; }
        return u == 0 ? $"{bytes} bytes" : $"{value:0.#} {units[u]}";
    }
}
