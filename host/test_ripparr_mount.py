"""Tests for ripparr_mount.py, with `mount`/`umount` replaced by a fake that writes mountinfo lines."""

import os
import subprocess
import tempfile
import unittest
from pathlib import Path

import ripparr_mount as rm


class FakeSystem:
    """Plays mount, umount and losetup: keeps a mountinfo file like the kernel's."""

    def __init__(self, folder: Path, fail_udf=False, fail_all=False, bdmv=True):
        self.mountinfo = folder / "mountinfo"
        self.mountinfo.write_text("22 1 0:21 / / rw - ext4 /dev/vda rw\n")
        self.fail_udf, self.fail_all, self.bdmv = fail_udf, fail_all, bdmv
        self.calls = []

    def run(self, args):
        self.calls.append(args)
        if args[0] == "mount":
            if self.fail_all or (self.fail_udf and "udf" in args):
                return subprocess.CompletedProcess(args, 32, "", f"mount: {args[-1]}: wrong fs type, bad option, bad superblock\n")
            point = Path(args[-1])
            if self.bdmv:
                (point / "BDMV").mkdir(parents=True)
            with self.mountinfo.open("a") as f:
                f.write(f"99 22 7:0 / {str(point).replace(' ', chr(92) + '040')} ro - udf /dev/loop0 ro\n")
            return subprocess.CompletedProcess(args, 0, "", "")
        if args[0] == "umount":
            point = args[-1]
            lines = [l for l in self.mountinfo.read_text().splitlines()
                     if l.split(" - ")[0].split()[4].replace(chr(92) + "040", " ") != point]
            self.mountinfo.write_text("\n".join(lines) + "\n")
            bdmv = Path(point) / "BDMV"
            if bdmv.is_dir():
                bdmv.rmdir()
            return subprocess.CompletedProcess(args, 0, "", "")
        if args[0] == "losetup":
            return subprocess.CompletedProcess(args, 0, "/downloads/film.iso\n", "")
        raise AssertionError(args)


class MounterTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.base = Path(self.tmp.name)
        self.downloads = self.base / "downloads"
        self.downloads.mkdir()
        self.iso = self.downloads / "Film (2021).iso"
        self.iso.write_bytes(b"\0" * 2048)
        self.root = self.base / "iso-mounts"

    def tearDown(self):
        self.tmp.cleanup()

    def mounter(self, **fake):
        self.fake = FakeSystem(self.base, **fake)
        return rm.Mounter(str(self.root), [str(self.downloads)], run=self.fake.run, mountinfo=str(self.fake.mountinfo))

    def test_mounts_read_only_under_root(self):
        m = self.mounter()
        point = m.mount(str(self.iso))
        self.assertEqual(self.root.resolve(), point.parent)
        self.assertTrue(point.name.startswith("Film_2021-"))
        mount = self.fake.calls[0]
        self.assertEqual(["mount", "-o", "loop,ro,nosuid,nodev,noexec", "-t", "udf"], mount[:5])
        self.assertEqual([{"iso": "/downloads/film.iso", "path": str(point)}], m.list())

    def test_mounting_twice_reuses_the_mount(self):
        m = self.mounter()
        self.assertEqual(m.mount(str(self.iso)), m.mount(str(self.iso)))
        self.assertEqual(1, sum(1 for c in self.fake.calls if c[0] == "mount"))

    def test_falls_back_from_udf(self):
        m = self.mounter(fail_udf=True)
        m.mount(str(self.iso))
        self.assertNotIn("-t", self.fake.calls[1])

    def test_failed_mount_says_why_and_leaves_no_folder(self):
        m = self.mounter(fail_all=True)
        with self.assertRaises(rm.MountError) as e:
            m.mount(str(self.iso))
        # The same reason from both tries, said once, without the mount point.
        self.assertEqual("mount failed: wrong fs type, bad option, bad superblock", str(e.exception))
        self.assertEqual([], list(self.root.iterdir()))

    def test_a_disc_without_bdmv_is_unmounted(self):
        m = self.mounter(bdmv=False)
        with self.assertRaises(rm.MountError):
            m.mount(str(self.iso))
        self.assertEqual("umount", self.fake.calls[-1][0])
        self.assertEqual([], m.list())

    def test_refuses_files_outside_the_allowed_folders(self):
        m = self.mounter()
        other = self.base / "other.iso"
        other.write_bytes(b"x")
        for bad in [str(other), "relative.iso", str(self.downloads / "notes.txt"), str(self.downloads / "missing.iso")]:
            with self.subTest(bad=bad), self.assertRaises(rm.MountError):
                m.mount(bad)

    def test_refuses_a_symlink_that_leads_outside(self):
        m = self.mounter()
        outside = self.base / "secret.iso"
        outside.write_bytes(b"x")
        link = self.downloads / "link.iso"
        os.symlink(outside, link)
        with self.assertRaises(rm.MountError):
            m.mount(str(link))

    def test_unmounts_only_its_own_mounts(self):
        m = self.mounter()
        point = m.mount(str(self.iso))
        with self.assertRaises(rm.MountError):
            m.unmount("/")
        with self.assertRaises(rm.MountError):
            m.unmount(str(self.root))
        m.unmount(str(point))
        self.assertFalse(point.exists())
        self.assertEqual([], m.list())

    def test_handle_answers_errors_as_json(self):
        m = self.mounter()
        self.assertEqual({"ok": True, "version": rm.VERSION, "root": str(self.root.resolve())}, m.handle({"op": "ping"}))
        self.assertFalse(m.handle({"op": "mount", "iso": "/etc/passwd"})["ok"])
        self.assertFalse(m.handle({"op": "nope"})["ok"])
        self.assertTrue(m.handle({"op": "mount", "iso": str(self.iso)})["path"].startswith(str(self.root.resolve())))

    def test_tidy_removes_empty_leftovers_only(self):
        m = self.mounter()
        point = m.mount(str(self.iso))
        (self.root / "old-empty").mkdir()
        m.tidy()
        self.assertFalse((self.root / "old-empty").exists())
        self.assertTrue(point.exists())

    def test_unescape(self):
        self.assertEqual("/mnt/pc/a b", rm.unescape("/mnt/pc/a\\040b"))


if __name__ == "__main__":
    unittest.main()
