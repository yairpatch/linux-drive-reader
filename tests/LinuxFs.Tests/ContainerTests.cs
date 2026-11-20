using LinuxFs.Core;
using LinuxFs.Core.Containers;
using LinuxFs.Core.Detection;
using LinuxFs.Core.Devices;

namespace LinuxFs.Tests;

/// <summary>LUKS-encrypted volumes and LVM, checked against images made by cryptsetup and lvm2.</summary>
public class ContainerTests
{
    private const string Password = "correct horse";
    private static string Image(string name) => Path.Combine(ExtTests.Images, name);

    [Theory]
    [InlineData("luks1_xts", 1, "ext4")]
    [InlineData("luks1_cbc", 1, "ext2")]
    [InlineData("luks2_argon2id", 2, "ext4")]
    [InlineData("luks2_argon2i", 2, "ext4")]
    [InlineData("luks2_pbkdf2", 2, "ext4")]
    public void Luks_unlocks_with_the_password_and_content_matches_Linux(string image, int version, string fsType)
    {
        string path = Image(image + ".img");
        using var dev = new CachedDevice(new ImageFileDevice(path));
        Assert.Equal("LUKS", FsDetector.Detect(dev).Type);
        var luks = LuksVolume.Open(dev);
        Assert.Equal(version, luks.Version);
        Assert.True(luks.HasPasswordSlots);
        Assert.Throws<WrongPasswordException>(() => luks.Unlock("not the password"));
        Assert.Throws<WrongPasswordException>(() => luks.Unlock(""));
        var key = luks.Unlock(Password);
        using var plain = key.Open(dev);
        ExtTests.AssertMatchesLinux(plain, path, fsType);
    }

    [Fact]
    public void Luks_accepts_a_password_from_any_key_slot()
    {
        string path = Image("luks2_pbkdf2.img");
        using var dev = new CachedDevice(new ImageFileDevice(path));
        using var plain = LuksVolume.Open(dev).Unlock("second password").Open(dev);
        ExtTests.AssertMatchesLinux(plain, path, "ext4");
    }

    [Fact]
    public void Lvm_lists_volumes_and_reads_one_stored_in_two_pieces()
    {
        using var dev = new CachedDevice(new ImageFileDevice(Image("lvm.img")));
        Assert.Equal("LVM", FsDetector.Detect(dev).Type);
        var group = LvmGroup.Read(dev);
        Assert.Equal("testvg", group.Name);
        Assert.Equal(new[] { "home", "root" }, group.Volumes.Select(v => v.Name));
        var root = group.Volumes.Single(v => v.Name == "root");
        Assert.Equal(36L * 1024 * 1024, root.Size);
        Assert.Equal(2, root.Segments.Count);
        Assert.All(group.Volumes, v => Assert.True(v.Supported, v.Note));
        using (var lv = root.Open(dev)) ExtTests.AssertMatchesLinux(lv, Image("lvm.img.root"), "ext4");
        using (var lv = group.Volumes.Single(v => v.Name == "home").Open(dev)) ExtTests.AssertMatchesLinux(lv, Image("lvm.img.home"), "ext2");
    }

    [Fact]
    public void Encrypted_install_layout_partition_then_Luks_then_Lvm()
    {
        using var disk = new CachedDevice(new ImageFileDevice(Image("luks_lvm.img")));
        var volumes = VolumeScanner.Scan(disk);
        Assert.Equal(new[] { "ext4", "LUKS" }, volumes.Select(v => v.FileSystem.Type));
        using (var boot = new SubDevice(disk, volumes[0].Partition.Offset, volumes[0].Partition.Length))
            ExtTests.AssertMatchesLinux(boot, Image("luks_lvm.img.boot"), "ext4");

        using var encrypted = new SubDevice(disk, volumes[1].Partition.Offset, volumes[1].Partition.Length);
        using var plain = LuksVolume.Open(encrypted).Unlock(Password).Open(encrypted);
        Assert.Equal("LVM", FsDetector.Detect(plain).Type);
        var group = LvmGroup.Read(plain);
        Assert.Equal("cryptvg", group.Name);
        Assert.Equal(new[] { "data", "root", "swap" }, group.Volumes.Select(v => v.Name));
        using (var lv = group.Volumes.Single(v => v.Name == "swap").Open(plain)) Assert.Equal("swap", FsDetector.Detect(lv).Type);
        using (var lv = group.Volumes.Single(v => v.Name == "root").Open(plain)) ExtTests.AssertMatchesLinux(lv, Image("luks_lvm.img.root"), "ext4");
        using (var lv = group.Volumes.Single(v => v.Name == "data").Open(plain)) ExtTests.AssertMatchesLinux(lv, Image("luks_lvm.img.data"), "ext2");
    }
}
