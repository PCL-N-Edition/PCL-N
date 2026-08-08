#!/usr/bin/env python3
"""Create the canonical launcher update archive from a scatter build.

The build artifact deliberately keeps the expanded install tree and optional
single-file portable binary in separate directories.  This script is the only
place that creates the archive consumed by the updater, so CI, beta and stable
releases cannot accidentally package installer-only siblings.
"""

from __future__ import annotations

import argparse
import stat
import tarfile
import zipfile
from pathlib import Path


FORBIDDEN_SUFFIXES = (".pdb", ".dbg")
PORTABLE_NAMES = {"PCL-N-Portable", "PCL-N-Portable.exe"}


def _payload_root(artifact: Path, platform: str) -> Path:
    if platform == "macos":
        return artifact / "PCL N.app" / "Contents" / "MacOS"
    return artifact


def validate_scatter(artifact: Path, platform: str) -> None:
    payload = _payload_root(artifact, platform)
    binary_name = "PCL-N-Edition.exe" if platform == "windows" else "PCL-N-Edition"
    host_name = "PCL-N-Host.exe" if platform == "windows" else "PCL-N-Host"

    required = [
        payload / binary_name,
        payload / "host" / host_name,
        payload / "pcln-layout",
    ]
    for path in required:
        if not path.is_file() or path.stat().st_size == 0:
            raise FileNotFoundError(f"scatter payload is missing required file: {path}")

    native = payload / "native"
    if not native.is_dir() or not any(path.is_file() for path in native.rglob("*")):
        raise FileNotFoundError(f"scatter payload has no expanded native runtime: {native}")

    for path in artifact.rglob("*"):
        if not path.is_file():
            continue
        if path.name in PORTABLE_NAMES:
            raise ValueError(
                f"portable single-file binary leaked into the scatter tree: {path}"
            )
        if path.suffix.lower() == ".zip":
            raise ValueError(f"nested zip leaked into the expanded scatter tree: {path}")
        if path.name.lower().endswith(FORBIDDEN_SUFFIXES):
            raise ValueError(f"debug symbol leaked into the release tree: {path}")


def _write_zip(artifact: Path, output: Path) -> None:
    with zipfile.ZipFile(
        output,
        mode="w",
        compression=zipfile.ZIP_DEFLATED,
        compresslevel=9,
        allowZip64=True,
    ) as archive:
        for path in sorted(artifact.rglob("*")):
            if not path.is_file():
                continue
            relative = path.relative_to(artifact).as_posix()
            info = zipfile.ZipInfo.from_file(path, arcname=relative)
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = (stat.S_IMODE(path.stat().st_mode) & 0xFFFF) << 16
            with path.open("rb") as source, archive.open(info, "w", force_zip64=True) as target:
                while chunk := source.read(1024 * 1024):
                    target.write(chunk)


def _write_tar_gz(artifact: Path, output: Path, platform: str) -> None:
    with tarfile.open(output, mode="w:gz", compresslevel=9) as archive:
        if platform == "macos":
            app = artifact / "PCL N.app"
            if not app.is_dir():
                raise FileNotFoundError(f"macOS application bundle is missing: {app}")
            archive.add(app, arcname="PCL N.app", recursive=True)
            return
        for path in sorted(artifact.iterdir(), key=lambda item: item.name):
            archive.add(path, arcname=path.name, recursive=True)


def create_archive(artifact: Path, output: Path, platform: str) -> None:
    artifact = artifact.resolve()
    output = output.resolve()
    if not artifact.is_dir():
        raise NotADirectoryError(f"artifact directory does not exist: {artifact}")
    if output.is_relative_to(artifact):
        raise ValueError("output archive must be outside the scatter artifact")

    validate_scatter(artifact, platform)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.unlink(missing_ok=True)
    if platform == "windows":
        if output.suffix.lower() != ".zip":
            raise ValueError("Windows update archive must use the .zip extension")
        _write_zip(artifact, output)
    else:
        if not output.name.endswith(".tar.gz"):
            raise ValueError("Unix update archive must use the .tar.gz extension")
        _write_tar_gz(artifact, output, platform)
    if not output.is_file() or output.stat().st_size == 0:
        raise RuntimeError(f"update archive was not created: {output}")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--artifact", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--platform", required=True, choices=("windows", "linux", "macos"))
    args = parser.parse_args()
    create_archive(args.artifact, args.output, args.platform)
    size_mib = args.output.resolve().stat().st_size / (1024 * 1024)
    print(f"Created canonical {args.platform} update archive: {args.output} ({size_mib:.2f} MiB)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
