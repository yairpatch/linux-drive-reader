using System.Globalization;
using System.Security.Cryptography;
using LinuxFs.Core;
using LinuxFs.Core.Detection;
using LinuxFs.Core.Devices;
using LinuxFs.Core.Partitions;

namespace LinuxFs.Tests;

/// <summary>
/// Compares what the reader sees with what Linux itself recorded when it built each image
/// (tools/make-test-images.sh writes a .manifest and .sha256 next to every image).
/// </summary>
public class ExtTests
{
    internal static readonly string Images = FindImages();

    private static string FindImages()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "tests", "images");
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException("tests/images not found. Run tools/make-test-images.sh first.");
    }

    private sealed record Expected(char Type, long Size, long Mtime, int Mode, string Link, string? Hash);

    private static Dictionary<string, Expected> LoadManifest(string prefix)
    {
        var hashes = new Dictionary<string, string>();
        foreach (var line in File.ReadAllLines(prefix + ".sha256"))
        {
            if (line.Length < 67) continue;
            hashes[line[66..].TrimStart('.', '/').Replace("./", "")] = line[..64];
        }
        var map = new Dictionary<string, Expected>();
        foreach (var line in File.ReadAllLines(prefix + ".manifest"))
        {
            var f = line.Split('\t');
            if (f.Length < 6) continue;
            long mtime = (long)Math.Floor(double.Parse(f[2], CultureInfo.InvariantCulture));
            hashes.TryGetValue(f[4], out var hash);
            map[f[4]] = new Expected(f[0][0], long.Parse(f[1]), mtime, Convert.ToInt32(f[3], 8), f[5], hash);
        }
        return map;
    }

    private static void Walk(IFileSystem fs, FsNode dir, string prefix, Dictionary<string, FsNode> into)
    {
        foreach (var n in fs.ListDirectory(dir))
        {
            string path = prefix + n.Name;
            Assert.True(into.TryAdd(path, n), "duplicate entry " + path);
            if (n.Kind == FsNodeKind.Directory) Walk(fs, n, path + "/", into);
        }
    }

    private static string Hash(IFileSystem fs, FsNode node)
    {
        using var s = fs.OpenFile(node);
        return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
    }

    internal static void AssertMatchesLinux(IBlockDevice volume, string manifestPrefix, string expectedType)
    {
        var expected = LoadManifest(manifestPrefix);
        using var fs = FileSystems.Open(volume);
        Assert.Equal(expectedType, fs.TypeName);
        Assert.Empty(fs.Warnings);

        var actual = new Dictionary<string, FsNode>();
        Walk(fs, fs.Root, "", actual);
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), actual.Keys.Order(StringComparer.Ordinal));

        foreach (var (path, e) in expected)
        {
            var n = actual[path];
            Assert.Null(n.Error);
            var kind = e.Type switch { 'd' => FsNodeKind.Directory, 'f' => FsNodeKind.File, 'l' => FsNodeKind.Symlink, _ => FsNodeKind.Other };
            Assert.True(kind == n.Kind, $"{path}: kind {n.Kind}, expected {kind}");
            Assert.True((int)(n.Mode & 0xFFF) == e.Mode, $"{path}: mode {Convert.ToString(n.Mode & 0xFFF, 8)}, expected {Convert.ToString(e.Mode, 8)}");
            Assert.True(n.ModifiedUtc.HasValue && new DateTimeOffset(DateTime.SpecifyKind(n.ModifiedUtc.Value, DateTimeKind.Utc)).ToUnixTimeSeconds() == e.Mtime,
                $"{path}: mtime {n.ModifiedUtc:o}, expected {e.Mtime}");
            if (kind == FsNodeKind.Symlink) Assert.Equal(e.Link, n.LinkTarget);
            if (kind == FsNodeKind.File)
            {
                Assert.True(n.Size == e.Size, $"{path}: size {n.Size}, expected {e.Size}");
                Assert.True(e.Hash == Hash(fs, n), $"{path}: content differs");
            }
        }
    }

    [Theory]
    [InlineData("ext4_default", "ext4")]
    [InlineData("ext3", "ext3")]
    [InlineData("ext2_1k", "ext2")]
    [InlineData("ext4_inline", "ext4")]
    [InlineData("ext4_metabg", "ext4")]
    [InlineData("ext4_bigalloc", "ext4")]
    [InlineData("ext4_frag", "ext4")]
    [InlineData("btrfs_default", "btrfs")]
    [InlineData("btrfs_zlib", "btrfs")]
    [InlineData("btrfs_lzo", "btrfs")]
    [InlineData("btrfs_zstd", "btrfs")]
    [InlineData("btrfs_small_nodes", "btrfs")]
    [InlineData("btrfs_mixed", "btrfs")]
    [InlineData("btrfs_xxhash", "btrfs")]
    [InlineData("btrfs_sha256", "btrfs")]
    [InlineData("btrfs_blake2", "btrfs")]
    [InlineData("xfs_default", "xfs")]
    [InlineData("xfs_1k", "xfs")]
    public void Whole_tree_matches_what_Linux_wrote(string image, string type)
    {
        string path = Path.Combine(Images, image + ".img");
        using var dev = new CachedDevice(new ImageFileDevice(path));
        var volumes = VolumeScanner.Scan(dev);
        Assert.Single(volumes);
        Assert.Equal(type, volumes[0].FileSystem.Type);
        Assert.True(volumes[0].FileSystem.Supported);
        AssertMatchesLinux(dev, path, type);
    }

    [Fact]
    public void Gpt_disk_lists_partitions_and_reads_them()
    {
        using var dev = new CachedDevice(new ImageFileDevice(Path.Combine(Images, "gpt.img")));
        var v = VolumeScanner.Scan(dev);
        Assert.Equal(2, v.Count);
        Assert.Equal(("GPT", 1, "rootfs", "Linux filesystem", "ext4", "part-a"),
            (v[0].Partition.Scheme, v[0].Partition.Number, v[0].Partition.Name, v[0].Partition.TypeName, v[0].FileSystem.Type, v[0].FileSystem.Label));
        Assert.Equal(("GPT", 2, "home", "Linux home", "ext2", "part-b"),
            (v[1].Partition.Scheme, v[1].Partition.Number, v[1].Partition.Name, v[1].Partition.TypeName, v[1].FileSystem.Type, v[1].FileSystem.Label));
        Assert.Equal(2048 * 512L, v[0].Partition.Offset);
        Assert.Equal(32768 * 512L, v[0].Partition.Length);
        using (var p = new SubDevice(dev, v[0].Partition.Offset, v[0].Partition.Length)) AssertMatchesLinux(p, Path.Combine(Images, "part_a.img"), "ext4");
        using (var p = new SubDevice(dev, v[1].Partition.Offset, v[1].Partition.Length)) AssertMatchesLinux(p, Path.Combine(Images, "part_b.img"), "ext2");
    }

    [Fact]
    public void Mbr_disk_lists_primary_and_logical_partitions()
    {
        using var dev = new CachedDevice(new ImageFileDevice(Path.Combine(Images, "mbr.img")));
        var v = VolumeScanner.Scan(dev);
        Assert.Equal(new[] { 1, 5, 6 }, v.Select(x => x.Partition.Number));
        Assert.Equal(new[] { "ext4", "ext2", "swap" }, v.Select(x => x.FileSystem.Type));
        Assert.Equal(new[] { 2048L, 36864L, 71680L }, v.Select(x => x.Partition.Offset / 512));
        Assert.False(v[2].FileSystem.Supported);
        using (var p = new SubDevice(dev, v[0].Partition.Offset, v[0].Partition.Length)) AssertMatchesLinux(p, Path.Combine(Images, "part_a.img"), "ext4");
        using (var p = new SubDevice(dev, v[1].Partition.Offset, v[1].Partition.Length)) AssertMatchesLinux(p, Path.Combine(Images, "part_b.img"), "ext2");
    }

    [Theory]
    [InlineData("ext4_default", "random_3mb.bin")]
    [InlineData("ext2_1k", "random_3mb.bin")]
    [InlineData("ext4_frag", "fragmented.bin")]
    [InlineData("ext2_1k", "sparse_far.bin")]
    [InlineData("btrfs_default", "random_3mb.bin")]
    [InlineData("btrfs_zlib", "mixed.bin")]
    [InlineData("btrfs_lzo", "docs.tar")]
    [InlineData("btrfs_zstd", "numbers.txt")]
    [InlineData("xfs_default", "many_extents.bin")]
    [InlineData("xfs_1k", "many_extents.bin")]
    public void Random_access_reads_match_a_sequential_read(string image, string file)
    {
        using var dev = new CachedDevice(new ImageFileDevice(Path.Combine(Images, image + ".img")));
        using var fs = FileSystems.Open(dev);
        var node = fs.ListDirectory(fs.Root).Single(n => n.Name == file);
        byte[] all;
        using (var s = fs.OpenFile(node)) { using var ms = new MemoryStream(); s.CopyTo(ms); all = ms.ToArray(); }
        Assert.Equal(node.Size, all.Length);

        var rng = new Random(1234);
        using var stream = fs.OpenFile(node);
        var buf = new byte[300_000];
        for (int i = 0; i < 300; i++)
        {
            long pos = rng.NextInt64(0, all.Length);
            int len = rng.Next(1, buf.Length);
            stream.Position = pos;
            int got = stream.ReadAtLeast(buf.AsSpan(0, len), len, throwOnEndOfStream: false);
            Assert.Equal((int)Math.Min(len, all.Length - pos), got);
            Assert.True(all.AsSpan((int)pos, got).SequenceEqual(buf.AsSpan(0, got)), $"mismatch at {pos}+{len}");
        }
    }

    [Theory]
    [InlineData("btrfs_default")]
    [InlineData("btrfs_xxhash")]
    [InlineData("btrfs_sha256")]
    [InlineData("btrfs_blake2")]
    public void Btrfs_reports_corrupted_file_content_instead_of_returning_it(string image)
    {
        string source = Path.Combine(Images, image + ".img");
        byte[] disk = File.ReadAllBytes(source);
        byte[] needle = new byte[32];
        using (var dev = new CachedDevice(new ImageFileDevice(source)))
        using (var fs = FileSystems.Open(dev))
        using (var s = fs.OpenFile(fs.ListDirectory(fs.Root).Single(n => n.Name == "random_3mb.bin")))
        {
            s.Position = 1_500_000;
            s.ReadExactly(needle);
        }
        int at = disk.AsSpan().IndexOf(needle);
        Assert.True(at > 0, "file content not found in the image");
        Assert.True(disk.AsSpan(at + 1).IndexOf(needle) < 0, "content is stored twice; the test needs a single copy");
        disk[at + 5] ^= 0x40; // one flipped bit

        string damaged = Path.Combine(Path.GetTempPath(), "linuxfs-corrupt-" + Guid.NewGuid().ToString("N") + ".img");
        try
        {
            File.WriteAllBytes(damaged, disk);
            using var dev = new CachedDevice(new ImageFileDevice(damaged));
            using var fs = FileSystems.Open(dev);
            var root = fs.ListDirectory(fs.Root);
            var ex = Assert.Throws<IOException>(() => Hash(fs, root.Single(n => n.Name == "random_3mb.bin")));
            Assert.Contains("integrity", ex.Message);
            // Everything else is still readable, including a file that has no checksums at all.
            Assert.Equal("hello world\n", new StreamReader(fs.OpenFile(root.Single(n => n.Name == "hello.txt"))).ReadToEnd());
            Assert.Equal(100000, Hash(fs, root.Single(n => n.Name == "no_checksum.bin")).Length > 0 ? root.Single(n => n.Name == "no_checksum.bin").Size : -1);
            Hash(fs, root.Single(n => n.Name == "docs.tar"));

            // Copying out reports the damaged file as failed and leaves no partial copy behind.
            string dest = Path.Combine(Path.GetTempPath(), "linuxfs-corrupt-out-" + Guid.NewGuid().ToString("N"));
            try
            {
                var result = Exporter.Export(fs, root.Where(n => n.Name is "random_3mb.bin" or "hello.txt").ToList(), dest);
                Assert.Equal(1, result.Files);
                Assert.Single(result.Errors);
                Assert.False(File.Exists(Path.Combine(dest, "random_3mb.bin")));
            }
            finally { if (Directory.Exists(dest)) Directory.Delete(dest, true); }
        }
        finally { File.Delete(damaged); }
    }

    [Fact]
    public void Xfs_old_v4_format_matches_Linux()
    {
        string path = Path.Combine(Images, "xfs_v4.img");
        Assert.True(File.Exists(path), "xfs_v4.img was not built: the build machine's kernel or mkfs.xfs has no v4 support");
        using var dev = new CachedDevice(new ImageFileDevice(path));
        AssertMatchesLinux(dev, path, "xfs");
    }

    [Fact]
    public void Xfs_warns_when_the_log_was_not_closed_cleanly()
    {
        using var dev = new CachedDevice(new ImageFileDevice(Path.Combine(Images, "xfs_dirty.img")));
        using var fs = FileSystems.Open(dev);
        Assert.Contains(fs.Warnings, w => w.Contains("not cleanly unmounted"));
        var all = new Dictionary<string, FsNode>();
        Walk(fs, fs.Root, "", all); // still browsable
        Assert.Contains("hello.txt", all.Keys);
    }

    [Fact]
    public void Symlinks_resolve_to_their_targets()
    {
        using var dev = new CachedDevice(new ImageFileDevice(Path.Combine(Images, "ext4_default.img")));
        using var fs = FileSystems.Open(dev);
        var root = new List<FsNode> { fs.Root };
        var viaDir = FsPath.Resolve(fs, root, "link_to_dir/a/b/c/leaf.txt");
        Assert.Equal(new[] { "", "docs", "deep", "a", "b", "c", "leaf.txt" }, viaDir!.Select(n => n.Name));
        var docs = FsPath.Resolve(fs, root, "docs")!;
        Assert.Equal("hello.txt", FsPath.Resolve(fs, docs, "link_up")![^1].Name);
        Assert.Null(FsPath.Resolve(fs, root, "long_symlink"));
    }

    [Fact]
    public void Export_copies_everything_and_makes_names_valid_for_Windows()
    {
        using var dev = new CachedDevice(new ImageFileDevice(Path.Combine(Images, "ext4_default.img")));
        using var fs = FileSystems.Open(dev);
        var expected = LoadManifest(Path.Combine(Images, "ext4_default.img"));
        string dest = Path.Combine(Path.GetTempPath(), "linuxfs-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = Exporter.Export(fs, fs.ListDirectory(fs.Root), dest);
            Assert.Empty(result.Errors);
            Assert.Equal(expected.Values.Count(e => e.Type == 'f'), result.Files);
            Assert.Equal(expected.Values.Count(e => e.Type == 'd'), result.Folders);
            Assert.Equal(expected.Values.Count(e => e.Type is 'l' or 'p'), result.Skipped.Count);
            Assert.Equal(result.Files, Directory.GetFiles(dest, "*", SearchOption.AllDirectories).Length);

            Assert.True(File.Exists(Path.Combine(dest, "weird_name_.txt")));
            Assert.True(File.Exists(Path.Combine(dest, "trailing.dot._")));
            Assert.True(File.Exists(Path.Combine(dest, "_CON")));
            var caseFiles = Directory.GetFiles(dest, "case*.txt").Select(f => File.ReadAllText(f).Trim()).Order().ToArray();
            Assert.Equal(new[] { "d", "e" }, caseFiles);
            Assert.Equal(4, result.Renamed.Count);

            string hello = Path.Combine(dest, "hello.txt");
            Assert.Equal("hello world\n", File.ReadAllText(hello));
            Assert.Equal(new DateTime(2020, 2, 3, 4, 5, 6, DateTimeKind.Utc), File.GetLastWriteTimeUtc(hello));
            string big = Path.Combine(dest, "random_3mb.bin");
            Assert.Equal(expected["random_3mb.bin"].Hash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(big))).ToLowerInvariant());

            // A second export into the same folder must not overwrite anything.
            var again = Exporter.Export(fs, fs.ListDirectory(fs.Root).Where(n => n.Name == "hello.txt").ToList(), dest);
            Assert.Equal(1, again.Files);
            Assert.True(File.Exists(Path.Combine(dest, "hello (2).txt")));
        }
        finally { if (Directory.Exists(dest)) Directory.Delete(dest, true); }
    }

    [Fact]
    public void Damaged_or_foreign_data_is_rejected_cleanly()
    {
        string path = Path.Combine(Path.GetTempPath(), "linuxfs-junk-" + Guid.NewGuid().ToString("N") + ".img");
        try
        {
            var junk = new byte[1 << 20];
            new Random(7).NextBytes(junk);
            File.WriteAllBytes(path, junk);
            using (var dev = new CachedDevice(new ImageFileDevice(path)))
            {
                Assert.Equal(FsDetector.Unknown, FsDetector.Detect(dev).Type);
                Assert.Throws<NotSupportedException>(() => FileSystems.Open(dev));
                Assert.Single(PartitionTable.Read(dev));
            }

            // A real superblock followed by garbage: must fail with a data error, not crash or hang.
            var img = File.ReadAllBytes(Path.Combine(Images, "part_a.img"));
            new Random(9).NextBytes(img.AsSpan(4096, img.Length - 4096));
            File.WriteAllBytes(path, img);
            using (var dev = new CachedDevice(new ImageFileDevice(path)))
            {
                var ex = Record.Exception(() =>
                {
                    using var fs = FileSystems.Open(dev);
                    var all = new Dictionary<string, FsNode>();
                    Walk(fs, fs.Root, "", all);
                });
                Assert.True(ex is InvalidDataException or IOException, "got " + (ex?.GetType().Name ?? "no exception"));
            }
        }
        finally { File.Delete(path); }
    }
}
