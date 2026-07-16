#!/usr/bin/env python3
"""Download HDiffPatch prebuilt binaries into tools/hdiffpatch/."""

from __future__ import annotations

import argparse
import io
import os
import platform
import stat
import sys
import tarfile
import urllib.request
import zipfile
from pathlib import Path

# Pin a known HDiffPatch release with multi-platform assets.
# Override with --version if needed.
DEFAULT_VERSION = "v4.12.0"
RELEASE_API = "https://api.github.com/repos/sisong/HDiffPatch/releases/tags/{tag}"


def detect_triplet() -> tuple[str, str]:
    system = platform.system().lower()
    machine = platform.machine().lower()
    if system.startswith("win"):
        os_name = "windows"
    elif system == "darwin":
        os_name = "macos"
    elif system == "linux":
        os_name = "linux"
    else:
        raise SystemExit(f"Unsupported OS: {system}")

    if machine in ("x86_64", "amd64"):
        arch = "x64"
    elif machine in ("aarch64", "arm64"):
        arch = "arm64"
    else:
        arch = machine
    return os_name, arch


def http_json(url: str, token: str | None) -> dict:
    req = urllib.request.Request(url, headers={"User-Agent": "PCL-N-Patches/1.0"})
    if token:
        req.add_header("Authorization", f"Bearer {token}")
    with urllib.request.urlopen(req, timeout=60) as resp:
        import json

        return json.load(resp)


def download(url: str, dest: Path, token: str | None) -> None:
    req = urllib.request.Request(url, headers={"User-Agent": "PCL-N-Patches/1.0"})
    if token:
        req.add_header("Authorization", f"Bearer {token}")
    with urllib.request.urlopen(req, timeout=300) as resp:
        dest.write_bytes(resp.read())


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--version", default=DEFAULT_VERSION)
    parser.add_argument(
        "--tools-dir",
        type=Path,
        default=Path(__file__).resolve().parents[1] / "tools" / "hdiffpatch",
    )
    args = parser.parse_args()
    token = os.environ.get("GH_TOKEN") or os.environ.get("GITHUB_TOKEN")

    tools = args.tools_dir
    tools.mkdir(parents=True, exist_ok=True)
    hdiffz = tools / ("hdiffz.exe" if platform.system().lower().startswith("win") else "hdiffz")
    hpatchz = tools / ("hpatchz.exe" if platform.system().lower().startswith("win") else "hpatchz")
    if hdiffz.is_file() and hpatchz.is_file():
        print(f"Already present: {tools}")
        return 0

    os_name, arch = detect_triplet()
    print(f"Fetching HDiffPatch {args.version} for {os_name}-{arch}…")
    try:
        release = http_json(RELEASE_API.format(tag=args.version), token)
    except Exception as exc:  # noqa: BLE001
        print(
            f"WARNING: cannot query GitHub release ({exc}).\n"
            "Install HDiffPatch manually into tools/hdiffpatch/ "
            "(need hdiffz + hpatchz).",
            file=sys.stderr,
        )
        return 1

    assets = release.get("assets") or []
    # Prefer asset names containing os + arch hints.
    candidates = []
    for asset in assets:
        name = (asset.get("name") or "").lower()
        url = asset.get("browser_download_url")
        if not url:
            continue
        score = 0
        if os_name in name or (os_name == "windows" and "win" in name):
            score += 2
        if arch in name or (arch == "x64" and ("x64" in name or "x86_64" in name or "amd64" in name)):
            score += 2
        if name.endswith((".zip", ".tar.gz", ".tgz")):
            score += 1
        if score:
            candidates.append((score, name, url))
    candidates.sort(reverse=True)

    if not candidates:
        print("No suitable HDiffPatch asset found; listing:", file=sys.stderr)
        for asset in assets:
            print(" -", asset.get("name"), file=sys.stderr)
        return 1

    _, name, url = candidates[0]
    archive = tools / name
    print(f"Downloading {name}…")
    download(url, archive, token)

    # Extract binaries
    data = archive.read_bytes()
    extracted = 0
    if name.endswith(".zip"):
        with zipfile.ZipFile(io.BytesIO(data)) as zf:
            for info in zf.infolist():
                base = Path(info.filename).name.lower()
                if base in ("hdiffz", "hdiffz.exe", "hpatchz", "hpatchz.exe"):
                    target = tools / Path(info.filename).name
                    target.write_bytes(zf.read(info))
                    extracted += 1
    elif name.endswith((".tar.gz", ".tgz")):
        with tarfile.open(fileobj=io.BytesIO(data), mode="r:gz") as tf:
            for member in tf.getmembers():
                base = Path(member.name).name.lower()
                if base in ("hdiffz", "hpatchz"):
                    f = tf.extractfile(member)
                    if f is None:
                        continue
                    target = tools / Path(member.name).name
                    target.write_bytes(f.read())
                    extracted += 1
    else:
        print(f"Unknown archive type: {name}", file=sys.stderr)
        return 1

    for binary in tools.iterdir():
        if binary.name.lower().startswith(("hdiffz", "hpatchz")):
            binary.chmod(binary.stat().st_mode | stat.S_IXUSR | stat.S_IXGRP | stat.S_IXOTH)

    if extracted == 0:
        print("Archive downloaded but binaries not found inside.", file=sys.stderr)
        return 1

    print(f"Installed {extracted} tools into {tools}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
