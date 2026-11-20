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
    /// <summary>Stays the same when the disk list is rebuilt, so mounted drives can be matched up again.</summary>
    public required string Id { get; init; }
    public bool Removable { get; init; }
    public bool IsSelected { get; set; }
    public string Icon => ImagePath != null ? "" : Removable ? "" : "";
    public ObservableCollection<object> Volumes { get; } = new();
}

/// <summary>
/// Anything that can sit under a disk: a partition, an LVM logical volume, or the unlocked inside of an
/// encrypted volume. Containers (LVM, LUKS) list what they hold as children.
/// </summary>
public sealed class VolumeItem : System.ComponentModel.INotifyPropertyChanged
{
    public required DiskItem Disk { get; init; }
    /// <summary>"Partition 2", "Volume vg/root", "Unlocked contents"…</summary>
    public required string Name { get; init; }
    /// <summary>Disk id plus the names leading here; identifies the same volume across rescans.</summary>
    public required string Location { get; init; }
    public required long Size { get; init; }
    public required LinuxFs.Core.Detection.DetectedFs Fs { get; init; }
    public string PartitionType { get; init; } = "";
    public string? PartitionName { get; init; }
    /// <summary>Turns a handle to the whole disk into this volume's contents.</summary>
    public required Func<IBlockDevice, IBlockDevice> Map { get; init; }
    public ObservableCollection<object> Children { get; } = new();

    private LinuxFs.Core.Containers.LuksKey? _key;
    /// <summary>Set once an encrypted volume has been unlocked.</summary>
    public LinuxFs.Core.Containers.LuksKey? Key
    {
        get => _key;
        set { _key = value; PropertyChanged?.Invoke(this, new(nameof(Detail))); PropertyChanged?.Invoke(this, new(nameof(Icon))); }
    }

    private bool _selected;
    /// <summary>Mirrors the highlight in the disk list, so the app can highlight what it opened itself.</summary>
    public bool IsSelected
    {
        get => _selected;
        set { if (_selected != value) { _selected = value; PropertyChanged?.Invoke(this, new(nameof(IsSelected))); } }
    }

    private char? _driveLetter;
    /// <summary>The Windows drive letter this volume is currently mounted as, if any.</summary>
    public char? DriveLetter
    {
        get => _driveLetter;
        set { if (_driveLetter != value) { _driveLetter = value; PropertyChanged?.Invoke(this, new(nameof(Detail))); } }
    }

    public bool IsLuks => Fs.Type == "LUKS";
    public bool IsLvm => Fs.Type == "LVM";
    /// <summary>A filesystem this app can browse.</summary>
    public bool Supported => Fs.Supported;
    public double Opacity => Supported || IsLuks || IsLvm ? 1.0 : 0.55;
    public string Icon => IsLuks ? (Key == null ? "" : "") : IsLvm ? "" : Supported ? "" : "";
    public Brush IconBrush => IsLuks ? EntryRow.Accent : Supported ? EntryRow.FolderBrush : EntryRow.FileBrush;
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public string Title
    {
        get
        {
            string label = Fs.Label ?? PartitionName ?? "";
            return label.Length > 0 ? $"{Name} — {label}" : Name;
        }
    }

    public string Detail
    {
        get
        {
            string size = Format.Size(Size);
            if (IsLuks) return $"Encrypted (LUKS) · {size} · " + (Key == null ? "select to unlock" : "unlocked");
            if (IsLvm) return $"LVM volume group · {size}";
            bool unknown = Fs.Type == LinuxFs.Core.Detection.FsDetector.Unknown;
            string type = unknown && PartitionType.Length > 0 ? PartitionType : Fs.Type;
            string note = Fs.Supported ? "" : unknown ? " · no known filesystem" : $" · {Fs.Note ?? "not supported yet"}";
            return $"{type} · {size}{note}" + (DriveLetter is { } d ? $" · drive {d}:" : "");
        }
    }

    /// <summary>This volume and everything inside it that can be browsed.</summary>
    public IEnumerable<VolumeItem> Browsable() =>
        (Supported ? new[] { this } : Array.Empty<VolumeItem>()).Concat(Children.OfType<VolumeItem>().SelectMany(c => c.Browsable()));
}

public sealed class NoteItem
{
    public required string Text { get; init; }
    public bool IsSelected { get; set; }
}

/// <summary>One row of the file list.</summary>
public sealed class EntryRow
{
    internal static readonly Brush FolderBrush = Frozen(0xE6, 0xA8, 0x17), FileBrush = Frozen(0x8A, 0x8A, 0x8A), LinkBrush = Frozen(0x3B, 0x8E, 0xEA), Accent = Frozen(0x3B, 0x8E, 0xEA);
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
