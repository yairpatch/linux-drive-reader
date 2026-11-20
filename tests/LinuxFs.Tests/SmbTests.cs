using System.Security.Cryptography;
using System.Text;
using LinuxFs.Core;
using LinuxFs.Core.Devices;
using LinuxFs.Core.Smb;

namespace LinuxFs.Tests;

/// <summary>
/// End to end: publish an image through the built-in server, map it to a real drive letter with
/// Windows' own client, and read it back through ordinary file APIs.
/// </summary>
public class SmbTests
{
    [Fact]
    public void Md4_matches_the_published_test_vectors()
    {
        Assert.Equal("31D6CFE0D16AE931B73C59D7E0C089C0", Convert.ToHexString(Ntlm.Md4(Array.Empty<byte>())));
        Assert.Equal("A448017AAF21D8525FC10AE87AA6729D", Convert.ToHexString(Ntlm.Md4(Encoding.ASCII.GetBytes("abc"))));
        Assert.Equal("E33B4DDC9C38F2199C3E7B164FCC0536", Convert.ToHexString(Ntlm.Md4(Encoding.ASCII.GetBytes("12345678901234567890123456789012345678901234567890123456789012345678901234567890"))));
    }

    [Fact]
    public void Remounting_on_the_same_letter_serves_the_new_volume_and_stale_letters_are_cleaned_up()
    {
        if (!OperatingSystem.IsWindows() || !DriveMapper.IsSupported) return;
        using var devA = new CachedDevice(new ImageFileDevice(Path.Combine(ExtTests.Images, "part_a.img")));
        using var devB = new CachedDevice(new ImageFileDevice(Path.Combine(ExtTests.Images, "ext4_frag.img")));
        using var a = FileSystems.Open(devA);
        using var b = FileSystems.Open(devB);
        using var server = SmbServer.ForCurrentUser();
        char letter = DriveMapper.FindFreeLetter() ?? throw new InvalidOperationException("no free drive letter");
        try
        {
            server.AddShare("again", a, devA.Length);
            Assert.Null(DriveMapper.Map(letter, server, "again", viaShell: false));
            Assert.False(File.Exists(letter + @":\fragmented.bin"));
            DriveMapper.Unmap(letter, viaShell: false);
            server.RemoveShare("again");

            // A different volume on the same letter straight away. It gets a new share name, as in the app:
            // Windows remembers for some seconds what it last saw under a name, missing files included.
            server.AddShare("again-2", b, devB.Length);
            Assert.Null(DriveMapper.Map(letter, server, "again-2", viaShell: false));
            Assert.True(File.Exists(letter + @":\fragmented.bin"));
            Assert.Equal("frag", new DriveInfo(letter + ":\\").VolumeLabel);
        }
        finally { DriveMapper.Unmap(letter, viaShell: false); }

        // A letter left behind by a crashed run is cleaned up, and only if it is ours.
        server.AddShare("stale", a, devA.Length);
        Assert.Null(DriveMapper.Map(letter, server, "stale", viaShell: false));
        DriveMapper.RemoveIfStale(letter, viaShell: false);
        Assert.False(Directory.Exists(letter + ":\\"));
        DriveMapper.RemoveIfStale('C', viaShell: false);
        Assert.True(Directory.Exists(@"C:\"));
    }

    private static string Sha(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
    }

    [Fact]
    public void Mapping_through_Explorer_works_as_it_must_when_the_app_is_elevated()
    {
        if (!OperatingSystem.IsWindows() || !DriveMapper.IsSupported) return;
        using var dev = new CachedDevice(new ImageFileDevice(Path.Combine(ExtTests.Images, "part_a.img")));
        using var fs = FileSystems.Open(dev);
        using var server = SmbServer.ForCurrentUser();
        server.AddShare("viashell", fs, dev.Length);
        char letter = DriveMapper.FindFreeLetter() ?? throw new InvalidOperationException("no free drive letter");
        string? error = DriveMapper.Map(letter, server, "viashell", viaShell: true);
        Assert.True(error == null, error);
        try { Assert.Equal("partition file\n", File.ReadAllText(letter + @":\readme.txt")); }
        finally
        {
            DriveMapper.Unmap(letter, viaShell: true);
            for (int i = 0; i < 50 && Directory.Exists(letter + ":\\"); i++) Thread.Sleep(100);
        }
        Assert.False(Directory.Exists(letter + ":\\"));
    }

    [Theory]
    [InlineData("ext4_default")]
    [InlineData("btrfs_zstd")]
    public void A_mapped_drive_letter_shows_the_same_files_as_Linux(string image)
    {
        if (!OperatingSystem.IsWindows() || !DriveMapper.IsSupported) return; // needs Windows 11 24H2 or newer
        string imagePath = Path.Combine(ExtTests.Images, image + ".img");
        using var dev = new CachedDevice(new ImageFileDevice(imagePath));
        using var fs = FileSystems.Open(dev);
        using var server = SmbServer.ForCurrentUser();
        server.AddShare("test", fs, dev.Length);

        char letter = DriveMapper.FindFreeLetter() ?? throw new InvalidOperationException("no free drive letter");
        string? error = DriveMapper.Map(letter, server, "test", viaShell: false);
        Assert.True(error == null, error);
        try
        {
            string drive = letter + ":\\";
            Assert.True(Directory.Exists(drive));
            var info = new DriveInfo(drive);
            Assert.Equal(DriveType.Network, info.DriveType);
            Assert.Equal(fs.TypeName, info.DriveFormat);
            Assert.Equal(fs.Label, info.VolumeLabel);

            // Everything Linux recorded whose name is already valid on Windows must be there, unchanged.
            var lines = File.ReadAllLines(imagePath + ".manifest").Select(l => l.Split('\t')).Where(f => f.Length >= 6).ToList();
            var hashes = File.ReadAllLines(imagePath + ".sha256").Where(l => l.Length > 66).ToDictionary(l => l[66..].TrimStart('.', '/'), l => l[..64]);
            bool Plain(string p) => p.Split('/').All(c => Exporter.SafeName(c) == c) && !p.Contains("Case.txt", StringComparison.OrdinalIgnoreCase);
            int files = 0, folders = 0;
            foreach (var f in lines.Where(f => Plain(f[4]) && !lines.Any(o => o[0] == "l" && (f[4] + "/").StartsWith(o[4] + "/"))))
            {
                string path = Path.Combine(drive, f[4].Replace('/', '\\'));
                if (f[0] == "d") { Assert.True(Directory.Exists(path), "missing folder " + f[4]); folders++; }
                else if (f[0] == "f")
                {
                    var fi = new FileInfo(path);
                    Assert.True(fi.Exists, "missing file " + f[4]);
                    Assert.True(fi.Length == long.Parse(f[1]), "wrong size for " + f[4]);
                    Assert.True(hashes[f[4]] == Sha(path), "wrong content for " + f[4]);
                    long mtime = (long)Math.Floor(double.Parse(f[2], System.Globalization.CultureInfo.InvariantCulture));
                    Assert.True(new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeSeconds() == mtime, "wrong date for " + f[4]);
                    files++;
                }
            }
            Assert.True(files > 1500 && folders > 5, $"only {files} files and {folders} folders were checked");

            // A folder listing through Windows has every entry exactly once.
            var listed = Directory.GetFileSystemEntries(Path.Combine(drive, "manyfiles"));
            Assert.Equal(1500, listed.Length);
            Assert.Equal(1500, listed.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.Equal(10, Directory.GetFiles(Path.Combine(drive, "manyfiles"), "*_15?.txt").Length);

            // Links are followed; names Windows cannot hold are adjusted; lookups ignore case.
            Assert.Equal("hello world\n", File.ReadAllText(Path.Combine(drive, "link_to_hello")));
            Assert.Equal("deep file\n", File.ReadAllText(Path.Combine(drive, @"link_to_dir\a\b\c\leaf.txt")));
            Assert.Equal("a\n", File.ReadAllText(Path.Combine(drive, "weird_name_.txt")));
            Assert.Equal("hello world\n", File.ReadAllText(Path.Combine(drive, "HELLO.TXT")));
            Assert.False(File.Exists(Path.Combine(drive, "a_fifo")));
            Assert.False(File.Exists(Path.Combine(drive, "no_such_file.txt")));

            // Reading from the middle of a large file.
            using (var s = File.OpenRead(Path.Combine(drive, "random_3mb.bin")))
            using (var direct = fs.OpenFile(fs.ListDirectory(fs.Root).Single(n => n.Name == "random_3mb.bin")))
            {
                var a = new byte[100_000]; var b = new byte[100_000];
                s.Position = direct.Position = 1_234_567;
                s.ReadExactly(a); direct.ReadExactly(b);
                Assert.True(a.AsSpan().SequenceEqual(b));
            }

            // Nothing can be changed.
            Assert.ThrowsAny<Exception>(() => File.WriteAllText(Path.Combine(drive, "new.txt"), "x"));
            Assert.ThrowsAny<Exception>(() => File.Delete(Path.Combine(drive, "hello.txt")));
            Assert.ThrowsAny<Exception>(() => File.AppendAllText(Path.Combine(drive, "hello.txt"), "x"));
            Assert.Equal("hello world\n", File.ReadAllText(Path.Combine(drive, "hello.txt")));
        }
        finally { DriveMapper.Unmap(letter, viaShell: false); }
        Assert.False(Directory.Exists(letter + ":\\"));
    }
}
