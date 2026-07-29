#!/usr/bin/env python3
"""Pack NativeAOT's external native dependencies for first-start extraction."""

from __future__ import annotations

import argparse
from pathlib import Path
import zipfile


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("root", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--binary", required=True)
    args = parser.parse_args()

    root = args.root.resolve()
    output = args.output.resolve()
    if not (root / args.binary).is_file():
        raise SystemExit(f"NativeAOT executable is missing: {root / args.binary}")

    excluded_names = {
        args.binary.casefold(),
        "pcl.desktop".casefold(),
        "pcl.desktop.exe".casefold(),
    }
    excluded_suffixes = {".pdb", ".xml", ".dbg"}
    files: list[tuple[str, Path]] = []
    for path in root.rglob("*"):
        if not path.is_file():
            continue
        relative = path.relative_to(root).as_posix()
        if path.name.casefold() in excluded_names:
            continue
        if path.suffix.casefold() in excluded_suffixes:
            continue
        if path.name.startswith(".pcln-"):
            continue
        files.append((relative, path))
    files.sort(key=lambda item: item[0].casefold())

    output.parent.mkdir(parents=True, exist_ok=True)
    output.unlink(missing_ok=True)
    with zipfile.ZipFile(
        output,
        "w",
        compression=zipfile.ZIP_DEFLATED,
        compresslevel=9,
    ) as archive:
        for relative, path in files:
            info = zipfile.ZipInfo(relative)
            info.date_time = (1980, 1, 1, 0, 0, 0)
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = (path.stat().st_mode & 0xFFFF) << 16
            with path.open("rb") as source, archive.open(info, "w") as target:
                for block in iter(lambda: source.read(1024 * 1024), b""):
                    target.write(block)

    size_mb = output.stat().st_size / (1024 * 1024)
    print(f"Packed {len(files)} NativeAOT runtime files: {output} ({size_mb:.1f} MB)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
