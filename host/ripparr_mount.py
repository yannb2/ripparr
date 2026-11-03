#!/usr/bin/env python3
"""Mounts Blu-ray ISO files read-only for Ripparr, so a job reads the disc where it is.

Ripparr runs in a container that cannot mount anything (that would need a privileged
container). This small service runs on the host (the media VM) as root instead:

    Ripparr  --(unix socket)-->  ripparr_mount.py serve  --> mount -o loop,ro ISO /mnt/pc/iso-mounts/NAME

The mount lands under a folder the container already sees through its /mnt/pc:/mnt/pc:ro,rslave
volume ("rslave": mounts made on the host later show up inside the container too).

Only standard library. Run it by hand too:

    sudo ./ripparr_mount.py mount /downloads/film.iso     # prints where it is mounted
    sudo ./ripparr_mount.py list
    sudo ./ripparr_mount.py unmount /mnt/pc/iso-mounts/film-1a2b3c4d
    sudo ./ripparr_mount.py serve                          # what the systemd unit runs

Protocol: one JSON object per line on the socket, one JSON answer per line.
    {"op": "ping"}                    -> {"ok": true, "version": "1", "root": "..."}
    {"op": "mount", "iso": "/x.iso"}  -> {"ok": true, "path": "/mnt/pc/iso-mounts/x-1a2b3c4d"}
    {"op": "unmount", "path": "..."}  -> {"ok": true}
    {"op": "list"}                    -> {"ok": true, "mounts": [{"iso": "...", "path": "..."}]}
Errors are {"ok": false, "error": "what went wrong"}.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import socketserver
import subprocess
import sys
import threading
from pathlib import Path

VERSION = "1"

DEFAULT_SOCKET = "/run/ripparr-mount/mount.sock"
DEFAULT_ROOT = "/mnt/pc/iso-mounts"
DEFAULT_ALLOW = ["/downloads", "/mnt/pc"]

# Mount options: read-only, and nothing on the disc can run or act as a device.
MOUNT_OPTIONS = "loop,ro,nosuid,nodev,noexec"


class MountError(Exception):
    """A request we refuse or a mount that failed; the message goes back to Ripparr."""


class Mounter:
    """Everything that touches the system, in one place so the tests can replace `run`."""

    def __init__(self, root: str, allow: list[str], run=None, mountinfo: str = "/proc/self/mountinfo"):
        # Real paths, so a symlink can't lead a request outside them.
        self.root = Path(os.path.realpath(root))
        self.allow = [Path(os.path.realpath(a)) for a in allow]
        self.run = run or self._run
        self.mountinfo = mountinfo
        # One request at a time: two jobs mounting the same ISO must not race.
        self.lock = threading.Lock()

    @staticmethod
    def _run(args: list[str]) -> subprocess.CompletedProcess:
        return subprocess.run(args, capture_output=True, text=True, timeout=120)

    # ---- checks ----

    def check_iso(self, iso: str) -> Path:
        """The ISO's real path, after making sure it is an .iso file inside an allowed folder."""
        if not iso or not os.path.isabs(iso):
            raise MountError("The ISO path must be absolute.")
        real = Path(os.path.realpath(iso))
        if real.suffix.lower() != ".iso":
            raise MountError(f"Not an .iso file: {iso}")
        if not any(real.is_relative_to(a) for a in self.allow):
            raise MountError(f"{iso} is outside the allowed folders ({', '.join(map(str, self.allow))}).")
        if not real.is_file():
            raise MountError(f"No such file: {iso}")
        return real

    def check_mount_path(self, path: str) -> Path:
        """A path we may unmount: a folder directly inside our root, nothing else."""
        real = Path(os.path.realpath(path)) if path else Path()
        if not path or real.parent != self.root:
            raise MountError(f"Not one of our mounts: {path}")
        return real

    def mount_path_for(self, iso: Path) -> Path:
        """Root/name-hash: readable, and two ISOs with the same name in different folders don't clash."""
        name = re.sub(r"[^A-Za-z0-9._-]+", "_", iso.stem).strip("._")[:60] or "disc"
        digest = hashlib.sha1(str(iso).encode()).hexdigest()[:8]
        return self.root / f"{name}-{digest}"

    # ---- what is mounted now ----

    def mounts(self) -> dict[Path, str]:
        """Our mount points (under root) and their source, read from mountinfo."""
        found: dict[Path, str] = {}
        root = str(self.root)
        try:
            lines = Path(self.mountinfo).read_text().splitlines()
        except OSError:
            return found
        for line in lines:
            # Fields: id parent major:minor root MOUNT_POINT options ... - fstype SOURCE superoptions
            parts = line.split(" - ", 1)
            left = parts[0].split()
            if len(left) < 5:
                continue
            point = unescape(left[4])
            if os.path.dirname(point) != root:
                continue
            source = unescape(parts[1].split()[1]) if len(parts) == 2 and len(parts[1].split()) > 1 else ""
            found[Path(point)] = source
        return found

    def backing_file(self, point: Path, source: str) -> str:
        """For a loop mount, the ISO behind /dev/loopN (losetup knows it); else what mountinfo said."""
        if source.startswith("/dev/loop"):
            result = self.run(["losetup", "--noheadings", "--output", "BACK-FILE", source])
            if result.returncode == 0 and result.stdout.strip():
                return result.stdout.strip()
        return source

    # ---- the operations ----

    def mount(self, iso: str) -> Path:
        with self.lock:
            real = self.check_iso(iso)
            point = self.mount_path_for(real)
            if point in self.mounts():
                return point  # already mounted (a retried job): reuse it

            point.mkdir(parents=True, exist_ok=True)
            errors = []
            # Blu-rays are UDF 2.50; try UDF first, then let mount guess (DVD-style ISO 9660).
            for fs in (["-t", "udf"], []):
                result = self.run(["mount", "-o", MOUNT_OPTIONS, *fs, str(real), str(point)])
                if result.returncode == 0:
                    break
                lines = (result.stderr or result.stdout or "").strip().splitlines()
                # "mount: /mnt/pc/iso-mounts/x: wrong fs type, ..." -> "wrong fs type, ..."
                error = lines[-1].replace(f"mount: {point}: ", "") if lines else f"exit code {result.returncode}"
                if error not in errors:
                    errors.append(error)
            else:
                remove_folder(point)
                raise MountError("mount failed: " + "; ".join(errors))

            if not (point / "BDMV").is_dir():
                self.run(["umount", str(point)])
                remove_folder(point)
                raise MountError("Mounted, but there is no BDMV folder on this disc.")
            return point

    def unmount(self, path: str) -> None:
        with self.lock:
            point = self.check_mount_path(path)
            if point in self.mounts():
                result = self.run(["umount", str(point)])
                if result.returncode != 0:
                    # Still in use (a shell sitting in it?): detach it now, the kernel frees it when it can.
                    result = self.run(["umount", "--lazy", str(point)])
                    if result.returncode != 0:
                        raise MountError("umount failed: " + (result.stderr or result.stdout).strip())
            remove_folder(point)

    def list(self) -> list[dict]:
        return [{"iso": self.backing_file(p, s), "path": str(p)} for p, s in sorted(self.mounts().items())]

    def tidy(self) -> None:
        """At start: remove empty folders left under root by an earlier run (never touches a mount)."""
        if not self.root.is_dir():
            return
        mounted = self.mounts()
        for child in self.root.iterdir():
            if child.is_dir() and child not in mounted:
                remove_folder(child)

    # ---- one request ----

    def handle(self, request: dict) -> dict:
        try:
            op = request.get("op")
            if op == "ping":
                return {"ok": True, "version": VERSION, "root": str(self.root)}
            if op == "mount":
                return {"ok": True, "path": str(self.mount(str(request.get("iso", ""))))}
            if op == "unmount":
                self.unmount(str(request.get("path", "")))
                return {"ok": True}
            if op == "list":
                return {"ok": True, "mounts": self.list()}
            return {"ok": False, "error": f"Unknown op: {op!r}"}
        except MountError as e:
            return {"ok": False, "error": str(e)}
        except Exception as e:  # never let one bad request stop the service
            return {"ok": False, "error": f"{type(e).__name__}: {e}"}


