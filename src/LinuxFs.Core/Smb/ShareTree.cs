namespace LinuxFs.Core.Smb;

/// <summary>One file or folder as Windows sees it through the share.</summary>
internal sealed class ShareEntry
{
    /// <summary>The name shown to Windows: the Linux name made valid for Windows.</summary>
    public required string Name { get; init; }
    public required FsNode Node { get; init; }
    /// <summary>The chain of folders from the top of the volume down to this entry.</summary>
    public required IReadOnlyList<FsNode> Trail { get; init; }
    public bool IsDirectory => Node.Kind == FsNodeKind.Directory;
    public long Size => IsDirectory ? 0 : Node.Size;
    public long FileId => Node.Id ^ (Node.Volume << 40);
}

/// <summary>
/// Presents a Linux filesystem the way a Windows share needs it: names valid on Windows, lookups that
/// ignore case, symbolic links already followed, and device files left out.
/// </summary>
internal sealed class ShareTree
{
    private readonly Dictionary<(long, long), IReadOnlyList<ShareEntry>> _cache = new();
    private readonly object _lock = new();

    public IFileSystem Fs { get; }
    public string Label { get; }
    public long TotalBytes { get; }
    public ShareEntry Root { get; }
    /// <summary>Set once a Windows client has attached to this share.</summary>
    public volatile bool Connected;

    public ShareTree(IFileSystem fs, string label, long totalBytes)
    {
        Fs = fs; Label = label; TotalBytes = totalBytes;
        Root = new ShareEntry { Name = "", Node = fs.Root, Trail = new[] { fs.Root } };
    }

    public IReadOnlyList<ShareEntry> List(ShareEntry directory)
    {
        var key = (directory.Node.Volume, directory.Node.Id);
        lock (_lock) if (_cache.TryGetValue(key, out var hit)) return hit;

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<ShareEntry>();
        foreach (var node in Fs.ListDirectory(directory.Node))
        {
            FsNode target = node;
            IReadOnlyList<FsNode> trail;
            if (node.Kind == FsNodeKind.Symlink)
            {
                if (node.LinkTarget == null) continue;
                List<FsNode>? resolved;
                try { resolved = FsPath.Resolve(Fs, directory.Trail, node.LinkTarget); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException) { continue; }
                if (resolved == null || resolved.Count == 0) continue;
                target = resolved[^1];
                // A link back to a folder it sits inside would make the tree endless for anything that walks it.
                if (target.Kind == FsNodeKind.Directory && directory.Trail.Any(a => a.Id == target.Id && a.Volume == target.Volume)) continue;
                trail = resolved;
            }
            else trail = directory.Trail.Append(node).ToList();
            if (target.Kind is not (FsNodeKind.Directory or FsNodeKind.File)) continue;
            result.Add(new ShareEntry { Name = Exporter.UniqueName(Exporter.SafeName(node.Name), used), Node = target, Trail = trail });
        }
        lock (_lock)
        {
            if (_cache.Count > 4096) _cache.Clear();
            _cache[key] = result;
        }
        return result;
    }

    /// <summary>Finds an entry by its Windows path. <paramref name="parentMissing"/> tells a missing folder from a missing name.</summary>
    public ShareEntry? Find(string path, out bool parentMissing)
    {
        parentMissing = false;
        var parts = path.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var current = Root;
        for (int i = 0; i < parts.Length; i++)
        {
            if (!current.IsDirectory) { parentMissing = true; return null; }
            var children = List(current);
            var next = children.FirstOrDefault(c => c.Name == parts[i])
                ?? children.FirstOrDefault(c => string.Equals(c.Name, parts[i], StringComparison.OrdinalIgnoreCase));
            if (next == null) { parentMissing = i < parts.Length - 1; return null; }
            current = next;
        }
        return current;
    }
}
