namespace LinuxFs.Core;

public sealed record ExportProgress(string CurrentPath, long BytesCopied, int FilesCopied);

public sealed class ExportResult
{
    public int Files { get; internal set; }
    public int Folders { get; internal set; }
    public long Bytes { get; internal set; }
    /// <summary>Items left out on purpose (links, device files).</summary>
    public List<string> Skipped { get; } = new();
    /// <summary>Items that failed to copy.</summary>
    public List<string> Errors { get; } = new();
    /// <summary>Items whose name had to change to be valid on Windows: Linux path and the new name.</summary>
    public List<(string Original, string SavedAs)> Renamed { get; } = new();
}

/// <summary>Copies files and folders out of a Linux filesystem into a Windows folder.</summary>
public static class Exporter
{
    public static ExportResult Export(IFileSystem fs, IReadOnlyList<FsNode> nodes, string destination,
        IProgress<ExportProgress>? progress = null, CancellationToken ct = default, bool preserveTimes = true)
    {
        Directory.CreateDirectory(destination);
        var result = new ExportResult();
        // Never overwrite what is already in the destination folder.
        var used = new HashSet<string>(Directory.EnumerateFileSystemEntries(destination).Select(p => Path.GetFileName(p)!), StringComparer.OrdinalIgnoreCase);
        var buffer = new byte[1024 * 1024];
        foreach (var node in nodes) ExportNode(fs, node, destination, "/" + node.Name, used, result, buffer, progress, ct, 0, preserveTimes);
        return result;
    }

    private static void ExportNode(IFileSystem fs, FsNode node, string destDir, string linuxPath, HashSet<string> used,
        ExportResult result, byte[] buffer, IProgress<ExportProgress>? progress, CancellationToken ct, int depth, bool preserveTimes)
    {
        ct.ThrowIfCancellationRequested();
        if (node.Kind == FsNodeKind.Symlink) { result.Skipped.Add($"{linuxPath} (link to {node.LinkTarget ?? "?"})"); return; }
        if (node.Kind == FsNodeKind.Other) { result.Skipped.Add($"{linuxPath} ({node.Error ?? node.TypeDescription.ToLowerInvariant()})"); return; }
        if (depth > 256) { result.Errors.Add($"{linuxPath}: folders nested too deeply"); return; }

        string name = UniqueName(SafeName(node.Name), used);
        if (name != node.Name) result.Renamed.Add((linuxPath, name));
        string target = Path.Combine(destDir, name);

        if (node.Kind == FsNodeKind.Directory)
        {
            IReadOnlyList<FsNode> children;
            try
            {
                children = fs.ListDirectory(node);
                Directory.CreateDirectory(target);
            }
            catch (Exception ex) when (IsItemFailure(ex)) { result.Errors.Add($"{linuxPath}: {ex.Message}"); return; }
            result.Folders++;
            var childNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var child in children)
                ExportNode(fs, child, target, linuxPath + "/" + child.Name, childNames, result, buffer, progress, ct, depth + 1, preserveTimes);
            if (preserveTimes) TrySetTime(() => Directory.SetLastWriteTimeUtc(target, node.ModifiedUtc!.Value), node);
            return;
        }

        try
        {
            using (var source = fs.OpenFile(node))
            using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.SequentialScan))
            {
                int n;
                while ((n = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    output.Write(buffer, 0, n);
                    result.Bytes += n;
                    progress?.Report(new ExportProgress(linuxPath, result.Bytes, result.Files));
                }
            }
            if (preserveTimes) TrySetTime(() => File.SetLastWriteTimeUtc(target, node.ModifiedUtc!.Value), node);
            result.Files++;
            progress?.Report(new ExportProgress(linuxPath, result.Bytes, result.Files));
        }
        catch (OperationCanceledException) { TryDelete(target); throw; }
        catch (Exception ex) when (IsItemFailure(ex)) { TryDelete(target); result.Errors.Add($"{linuxPath}: {ex.Message}"); }
    }

    private static bool IsItemFailure(Exception ex) =>
        ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException or ArgumentException;

    private static void TrySetTime(Action set, FsNode node)
    {
        if (node.ModifiedUtc is not { } t || t.Year < 1981) return;
        try { set(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Turns a Linux file name into one Windows accepts.</summary>
    public static string SafeName(string name)
    {
        var chars = name.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            if (chars[i] < 32 || chars[i] is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*') chars[i] = '_';
        string safe = new(chars);
        if (safe.EndsWith('.') || safe.EndsWith(' ')) safe += "_";
        int dot = safe.IndexOf('.');
        if (Reserved.Contains(dot < 0 ? safe : safe[..dot])) safe = "_" + safe;
        if (safe.Length == 0) safe = "_";
        if (safe.Length > 240) safe = safe[..240];
        return safe;
    }

    private static string UniqueName(string name, HashSet<string> used)
    {
        if (used.Add(name)) return name;
        string stem = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
        for (int i = 2; ; i++)
        {
            string candidate = $"{stem} ({i}){ext}";
            if (used.Add(candidate)) return candidate;
        }
    }
}