def unescape(field: str) -> str:
    """mountinfo writes spaces and a few other characters as \\040-style octal escapes."""
    return re.sub(r"\\([0-7]{3})", lambda m: chr(int(m.group(1), 8)), field)


def remove_folder(path: Path) -> None:
    """rmdir only: an empty mount point goes, anything with files in it stays."""
    try:
        path.rmdir()
    except OSError:
        pass


# ---- the socket service ----

class Handler(socketserver.StreamRequestHandler):
    def handle(self):
        for raw in self.rfile:
            line = raw.strip()
            if not line:
                continue
            try:
                request = json.loads(line)
                if not isinstance(request, dict):
                    raise ValueError("expected a JSON object")
            except ValueError as e:
                answer = {"ok": False, "error": f"Bad request: {e}"}
            else:
                answer = self.server.mounter.handle(request)
                if request.get("op") in ("mount", "unmount"):
                    print(f"{request.get('op')} {request.get('iso') or request.get('path')}: "
                          f"{answer.get('path') or answer.get('error') or 'ok'}", flush=True)
            self.wfile.write((json.dumps(answer) + "\n").encode())
            self.wfile.flush()


class Server(socketserver.ThreadingMixIn, socketserver.UnixStreamServer):
    daemon_threads = True


def serve(mounter: Mounter, socket_path: str, gid: int) -> None:
    folder = os.path.dirname(socket_path)
    # Keep the folder (never delete it): the container bind-mounts the folder, not the socket.
    os.makedirs(folder, exist_ok=True)
    os.chown(folder, 0, gid)
    os.chmod(folder, 0o750)
    if os.path.exists(socket_path):
        os.remove(socket_path)

    mounter.root.mkdir(parents=True, exist_ok=True)
    mounter.tidy()
    server = Server(socket_path, Handler)
    server.mounter = mounter
    # Only root and the group (Ripparr's user, gid 1000) may talk to it.
    os.chown(socket_path, 0, gid)
    os.chmod(socket_path, 0o660)
    print(f"ripparr-mount {VERSION}: {socket_path}, mounts in {mounter.root}, "
          f"ISOs from {', '.join(map(str, mounter.allow))}", flush=True)
    try:
        server.serve_forever()
    finally:
        server.server_close()


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Mount Blu-ray ISO files read-only for Ripparr.")
    parser.add_argument("--root", default=DEFAULT_ROOT, help=f"where the ISOs are mounted (default {DEFAULT_ROOT})")
    parser.add_argument("--allow", action="append", help="folder ISOs may come from; repeat it (default /downloads and /mnt/pc)")
    sub = parser.add_subparsers(dest="command", required=True)
    s = sub.add_parser("serve", help="listen on the socket (what the systemd unit runs)")
    s.add_argument("--socket", default=DEFAULT_SOCKET)
    s.add_argument("--group", type=int, default=1000, help="gid allowed to use the socket (default 1000, Ripparr's user)")
    m = sub.add_parser("mount", help="mount one ISO and print where")
    m.add_argument("iso")
    u = sub.add_parser("unmount", help="unmount one of our mounts")
    u.add_argument("path")
    sub.add_parser("list", help="show what is mounted")
    args = parser.parse_args(argv)

    mounter = Mounter(args.root, args.allow or DEFAULT_ALLOW)
    if args.command == "serve":
        serve(mounter, args.socket, args.group)
        return 0

    answer = mounter.handle({"op": args.command, "iso": getattr(args, "iso", None), "path": getattr(args, "path", None)})
    if not answer["ok"]:
        print(answer["error"], file=sys.stderr)
        return 1
    if args.command == "mount":
        print(answer["path"])
    elif args.command == "list":
        for item in answer["mounts"]:
            print(f"{item['path']}  <-  {item['iso']}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
