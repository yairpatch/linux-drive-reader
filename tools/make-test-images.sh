#!/bin/bash
# Builds the disk images the tests read. Development-time only: needs Linux and root
# (it formats and loop-mounts images). The app itself never uses this.
#   usage: sudo bash make-test-images.sh <output-dir> [ext|btrfs|xfs|crypt|journal]
set -euo pipefail
OUT="$1"
if [ $# -ge 2 ]; then ONLY=$2; else ONLY=all; fi
W=$(mktemp -d)
MNT=$W/mnt
SRC=$W/src
mkdir -p "$OUT" "$MNT" "$SRC"
LOOPS=""
cleanup() {
  umount "$MNT" 2>/dev/null || true
  vgchange -an testvg cryptvg >/dev/null 2>&1 || true
  for m in /dev/mapper/t_*; do [ -e "$m" ] && cryptsetup close "$(basename "$m")" 2>/dev/null || true; done
  for l in $LOOPS; do losetup -d "$l" 2>/dev/null || true; done
  rm -rf "$W"
}
trap cleanup EXIT

# ---- the file tree every image gets -------------------------------------
mkdir -p "$SRC/docs/deep/a/b/c" "$SRC/empty_dir" "$SRC/unicode_שלום_日本語" "$SRC/manyfiles"
echo "hello world" > "$SRC/hello.txt"
: > "$SRC/empty.txt"
head -c 60 /dev/urandom | base64 -w0 > "$SRC/small_80_bytes.txt"
head -c 3000000 /dev/urandom > "$SRC/random_3mb.bin"
head -c 70000 /dev/urandom > "$SRC/docs/medium.bin"
echo "deep file" > "$SRC/docs/deep/a/b/c/leaf.txt"
echo "unicode content" > "$SRC/unicode_שלום_日本語/קובץ.txt"
ln -s hello.txt "$SRC/link_to_hello"
ln -s docs/deep "$SRC/link_to_dir"
ln -s ../hello.txt "$SRC/docs/link_up"
ln -s "/absolute/$(printf 'x%.0s' $(seq 1 120))" "$SRC/long_symlink"
for i in $(seq 1 1500); do echo "file $i" > "$SRC/manyfiles/file_with_a_fairly_long_name_$i.txt"; done
truncate -s 5M "$SRC/sparse.bin"
printf 'DATA' | dd of="$SRC/sparse.bin" bs=1 seek=4000000 conv=notrunc status=none
echo a > "$SRC/weird:name?.txt"
echo b > "$SRC/trailing.dot."
echo c > "$SRC/CON"
echo d > "$SRC/Case.txt"
echo e > "$SRC/case.txt"
mkfifo "$SRC/a_fifo"
chmod 4755 "$SRC/hello.txt"
touch -d '2020-02-03 04:05:06 UTC' "$SRC/hello.txt"
touch -d '1999-12-31 23:59:59 UTC' "$SRC/docs/medium.bin"

SMALL=$W/small
mkdir -p "$SMALL/sub"
echo "partition file" > "$SMALL/readme.txt"
head -c 200000 /dev/urandom > "$SMALL/sub/data.bin"

# ---- helpers -------------------------------------------------------------
manifest() { # <mounted dir> <output prefix>
  ( cd "$1"
    find . -mindepth 1 -printf '%y\t%s\t%T@\t%m\t%P\t%l\n' > "$2.manifest"
    find . -type f -exec sha256sum {} + > "$2.sha256" || true )
}
build() { # <name> <size> <source tree> <hook function or ''> <mke2fs args...>
  local name=$1 size=$2 tree=$3 hook=$4; shift 4
  local img=$W/$name.img
  rm -f "$img"; truncate -s "$size" "$img"
  mke2fs -q -F "$@" "$img"
  mount -o loop "$img" "$MNT"
  cp -a "$tree/." "$MNT/"
  if [ -n "$hook" ]; then $hook; fi
  sync
  manifest "$MNT" "$OUT/$name.img"
  umount "$MNT"
  cp "$img" "$OUT/$name.img"
  echo "built $name.img"
}
hook_far() { # data past the double-indirect range (1 KiB blocks) => triple indirect
  dd if=/dev/urandom of="$MNT/sparse_far.bin" bs=1k count=8 seek=70000 status=none
}
hook_frag() { # a file broken into thousands of extents => multi-level extent tree
  python3 - "$MNT" <<'PY'
import os, sys
mnt = sys.argv[1]
fill = os.path.join(mnt, "fill")
os.mkdir(fill)
n = 0
try:
    while True:
        with open(os.path.join(fill, "f%d" % n), "wb") as f:
            f.write(b"x" * 4096)
        n += 1
        if n % 500 == 0:
            os.sync()
except OSError:
    pass
os.sync()
for i in range(0, n, 2):
    try: os.remove(os.path.join(fill, "f%d" % i))
    except OSError: pass
os.sync()
size = (n // 2) * 4096 * 9 // 10
with open(os.path.join(mnt, "fragmented.bin"), "wb") as f:
    f.write(os.urandom(size))
os.sync()
import shutil
shutil.rmtree(fill)
os.sync()
PY
  echo "fragmented.bin extents: $(filefrag "$MNT/fragmented.bin" | sed 's/.*: //')"
}

if [ "$ONLY" = all ] || [ "$ONLY" = ext ]; then
# ---- single-filesystem images -------------------------------------------
build ext4_default 48M "$SRC" ""        -t ext4 -L ext4test
build ext3         48M "$SRC" ""        -t ext3 -L ext3test
build ext2_1k      32M "$SRC" hook_far  -t ext2 -b 1024 -I 128 -L ext2test
build ext4_inline  48M "$SRC" ""        -t ext4 -O inline_data -L inline
build ext4_metabg 160M "$SRC" ""        -t ext4 -b 1024 -O 64bit,meta_bg,^resize_inode -L metabg
build ext4_bigalloc 64M "$SRC" ""       -t ext4 -O bigalloc -C 16384 -L bigalloc
build ext4_frag    48M "$SMALL" hook_frag -t ext4 -m 0 -N 16000 -L frag
build part_a       16M "$SMALL" ""      -t ext4 -L part-a
build part_b       16M "$SMALL" ""      -t ext2 -L part-b

# ---- partitioned disks ---------------------------------------------------
G=$W/gpt.img; truncate -s 40M "$G"
sfdisk -q "$G" <<'EOF'
label: gpt
start=2048, size=32768, type=0FC63DAF-8483-4772-8E79-3D69D8477DE4, name="rootfs"
start=34816, size=32768, type=933AC7E1-2EB4-4F13-B844-0E14E2AEF915, name="home"
EOF
dd if="$OUT/part_a.img" of="$G" bs=512 seek=2048 conv=notrunc status=none
dd if="$OUT/part_b.img" of="$G" bs=512 seek=34816 conv=notrunc status=none
cp "$G" "$OUT/gpt.img"

M=$W/mbr.img; truncate -s 64M "$M"
sfdisk -q "$M" <<'EOF'
label: dos
start=2048, size=32768, type=83
start=34816, size=90000, type=5
start=36864, size=32768, type=83
start=71680, size=32768, type=82
EOF
dd if="$OUT/part_a.img" of="$M" bs=512 seek=2048 conv=notrunc status=none
dd if="$OUT/part_b.img" of="$M" bs=512 seek=36864 conv=notrunc status=none
S=$W/swap.img; truncate -s 16M "$S"; mkswap -q "$S" >/dev/null
dd if="$S" of="$M" bs=512 seek=71680 conv=notrunc status=none
cp "$M" "$OUT/mbr.img"
echo "built gpt.img mbr.img"
fi

if [ "$ONLY" = all ] || [ "$ONLY" = btrfs ]; then
# ---- btrfs ---------------------------------------------------------------
BSRC=$W/bsrc
mkdir -p "$BSRC"
cp -a "$SRC/." "$BSRC/"
seq 1 300000 > "$BSRC/numbers.txt"
tar cf - /usr/share/doc 2>/dev/null | head -c 6000000 > "$BSRC/docs.tar" || true
cp /usr/lib/x86_64-linux-gnu/libc.so.6 "$BSRC/libc.bin"
head -c 1500 /dev/zero | tr '\0' 'a' > "$BSRC/tiny_compressible.txt"
( head -c 300000 /dev/urandom; seq 1 50000; head -c 300000 /dev/urandom ) > "$BSRC/mixed.bin"
# Hundreds of ordinary small text and JSON files, 60 bytes to 9 KB: the sizes btrfs compresses
# inside the metadata or into a single block, where compressors behave differently than on big files.
python3 - "$BSRC/smalltext" <<'PY'
import glob, json, os, random, sys
out = sys.argv[1]
os.makedirs(out)
random.seed(7)
sources = sorted(glob.glob("/usr/lib/python3*/**/*.py", recursive=True))[:4000]
random.shuffle(sources)
n = 0
for path in sources:
    if n >= 320: break
    try: data = open(path, "rb").read()
    except OSError: continue
    if len(data) < 9000: continue
    start = random.randrange(0, len(data) - 9000)
    size = random.choice([60, 90, 150, 300, 500, 800, 1000, 1024, 1500, 2000, 2047, 2048, 2049, 3000, 4095, 4096, 4097, 6000, 9000])
    open(os.path.join(out, "text_%03d_%d.py" % (n, size)), "wb").write(data[start:start + size])
    n += 1
words = ["name", "version", "description", "permissions", "background", "content_scripts", "matches", "icons", "action", "default_popup",
         "host_permissions", "storage", "tabs", "activeTab", "scripting", "service_worker", "js", "css", "run_at", "document_idle"]
for i in range(120):
    doc = {"manifest_version": 3, "name": "ext %d" % i, "version": "1.%d.%d" % (i % 7, i)}
    for k in range(random.randrange(2, 40)):
        key = random.choice(words)
        doc[key + ("_%d" % k if k % 3 == 0 else "")] = random.choice([
            [random.choice(words) for _ in range(random.randrange(1, 6))],
            {"16": "icons/icon16.png", "48": "icons/icon48.png", "128": "icons/icon128.png"},
            "https://*.example.com/*", random.randrange(100000), True])
    open(os.path.join(out, "manifest_%03d.json" % i), "w").write(json.dumps(doc, indent=random.choice([None, 2, 4])))
PY

btrfs_extras() {
  # Overwriting the middle of a file leaves older extents only partly referenced.
  dd if=/dev/urandom of="$MNT/random_3mb.bin" bs=4k seek=100 count=10 conv=notrunc status=none
  seq 1 3000 | dd of="$MNT/numbers.txt" bs=4k seek=50 conv=notrunc status=none
  sync
  fallocate -l 2M "$MNT/prealloc.bin"
  printf 'written' | dd of="$MNT/prealloc.bin" bs=1 seek=500000 conv=notrunc status=none
  truncate -s 10M "$MNT/hole_tail.bin"
  touch "$MNT/no_checksum.bin"; chattr +C "$MNT/no_checksum.bin"   # nodatacow => no data checksums
  head -c 100000 /dev/urandom > "$MNT/no_checksum.bin"
  btrfs subvolume create "$MNT/subvol_a" >/dev/null
  cp -a "$BSRC/docs" "$BSRC/hello.txt" "$MNT/subvol_a/"
  btrfs subvolume create "$MNT/subvol_a/nested" >/dev/null
  echo nested > "$MNT/subvol_a/nested/inside.txt"
  sync
  btrfs subvolume snapshot "$MNT/subvol_a" "$MNT/snap_a" >/dev/null
  echo "changed after snapshot" > "$MNT/subvol_a/hello.txt"
}
hook_zstd_high() {
  mount -o remount,compress-force=zstd:15 "$MNT"
  mkdir "$MNT/level15"
  cp -a "$BSRC/numbers.txt" "$BSRC/docs.tar" "$BSRC/libc.bin" "$BSRC/mixed.bin" "$MNT/level15/"
}
build_btrfs() { # <name> <size> <mount options> <hook or ''> <mkfs.btrfs args...>
  local name=$1 size=$2 opts=$3 hook=$4; shift 4
  local img=$W/$name.img
  rm -f "$img"; truncate -s "$size" "$img"
  mkfs.btrfs -q -f "$@" "$img"
  mount -o "loop,$opts" "$img" "$MNT"
  cp -a "$BSRC/." "$MNT/"
  sync
  btrfs_extras
  if [ -n "$hook" ]; then $hook; fi
  sync
  manifest "$MNT" "$OUT/$name.img"
  umount "$MNT"
  cp "$img" "$OUT/$name.img"
  echo "built $name.img"
}
build_btrfs btrfs_default     192M noatime ""                         -L btrfstest
build_btrfs btrfs_zlib        192M compress-force=zlib ""             -L zlib
build_btrfs btrfs_lzo         192M compress-force=lzo ""              -L lzo
build_btrfs btrfs_zstd        192M compress-force=zstd hook_zstd_high -L zstd
build_btrfs btrfs_small_nodes 192M noatime ""                         -L small -n 4096
build_btrfs btrfs_mixed       128M compress=zstd ""                   -L mixed -M
build_btrfs btrfs_xxhash      160M compress=zstd ""                   -L xxhash --csum xxhash
build_btrfs btrfs_sha256      160M noatime ""                         -L sha256 --csum sha256
build_btrfs btrfs_blake2      160M compress=lzo ""                    -L blake2 --csum blake2
fi

if [ "$ONLY" = all ] || [ "$ONLY" = xfs ]; then
# ---- xfs -----------------------------------------------------------------
XSRC=$W/xsrc
mkdir -p "$XSRC/midfiles"
cp -a "$SRC/." "$XSRC/"
seq 1 300000 > "$XSRC/numbers.txt"
for i in $(seq 1 40); do echo "mid $i" > "$XSRC/midfiles/entry_$i.txt"; done

xfs_extras() {
  fallocate -l 2M "$MNT/prealloc.bin"
  printf 'written' | dd of="$MNT/prealloc.bin" bs=1 seek=500000 conv=notrunc status=none
  truncate -s 10M "$MNT/hole_tail.bin"
  # Thousands of separate extents force the file's extent list into a multi-level tree.
  python3 - "$MNT/many_extents.bin" <<'PY'
import os, sys
with open(sys.argv[1], "wb") as f:
    for i in range(6000):
        f.seek(i * 8192)
        f.write(os.urandom(4096))
        if i % 200 == 0:
            f.flush(); os.fsync(f.fileno())
PY
}
hook_dirty() { # a copy taken while still mounted: its log has no unmount record
  xfs_freeze -f "$MNT"; xfs_freeze -u "$MNT"   # push everything so far out of the log
  echo "late change" > "$MNT/late.txt"; mkdir "$MNT/late_dir"; sync
  cp "$img" "$OUT/xfs_dirty.img"
  rm -r "$MNT/late.txt" "$MNT/late_dir"
}
build_xfs() { # <name> <size> <hook or ''> <mkfs.xfs args...>
  local name=$1 size=$2 hook=$3; shift 3
  local img=$W/$name.img
  rm -f "$img"; truncate -s "$size" "$img"
  mkfs.xfs -q -f "$@" "$img"
  mount -o loop "$img" "$MNT"
  cp -a "$XSRC/." "$MNT/"
  xfs_extras
  sync
  if [ -n "$hook" ]; then $hook; fi
  sync
  manifest "$MNT" "$OUT/$name.img"
  umount "$MNT"
  cp "$img" "$OUT/$name.img"
  echo "built $name.img ($(filefrag "$OUT/$name.img" >/dev/null 2>&1; echo ok))"
}
build_xfs xfs_default 300M "" -L xfstest
build_xfs xfs_1k      300M ""         -L xfs1k -b size=1024 -n size=8192
# The old v4 format: recent kernels refuse to mount it, so let mkfs.xfs fill it from a folder instead.
V=$W/v4src; mkdir "$V"; cp -a "$XSRC/." "$V/"
REAL_MNT=$MNT; MNT=$V; xfs_extras; MNT=$REAL_MNT
P=$W/xfs_v4.img; truncate -s 300M "$P"
if mkfs.xfs -q -f -m crc=0 -n ftype=0 -L xfsv4 -p "$V" "$P" 2>/dev/null; then
  manifest "$V" "$OUT/xfs_v4.img"
  cp "$P" "$OUT/xfs_v4.img"
  echo "built xfs_v4.img"
else
  echo "SKIPPED xfs_v4: this mkfs.xfs no longer supports the old v4 format"
fi
fi

if [ "$ONLY" = all ] || [ "$ONLY" = crypt ]; then
# ---- LUKS and LVM --------------------------------------------------------
PW="correct horse"
PW2="second password"
pidof systemd-udevd >/dev/null 2>&1 || export DM_DISABLE_UDEV=1
export LVM_SUPPRESS_FD_WARNINGS=1
fill_dev() { # <block device> <fs type> <label> <manifest prefix> <source tree>
  mke2fs -q -F -t "$2" -L "$3" "$1"
  mount "$1" "$MNT"
  cp -a "$5/." "$MNT/"
  sync
  manifest "$MNT" "$4"
  umount "$MNT"
}
build_luks() { # <name> <fs type> <cryptsetup luksFormat args...>
  local name=$1 fst=$2; shift 2
  local img=$W/$name.img
  rm -f "$img"; truncate -s 64M "$img"
  printf '%s' "$PW" | cryptsetup luksFormat -q --key-file - "$@" "$img"
  printf '%s' "$PW" | cryptsetup open --key-file - "$img" "t_$name"
  fill_dev "/dev/mapper/t_$name" "$fst" "$name" "$OUT/$name.img" "$SMALL"
  cryptsetup close "t_$name"
  cp "$img" "$OUT/$name.img"
  echo "built $name.img"
}
build_luks luks1_xts      ext4 --type luks1 --pbkdf-force-iterations 2000
build_luks luks1_cbc      ext2 --type luks1 --pbkdf-force-iterations 2000 --cipher aes-cbc-essiv:sha256 --key-size 256 --hash sha1
build_luks luks2_argon2id ext4 --type luks2 --pbkdf argon2id --pbkdf-memory 65536 --pbkdf-force-iterations 4 --pbkdf-parallel 4
build_luks luks2_argon2i  ext4 --type luks2 --pbkdf argon2i --pbkdf-memory 32768 --pbkdf-force-iterations 5 --pbkdf-parallel 1 --sector-size 4096
build_luks luks2_pbkdf2   ext4 --type luks2 --pbkdf pbkdf2 --pbkdf-force-iterations 2000 --hash sha512 --key-size 256
# a second password in another key slot
printf '%s' "$PW" > "$W/k1"; printf '%s' "$PW2" > "$W/k2"
cryptsetup luksAddKey -q --key-file "$W/k1" --pbkdf pbkdf2 --pbkdf-force-iterations 2000 "$OUT/luks2_pbkdf2.img" "$W/k2"

# LVM straight on a disk; "root" is grown later so it ends up in two separate pieces
BIGROOT=$W/bigroot; mkdir "$BIGROOT"; cp -a "$SMALL/." "$BIGROOT/"; head -c 24000000 /dev/urandom > "$BIGROOT/big.bin"
L=$W/lvm.img; truncate -s 96M "$L"
LD=$(losetup -f --show "$L"); LOOPS="$LOOPS $LD"
pvcreate -q -ff -y "$LD" >/dev/null
vgcreate -q testvg "$LD" >/dev/null
lvcreate -q -y -L 20M -n root testvg >/dev/null
lvcreate -q -y -L 12M -n home testvg >/dev/null
lvextend -q -L +16M testvg/root >/dev/null
fill_dev /dev/mapper/testvg-root ext4 lvroot "$OUT/lvm.img.root" "$BIGROOT"
fill_dev /dev/mapper/testvg-home ext2 lvhome "$OUT/lvm.img.home" "$SMALL"
vgchange -an testvg >/dev/null
losetup -d "$LD"
cp "$L" "$OUT/lvm.img"
echo "built lvm.img"

# The classic encrypted install: a boot partition, then LUKS holding LVM holding the filesystems
C=$W/luks_lvm.img; truncate -s 164M "$C"
sfdisk -q "$C" <<'EOF'
label: gpt
start=2048, size=32768, type=0FC63DAF-8483-4772-8E79-3D69D8477DE4, name="boot"
start=34816, size=294912, type=CA7D7CCB-63ED-4C53-861C-1742536059CC, name="cryptroot"
EOF
CD=$(losetup -f --show -P "$C"); LOOPS="$LOOPS $CD"
fill_dev "$CD"p1 ext4 boot "$OUT/luks_lvm.img.boot" "$SMALL"
printf '%s' "$PW" | cryptsetup luksFormat -q --key-file - --type luks2 --pbkdf argon2id --pbkdf-memory 65536 --pbkdf-force-iterations 4 --pbkdf-parallel 2 "$CD"p2
printf '%s' "$PW" | cryptsetup open --key-file - "$CD"p2 t_cl
pvcreate -q -ff -y /dev/mapper/t_cl >/dev/null
vgcreate -q cryptvg /dev/mapper/t_cl >/dev/null
lvcreate -q -y -L 64M -n root cryptvg >/dev/null
lvcreate -q -y -L 16M -n swap cryptvg >/dev/null
lvcreate -q -y -l 100%FREE -n data cryptvg >/dev/null
fill_dev /dev/mapper/cryptvg-root ext4 cryptroot "$OUT/luks_lvm.img.root" "$BIGROOT"
mkswap -q /dev/mapper/cryptvg-swap >/dev/null
fill_dev /dev/mapper/cryptvg-data ext2 cryptdata "$OUT/luks_lvm.img.data" "$SMALL"
vgchange -an cryptvg >/dev/null
cryptsetup close t_cl
losetup -d "$CD"
cp "$C" "$OUT/luks_lvm.img"
echo "built luks_lvm.img"
fi

if [ "$ONLY" = all ] || [ "$ONLY" = journal ]; then
# ---- disks copied in the middle of use: committed changes still only in the journal / log ----
# Keep the kernel from writing changed metadata back to its home location on its own, so that a copy
# taken right after "sync" holds those changes only in the journal, as after a crash or a pulled plug.
OLD_EXPIRE=$(cat /proc/sys/vm/dirty_expire_centisecs); OLD_WB=$(cat /proc/sys/vm/dirty_writeback_centisecs)
restore_vm() { echo "$OLD_EXPIRE" > /proc/sys/vm/dirty_expire_centisecs; echo "$OLD_WB" > /proc/sys/vm/dirty_writeback_centisecs; }
trap 'restore_vm; cleanup' EXIT
echo 360000 > /proc/sys/vm/dirty_expire_centisecs
echo 0 > /proc/sys/vm/dirty_writeback_centisecs

changes() { # what happens between the last clean unmount and the "crash"
  mkdir -p "$MNT/added/deep"
  for i in $(seq 1 300); do echo "new file $i" > "$MNT/added/file_$i.txt"; done
  head -c 3000000 /dev/urandom > "$MNT/added/deep/big.bin"
  echo "appended after the last clean unmount" >> "$MNT/readme.txt"
  rm -r "$MNT/sub"
  mv "$MNT/readme.txt" "$MNT/readme-renamed.txt"
  ln -s added/deep/big.bin "$MNT/link_to_big"
  printf '\300\073\071\230 starts with the journal magic number' > "$MNT/added/escape.bin"
  chmod 600 "$MNT/added/file_1.txt"
  touch -d '2021-06-07 08:09:10 UTC' "$MNT/added/file_2.txt"
}
churn() { # fill the journal several times over, so that it wraps around its end
  mkdir -p "$MNT/churn"
  for round in $(seq 1 ${CHURN_ROUNDS:-0}); do
    for i in $(seq 1 ${CHURN_FILES:-150}); do echo "$round $i" > "$MNT/churn/f_$i"; done; sync
    rm -f "$MNT"/churn/f_*; sync
  done
  rmdir "$MNT/churn"
}
dirty() { # <name> <size> <kind ext|xfs> <mount options> <mkfs args...>
  local name=$1 size=$2 kind=$3 mopts=$4; shift 4
  local img=$W/$name.img truth=$W/truth.img
  rm -f "$img"; truncate -s "$size" "$img"
  if [ "$kind" = ext ]; then mke2fs -q -F "$@" "$img"; else mkfs.xfs -q -f "$@" "$img"; fi
  mount -o loop "$img" "$MNT"; cp -a "$SMALL/." "$MNT/"; umount "$MNT"
  mount -o "loop,$mopts" "$img" "$MNT"
  churn
  changes
  sync
  cp "$img" "$OUT/$name.img"          # the "crash": a copy of the disk while still in use
  umount "$MNT"
  # What Linux shows for that copy: mounting it replays the journal.
  cp "$OUT/$name.img" "$truth"
  if [ "$kind" = ext ]; then mount -o loop "$truth" "$MNT"; else mount -o loop,nouuid "$truth" "$MNT"; fi
  manifest "$MNT" "$OUT/$name.img"
  umount "$MNT"; rm -f "$truth"
  if [ "$kind" = ext ]; then
    dumpe2fs -h "$OUT/$name.img" 2>/dev/null | grep -q needs_recovery && echo "built $name.img (journal needs replay)" || echo "WARNING: $name.img does not need replay"
  else
    echo "built $name.img"
  fi
}
dirty ext4_journal        64M  ext rw            -t ext4 -L journal
dirty ext4_journal_data   64M  ext data=journal  -t ext4 -L datajournal
dirty ext3_journal        64M  ext rw            -t ext3 -L journal3
dirty xfs_journal        300M  xfs rw            -L xfsjournal
dirty xfs_journal_1k     300M  xfs rw            -L xfsjournal1k -b size=1024
CHURN_ROUNDS=40 dirty ext4_journal_wrap  64M  ext rw  -t ext4 -L wrap -J size=4
CHURN_ROUNDS=60 CHURN_FILES=2000 dirty xfs_journal_wrap 300M  xfs rw  -L xfswrap
CHURN_ROUNDS=0
restore_vm
fi
ls -la "$OUT"/*.img
