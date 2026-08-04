#!/usr/bin/env python3
"""Re-encode a zip as store (method 0) for the C launcher zip reader."""

from __future__ import annotations

import argparse
import tempfile
import zipfile
from pathlib import Path


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("source", type=Path)
    parser.add_argument("dest", type=Path)
    args = parser.parse_args()

    source = args.source.resolve()
    dest = args.dest.resolve()
    if not source.is_file():
        raise SystemExit(f"source zip missing: {source}")

    dest.parent.mkdir(parents=True, exist_ok=True)
    if dest.exists():
        dest.unlink()

    with tempfile.TemporaryDirectory(prefix="pcln-store-") as tmp:
        root = Path(tmp)
        with zipfile.ZipFile(source, "r") as zin:
            zin.extractall(root)
        files = sorted(
            (p.relative_to(root).as_posix(), p)
            for p in root.rglob("*")
            if p.is_file()
        )
        with zipfile.ZipFile(dest, "w", compression=zipfile.ZIP_STORED) as zout:
            for relative, path in files:
                info = zipfile.ZipInfo(relative)
                info.compress_type = zipfile.ZIP_STORED
                info.date_time = (1980, 1, 1, 0, 0, 0)
                with path.open("rb") as src, zout.open(info, "w") as out:
                    while True:
                        block = src.read(1024 * 1024)
                        if not block:
                            break
                        out.write(block)

    print(f"Store-repacked {source.name} -> {dest} ({dest.stat().st_size} bytes)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
