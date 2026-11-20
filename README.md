# Linux Drive Reader

A Windows app that reads Linux disks without WSL, drivers or any third-party software.
Version 1 is a read-only file browser: pick a partition, browse it, copy files out to Windows.

- **Reads:** ext2, ext3, ext4 and btrfs (subvolumes, snapshots, zlib/LZO/zstd compression, file contents verified against their checksums; one disk only), and xfs (v4 and v5 formats). Other filesystems are detected and shown as "not supported yet".
- **Containers:** LUKS1 and LUKS2 encrypted volumes (password unlock; AES-XTS and AES-CBC; PBKDF2, Argon2i, Argon2id) and LVM logical volumes, in any nesting (for example partition → LUKS → LVM → filesystem).
- **Sources:** physical disks (needs administrator rights) and disk image files (no special rights).
- **Partition tables:** GPT, MBR (including logical partitions), or none.

## Layout

- `src/LinuxFs.Core` – disk access, partition tables, filesystem detection, the filesystem readers (`Ext`, `Btrfs`, `Xfs`), containers (`Containers`: LUKS, LVM), crypto written for them (`Crypto`), export to Windows.
- `src/LinuxDriveReader` – the WPF app.
- `tests/LinuxFs.Tests` – compares the reader against images built by real Linux tools.
- `tools/make-test-images.sh` – builds those images (development only; needs Linux and root).

## Build and run

    dotnet build
    dotnet run --project src/LinuxDriveReader
    dotnet test          # needs tests/images, see below

To build the test images (from Windows, using a WSL distro as the Linux build box):

    wsl -u root -- bash tools/make-test-images.sh tests/images

## Adding a filesystem

Implement `IFileSystem` (see `Ext/ExtFileSystem.cs`), mark the type as supported in
`Detection/FsDetector.cs`, and return it from `FileSystems.Open`.

## Not done yet

Journal replay for uncleanly unmounted ext3/4 (the app warns instead), fscrypt-encrypted folders,
applying the xfs log after an unclean shutdown (the app warns instead), xfs metadata checksum verification, multi-disk btrfs (RAID 0/10/5/6), LVM volumes that span several disks or use thin/RAID/cache types, LUKS unlocked by TPM or token, non-AES ciphers, f2fs, write support, and a real drive letter.
