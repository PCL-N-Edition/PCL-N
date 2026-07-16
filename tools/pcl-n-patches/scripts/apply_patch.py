#!/usr/bin/env python3
"""Apply a HDiffPatch file: old_binary + patch → new_binary."""

from __future__ import annotations

import argparse
import hashlib
import platform
import subprocess
import sys
from pathlib import Path


def sha256_file(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


def find_hpatchz(tools_hint: Path | None) -> Path:
    names = ["hpatchz.exe", "hpatchz"] if platform.system().lower().startswith("win") else ["hpatchz"]
    candidates: list[Path] = []
    if tools_hint:
        candidates.extend(tools_hint / n for n in names)
    root = Path(__file__).resolve().parents[1] / "tools" / "hdiffpatch"
    candidates.extend(root / n for n in names)
    for c in candidates:
        if c.is_file():
            return c
    # PATH
    from shutil import which

    for n in names:
        w = which(n)
        if w:
            return Path(w)
    raise FileNotFoundError("hpatchz not found. Run scripts/bootstrap_hdiffpatch.py first.")


def main() -> int:
    parser = argparse.ArgumentParser(description="Apply PCL-N HDiffPatch")
    parser.add_argument("--old", type=Path, required=True, help="Current binary")
    parser.add_argument("--patch", type=Path, required=True, help=".hdiff file")
    parser.add_argument("--out", type=Path, required=True, help="Output new binary")
    parser.add_argument("--expect-sha256", default=None, help="Optional integrity check")
    parser.add_argument("--tools-dir", type=Path, default=None)
    args = parser.parse_args()

    if not args.old.is_file():
        print(f"old binary missing: {args.old}", file=sys.stderr)
        return 1
    if not args.patch.is_file():
        print(f"patch missing: {args.patch}", file=sys.stderr)
        return 1

    hpatchz = find_hpatchz(args.tools_dir)
    args.out.parent.mkdir(parents=True, exist_ok=True)
    if args.out.exists():
        args.out.unlink()

    # hpatchz old_file diff_file out_new_file
    cmd = [str(hpatchz), str(args.old), str(args.patch), str(args.out)]
    print("Running:", " ".join(cmd))
    proc = subprocess.run(cmd, check=False)
    if proc.returncode != 0:
        print(f"hpatchz failed with code {proc.returncode}", file=sys.stderr)
        return proc.returncode or 1

    digest = sha256_file(args.out)
    print(f"Output SHA-256: {digest}")
    print(f"Output size: {args.out.stat().st_size}")
    if args.expect_sha256 and digest.lower() != args.expect_sha256.lower():
        print("SHA-256 mismatch!", file=sys.stderr)
        return 2
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
