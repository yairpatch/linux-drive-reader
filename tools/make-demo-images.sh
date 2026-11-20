#!/bin/bash
# Demo disk images for the website screenshots: a btrfs laptop disk and an encrypted (LUKS + LVM) one.
# Development only; needs Linux and root.  usage: sudo bash make-demo-images.sh <output-dir>
set -euo pipefail
OUT="$1"; W=$(mktemp -d); MNT=$W/mnt; mkdir -p "$OUT" "$MNT"
export DM_DISABLE_UDEV=1 LVM_SUPPRESS_FD_WARNINGS=1
LOOPS=""
cleanup() { umount -R "$MNT" 2>/dev/null || true; vgchange -an demovg >/dev/null 2>&1 || true; cryptsetup close demo_crypt 2>/dev/null || true; for l in $LOOPS; do losetup -d $l 2>/dev/null || true; done; rm -rf "$W"; }
trap cleanup EXIT
R() { head -c "$1" /dev/urandom > "$2"; }
home_tree() {
  local h=$1/alex
  mkdir -p $h/{Documents/{Invoices,Notes},Pictures/{2025,2026},Music,Downloads,Projects/website/src,Videos,.config/nvim,.local/share,.ssh}
  R 184000 $h/Documents/resume.pdf; R 92000 $h/Documents/Invoices/invoice-2026-03.pdf; R 88000 $h/Documents/Invoices/invoice-2026-04.pdf
  printf '# Shopping\n- milk\n- coffee\n' > $h/Documents/Notes/todo.md; seq 1 400 | sed 's/^/line /' > $h/Documents/Notes/journal.txt
  n=0; for d in 0312 0405 0517 0602 0714 0821 0909 1015; do n=$((n+1)); R $((2100000 + n*137000)) $h/Pictures/2026/IMG_2026${d}_1432.jpg; done
  for i in 1 2 3; do R $((1800000 + i*90000)) $h/Pictures/2025/holiday_$i.jpg; done
  R 7400000 $h/Music/demo-track.flac; R 31000000 $h/Videos/birthday.mp4; R 5200000 $h/Downloads/notes-backup.tar.gz
  printf '{\n  "name": "website",\n  "version": "1.0.0"\n}\n' > $h/Projects/website/package.json
  printf '<!doctype html>\n<title>Hello</title>\n' > $h/Projects/website/src/index.html
  printf 'set number\n' > $h/.config/nvim/init.vim; printf 'export EDITOR=nvim\n' > $h/.bashrc
  printf 'ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIDemoDemoDemoDemoDemoDemoDemo alex@laptop\n' > $h/.ssh/id_ed25519.pub
  chown -R 1000:1000 $h; chmod 700 $h/.ssh
}
root_tree() { mkdir -p $1/{etc,usr/bin,var/log,boot,home,opt,srv,tmp}; printf 'NAME="Ubuntu"\nVERSION="26.04 LTS"\n' > $1/etc/os-release; printf 'laptop\n' > $1/etc/hostname; seq 1 2000 | sed 's/^/Nov 18 kernel: event /' > $1/var/log/syslog; chmod 1777 $1/tmp; }

B=$W/laptop.img; truncate -s 420M $B
sfdisk -q $B <<'EOF'
label: gpt
start=2048, size=65536, type=C12A7328-F81F-11D2-BA4B-00A0C93EC93B, name="EFI"
start=67584, type=0FC63DAF-8483-4772-8E79-3D69D8477DE4, name="ubuntu"
EOF
L=$(losetup -f --show -P $B); LOOPS="$LOOPS $L"
mkfs.vfat -n EFI ${L}p1 >/dev/null
mkfs.btrfs -q -f -L ubuntu ${L}p2
mount -o compress=zstd ${L}p2 $MNT
btrfs subvolume create $MNT/@ >/dev/null; btrfs subvolume create $MNT/@home >/dev/null; btrfs subvolume create $MNT/@snapshots >/dev/null
root_tree $MNT/@; home_tree $MNT/@home
btrfs subvolume snapshot -r $MNT/@home $MNT/@snapshots/home-2026-11-01 >/dev/null
sync; umount $MNT; losetup -d $L; LOOPS=""
cp $B "$OUT/laptop.img"; echo built laptop.img

C=$W/encrypted-laptop.img; truncate -s 380M $C
sfdisk -q $C <<'EOF'
label: gpt
start=2048, size=65536, type=0FC63DAF-8483-4772-8E79-3D69D8477DE4, name="boot"
start=67584, type=CA7D7CCB-63ED-4C53-861C-1742536059CC, name="cryptroot"
EOF
L=$(losetup -f --show -P $C); LOOPS="$LOOPS $L"
mke2fs -q -F -t ext4 -L boot ${L}p1
printf 'demo' | cryptsetup luksFormat -q --key-file - --type luks2 --pbkdf argon2id --pbkdf-memory 65536 --pbkdf-force-iterations 4 ${L}p2
printf 'demo' | cryptsetup open --key-file - ${L}p2 demo_crypt
pvcreate -q -ff -y /dev/mapper/demo_crypt >/dev/null; vgcreate -q demovg /dev/mapper/demo_crypt >/dev/null
lvcreate -q -y -L 120M -n root demovg >/dev/null; lvcreate -q -y -L 32M -n swap demovg >/dev/null; lvcreate -q -y -l 100%FREE -n home demovg >/dev/null
mke2fs -q -F -t ext4 -L root /dev/mapper/demovg-root; mount /dev/mapper/demovg-root $MNT; root_tree $MNT; umount $MNT
mkswap -q /dev/mapper/demovg-swap >/dev/null
mke2fs -q -F -t ext4 -L home /dev/mapper/demovg-home; mount /dev/mapper/demovg-home $MNT; home_tree $MNT; umount $MNT
vgchange -an demovg >/dev/null; cryptsetup close demo_crypt; losetup -d $L; LOOPS=""
cp $C "$OUT/encrypted-laptop.img"; echo built encrypted-laptop.img
