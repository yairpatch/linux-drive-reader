using LinuxFs.Core.Detection;
using LinuxFs.Core.Devices;
using LinuxFs.Core.Btrfs;
using LinuxFs.Core.Ext;
using LinuxFs.Core.Partitions;

namespace LinuxFs.Core;

public enum FsNodeKind { Directory, File, Symlink, Other }

/// <summary>One file, folder or link inside a filesystem.</summary>
public sealed record FsNode
{
    public required string Name { get; init; }
    public required FsNodeKind Kind { get; init; }
    public required long Id { get; init; }
    /// <summary>Which subvolume the entry belongs to, for filesystems that have them (btrfs).</summary>
    public long Volume { get; init; }
    public long Size { get; init; }
    public DateTime? ModifiedUtc { get; init; }
    public uint Mode { get; init; }
    public uint Uid { get; init; }
    public uint Gid { get; init; }
    public string? LinkTarget { get; init; }
    /// <summary>Set when the entry itself could not be read.</summary>
    public string? Error { get; init; }

    public string TypeDescription => Kind switch
    {
        FsNodeKind.Directory => "Folder",
        FsNodeKind.File => "File",
        FsNodeKind.Symlink => "Link",
        _ => (Mode & 0xF000) switch
        {
            0x1000 => "Named pipe",
            0x2000 => "Character device",
            0x6000 => "Block device",
            0xC000 => "Socket",
            _ => "Special",
        },
    };

    /// <summary>Unix-style permissions, e.g. drwxr-xr-x.</summary>
    public string Permissions
    {
        get
        {
            Span<char> c = stackalloc char[10];
            c[0] = (Mode & 0xF000) switch { 0x4000 => 'd', 0xA000 => 'l', 0x2000 => 'c', 0x6000 => 'b', 0x1000 => 'p', 0xC000 => 's', _ => '-' };
            const string rwx = "rwxrwxrwx";
            for (int i = 0; i < 9; i++) c[i + 1] = (Mode & (0x100u >> i)) != 0 ? rwx[i] : '-';
            if ((Mode & 0x800) != 0) c[3] = c[3] == 'x' ? 's' : 'S';
            if ((Mode & 0x400) != 0) c[6] = c[6] == 'x' ? 's' : 'S';
            if ((Mode & 0x200) != 0) c[9] = c[9] == 'x' ? 't' : 'T';
            return new string(c);
        }
    }
}

/// <summary>A read-only view of one filesystem. Safe to call from several threads.</summary>
public interface IFileSystem : IDisposable
{
    string TypeName { get; }
    string Label { get; }
    /// <summary>Things the user should know, e.g. the filesystem was not cleanly unmounted.</summary>
    IReadOnlyList<string> Warnings { get; }
    FsNode Root { get; }
    IReadOnlyList<FsNode> ListDirectory(FsNode directory);
    Stream OpenFile(FsNode file);
}

public static class FileSystems
{
    /// <summary>Opens the filesystem on a volume, or throws NotSupportedException.</summary>
    public static IFileSystem Open(IBlockDevice volume)
    {
        var detected = FsDetector.Detect(volume);
        if (detected.Type is "ext2" or "ext3" or "ext4") return new ExtFileSystem(volume);
        if (detected.Type == "btrfs") return new BtrfsFileSystem(volume);
        throw new NotSupportedException(detected.Type == FsDetector.Unknown
            ? "No known filesystem was found on this volume."
            : $"{detected.Type} is not supported yet.");
    }
}

public sealed record VolumeInfo(PartitionInfo Partition, DetectedFs FileSystem);

public static class VolumeScanner
{
    /// <summary>Lists the partitions of a disk and what each one contains.</summary>
    public static List<VolumeInfo> Scan(IBlockDevice disk)
    {
        var result = new List<VolumeInfo>();
        foreach (var p in PartitionTable.Read(disk))
        {
            DetectedFs fs;
            try { using var sub = new SubDevice(disk, p.Offset, p.Length); fs = FsDetector.Detect(sub); }
            catch (Exception ex) when (ex is IOException or ArgumentException) { fs = new DetectedFs(FsDetector.Unknown, null, false, ex.Message); }
            result.Add(new VolumeInfo(p, fs));
        }
        return result;
    }
}

public static class FsPath
{
    /// <summary>
    /// Follows a path (as found in a symbolic link) starting from a folder.
    /// <paramref name="trail"/> is the chain of folders from the root to the starting folder.
    /// Returns the chain from the root to the target, or null if it does not exist.
    /// </summary>
    public static List<FsNode>? Resolve(IFileSystem fs, IReadOnlyList<FsNode> trail, string path, int depth = 0)
    {
        if (depth > 40 || trail.Count == 0) return null;
        var current = path.StartsWith('/') ? new List<FsNode> { trail[0] } : new List<FsNode>(trail);
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..") { if (current.Count > 1) current.RemoveAt(current.Count - 1); continue; }
            var here = current[^1];
            if (here.Kind != FsNodeKind.Directory) return null;
            var child = fs.ListDirectory(here).FirstOrDefault(n => n.Name == part);
            if (child == null) return null;
            if (child.Kind == FsNodeKind.Symlink)
            {
                if (child.LinkTarget == null) return null;
                var resolved = Resolve(fs, current, child.LinkTarget, depth + 1);
                if (resolved == null) return null;
                current = resolved;
            }
            else current.Add(child);
        }
        return current;
    }
}
