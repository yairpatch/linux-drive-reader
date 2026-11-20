using LinuxFs.Core;
using LinuxFs.Core.Devices;
using LinuxFs.Core.Ext;
using LinuxFs.Core.Xfs;

namespace LinuxFs.Tests;

/// <summary>
/// Disks copied while still in use, so that committed changes exist only in the journal or log.
/// The manifests record what Linux showed after mounting the same copies, which replays them.
/// </summary>
public class JournalTests
{
    private static string Image(string name) => Path.Combine(ExtTests.Images, name + ".img");

    [Theory]
    [InlineData("ext4_journal", "ext4")]
    [InlineData("ext4_journal_data", "ext4")]
    [InlineData("ext3_journal", "ext3")]
    [InlineData("xfs_journal", "xfs")]
    [InlineData("xfs_journal_1k", "xfs")]
    [InlineData("ext4_journal_wrap", "ext4")]
    [InlineData("xfs_journal_wrap", "xfs")]
    public void After_replay_the_disk_matches_what_Linux_shows(string image, string type)
    {
        using var dev = new CachedDevice(new ImageFileDevice(Image(image)));
        using (var fs = FileSystems.Open(dev))
        {
            int applied = fs switch { ExtFileSystem e => e.JournalTransactionsApplied, XfsFileSystem x => x.LogTransactionsApplied, _ => -1 };
            Assert.True(applied > 0, $"expected the journal to be replayed, got {applied}");
        }
        ExtTests.AssertMatchesLinux(dev, Image(image), type);
    }

    [Theory]
    [InlineData("ext4_journal")]
    [InlineData("ext3_journal")]
    [InlineData("xfs_journal")]
    public void Without_replay_the_recent_changes_are_missing_and_a_warning_says_so(string image)
    {
        // Proves these images really depend on the journal: the same comparison fails without replaying it.
        using var dev = new CachedDevice(new ImageFileDevice(Image(image)));
        using (var fs = FileSystems.Open(dev, new OpenOptions(ReplayJournal: false)))
            Assert.Contains(fs.Warnings, w => w.Contains("not cleanly unmounted"));
        Assert.ThrowsAny<Exception>(() => ExtTests.AssertMatchesLinux(dev, Image(image), "", new OpenOptions(ReplayJournal: false)));
    }

    [Fact]
    public void A_journal_block_starting_with_the_journal_magic_is_restored()
    {
        // In data=journal mode file contents go through the journal; this file's first bytes equal the
        // journal's own magic number, which jbd2 stores "escaped" and replay has to put back.
        using var dev = new CachedDevice(new ImageFileDevice(Image("ext4_journal_data")));
        using var fs = FileSystems.Open(dev);
        var added = fs.ListDirectory(fs.Root).Single(n => n.Name == "added");
        var file = fs.ListDirectory(added).Single(n => n.Name == "escape.bin");
        using var s = fs.OpenFile(file);
        var head = new byte[4];
        s.ReadExactly(head);
        Assert.Equal(new byte[] { 0xC0, 0x3B, 0x39, 0x98 }, head);
    }

    [Fact]
    public void A_damaged_journal_is_reported_and_the_disk_still_opens()
    {
        byte[] disk = File.ReadAllBytes(Image("ext4_journal"));
        long journalStart;
        using (var dev = new CachedDevice(new ImageFileDevice(Image("ext4_journal"))))
        {
            var fs = new ExtFileSystem(dev);
            journalStart = fs.CreateMap(fs.ReadInode(fs.JournalInode)).Map(0).Physical * fs.BlockSize;
        }
        disk[journalStart] ^= 0xFF; // break the journal's magic number
        string path = Path.Combine(Path.GetTempPath(), "linuxfs-badjournal-" + Guid.NewGuid().ToString("N") + ".img");
        try
        {
            File.WriteAllBytes(path, disk);
            using var dev = new CachedDevice(new ImageFileDevice(path));
            using var fs = FileSystems.Open(dev);
            Assert.Contains(fs.Warnings, w => w.Contains("could not be applied"));
            Assert.NotEmpty(fs.ListDirectory(fs.Root));
        }
        finally { File.Delete(path); }
    }
}
