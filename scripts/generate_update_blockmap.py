#!/usr/bin/env python3
"""Build signed content-addressed block maps for scatter and single-file updates."""

from __future__ import annotations

import argparse
import gzip
import hashlib
import json
import shutil
import stat
import tarfile
import tempfile
import zipfile
from pathlib import Path


FORMAT_VERSION = 1
LAYOUT = "pcln-blockmap-v1"
FILE_LAYOUT = "pcln-blockmap-file-v1"
ALGORITHM = "pcln-fastcdc-v1"
COMPRESSION = "gzip"
MIN_CHUNK = 256 * 1024
AVG_CHUNK = 1024 * 1024
MAX_CHUNK = 2 * 1024 * 1024
EARLY_MASK = (1 << 21) - 1
LATE_MASK = (1 << 19) - 1
UINT64_MASK = (1 << 64) - 1


def _splitmix64(value: int) -> int:
    value = (value + 0x9E3779B97F4A7C15) & UINT64_MASK
    value = ((value ^ (value >> 30)) * 0xBF58476D1CE4E5B9) & UINT64_MASK
    value = ((value ^ (value >> 27)) * 0x94D049BB133111EB) & UINT64_MASK
    return value ^ (value >> 31)


GEAR_TABLE = tuple(_splitmix64(index) for index in range(256))


def _safe_extract(archive: Path, destination: Path) -> None:
    destination.mkdir(parents=True, exist_ok=True)
    root = destination.resolve()
    if archive.name.endswith(".zip"):
        with zipfile.ZipFile(archive) as source:
            for entry in source.infolist():
                candidate = (destination / entry.filename).resolve()
                if not candidate.is_relative_to(root):
                    raise ValueError(f"zip entry escapes package root: {entry.filename}")
            source.extractall(destination)
            for entry in source.infolist():
                if entry.is_dir():
                    continue
                mode = (entry.external_attr >> 16) & 0xFFFF
                if mode:
                    (destination / entry.filename).chmod(stat.S_IMODE(mode))
    else:
        with tarfile.open(archive, mode="r:*") as source:
            source.extractall(destination, filter="data")


def _flatten_package_root(root: Path) -> None:
    children = list(root.iterdir())
    if len(children) != 1 or not children[0].is_dir():
        return
    only = children[0]
    recognizable = (
        (only / "pcln-layout").is_file()
        or (only / "host").is_dir()
        or (only / "PCL-N-Edition").is_file()
        or (only / "PCL-N-Edition.exe").is_file()
        or (only / "Contents" / "MacOS" / "PCL-N-Edition").is_file()
    )
    if not recognizable:
        return
    for item in only.iterdir():
        shutil.move(str(item), str(root / item.name))
    only.rmdir()


def _should_ignore(path: Path) -> bool:
    name = path.name
    return (
        name in {"pcln-install-kind", ".pcln-old", ".pcln-new"}
        or name.endswith((".pcln-old", ".pcln-new", ".update", ".pdb", ".dbg"))
        or (name.startswith(".") and name != "pcln-layout")
    )


def _flush_chunk(
    data: bytearray,
    output_root: Path,
) -> tuple[dict, bool]:
    raw = bytes(data)
    sha256 = hashlib.sha256(raw).hexdigest()
    compressed = gzip.compress(raw, compresslevel=9, mtime=0)
    relative = Path("block") / sha256[:2] / sha256
    target = output_root / relative
    created = False
    if target.exists():
        if target.stat().st_size != len(compressed):
            raise ValueError(f"block collision or corrupt cache: {target}")
    else:
        target.parent.mkdir(parents=True, exist_ok=True)
        temporary = target.with_suffix(".tmp")
        temporary.write_bytes(compressed)
        temporary.replace(target)
        created = True
    return (
        {
            "sha256": sha256,
            "size": len(raw),
            "compressedSize": len(compressed),
            "path": relative.as_posix(),
        },
        created,
    )


def chunk_file(path: Path, output_root: Path) -> tuple[str, int, list[dict], int, int]:
    file_hash = hashlib.sha256()
    file_size = 0
    rolling = 0
    buffer = bytearray()
    chunks: list[dict] = []
    created_blocks = 0
    created_bytes = 0

    def flush() -> None:
        nonlocal rolling, created_blocks, created_bytes
        if not buffer:
            return
        chunk, created = _flush_chunk(buffer, output_root)
        chunks.append(chunk)
        if created:
            created_blocks += 1
            created_bytes += chunk["compressedSize"]
        buffer.clear()
        rolling = 0

    with path.open("rb") as source:
        while data := source.read(128 * 1024):
            file_hash.update(data)
            file_size += len(data)
            for value in data:
                buffer.append(value)
                rolling = ((rolling << 1) + GEAR_TABLE[value]) & UINT64_MASK
                length = len(buffer)
                if length < MIN_CHUNK:
                    continue
                mask = EARLY_MASK if length < AVG_CHUNK else LATE_MASK
                if (rolling & mask) == 0 or length >= MAX_CHUNK:
                    flush()
    flush()
    if file_size == 0:
        chunk, created = _flush_chunk(bytearray(), output_root)
        chunks.append(chunk)
        if created:
            created_blocks += 1
            created_bytes += chunk["compressedSize"]
    return file_hash.hexdigest(), file_size, chunks, created_blocks, created_bytes


def _manifest_sha256(files: list[dict]) -> str:
    canonical = "".join(
        f"{entry['path']}\t{entry['sha256']}\t{entry['size']}\n"
        for entry in sorted(files, key=lambda item: item["path"])
    )
    return hashlib.sha256(canonical.encode("utf-8")).hexdigest()


def build_blockmap(
    archive: Path,
    output_root: Path,
    *,
    target_tag: str,
    target_version: str,
    runtime_id: str,
    runtime_variant: str,
    configuration: str,
) -> Path:
    archive = archive.resolve()
    output_root = output_root.resolve()
    if not archive.is_file():
        raise FileNotFoundError(archive)
    output_root.mkdir(parents=True, exist_ok=True)

    with tempfile.TemporaryDirectory(prefix="pcln-blockmap-") as temporary:
        tree = Path(temporary) / "tree"
        _safe_extract(archive, tree)
        _flatten_package_root(tree)
        entries: list[dict] = []
        total_blocks = 0
        referenced_compressed_bytes = 0
        created_blocks = 0
        created_bytes = 0
        for path in sorted(tree.rglob("*")):
            if not path.is_file() or _should_ignore(path):
                continue
            relative = path.relative_to(tree).as_posix()
            sha256, size, chunks, new_count, new_bytes = chunk_file(path, output_root)
            entries.append(
                {
                    "path": relative,
                    "sha256": sha256,
                    "size": size,
                    "unixMode": stat.S_IMODE(path.stat().st_mode),
                    "chunks": chunks,
                }
            )
            total_blocks += len(chunks)
            referenced_compressed_bytes += sum(chunk["compressedSize"] for chunk in chunks)
            created_blocks += new_count
            created_bytes += new_bytes
        if not entries:
            raise ValueError(f"update archive has no files: {archive}")

    stem = archive.name[:-7] if archive.name.endswith(".tar.gz") else archive.stem
    manifest = {
        "formatVersion": FORMAT_VERSION,
        "layout": LAYOUT,
        "algorithm": ALGORITHM,
        "compression": COMPRESSION,
        "blockBasePath": "/v1/updates/block",
        "targetTag": target_tag,
        "targetVersion": target_version,
        "runtimeId": runtime_id,
        "runtimeVariant": runtime_variant,
        "configuration": configuration,
        "targetAssetName": archive.name,
        "targetManifestSha256": _manifest_sha256(entries),
        "targetFiles": entries,
        "stats": {
            "fileCount": len(entries),
            "blockReferences": total_blocks,
            "referencedCompressedBytes": referenced_compressed_bytes,
            "newUniqueBlocks": created_blocks,
            "newUniqueCompressedBytes": created_bytes,
            "chunkMin": MIN_CHUNK,
            "chunkAverage": AVG_CHUNK,
            "chunkMax": MAX_CHUNK,
        },
    }
    manifest_path = output_root / "manifests" / f"{stem}.blockmap.json"
    manifest_path.parent.mkdir(parents=True, exist_ok=True)
    manifest_path.write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    return manifest_path


def build_file_blockmap(
    source: Path,
    output_root: Path,
    *,
    target_asset_name: str,
    entry_name: str,
    target_tag: str,
    target_version: str,
    runtime_id: str,
    runtime_variant: str,
    configuration: str,
) -> Path:
    source = source.resolve()
    output_root = output_root.resolve()
    if not source.is_file():
        raise FileNotFoundError(source)
    if not target_asset_name or Path(target_asset_name).name != target_asset_name:
        raise ValueError("target asset name must be a file name")
    normalized_entry = entry_name.replace("\\", "/").strip("/")
    if not normalized_entry or any(part in {"", ".", ".."} for part in normalized_entry.split("/")):
        raise ValueError("entry name must be a safe relative path")

    output_root.mkdir(parents=True, exist_ok=True)
    sha256, size, chunks, created_blocks, created_bytes = chunk_file(source, output_root)
    entries = [
        {
            "path": normalized_entry,
            "sha256": sha256,
            "size": size,
            "unixMode": stat.S_IMODE(source.stat().st_mode),
            "chunks": chunks,
        }
    ]
    manifest = {
        "formatVersion": FORMAT_VERSION,
        "layout": FILE_LAYOUT,
        "algorithm": ALGORITHM,
        "compression": COMPRESSION,
        "blockBasePath": "/v1/updates/block",
        "targetTag": target_tag,
        "targetVersion": target_version,
        "runtimeId": runtime_id,
        "runtimeVariant": runtime_variant,
        "configuration": configuration,
        "targetAssetName": target_asset_name,
        "targetManifestSha256": _manifest_sha256(entries),
        "targetFiles": entries,
        "stats": {
            "fileCount": 1,
            "blockReferences": len(chunks),
            "referencedCompressedBytes": sum(chunk["compressedSize"] for chunk in chunks),
            "newUniqueBlocks": created_blocks,
            "newUniqueCompressedBytes": created_bytes,
            "chunkMin": MIN_CHUNK,
            "chunkAverage": AVG_CHUNK,
            "chunkMax": MAX_CHUNK,
        },
    }
    stem = target_asset_name[:-4] if target_asset_name.lower().endswith(".exe") else Path(target_asset_name).stem
    manifest_path = output_root / "manifests" / f"{stem}.blockmap.json"
    manifest_path.parent.mkdir(parents=True, exist_ok=True)
    manifest_path.write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    return manifest_path


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    source = parser.add_mutually_exclusive_group(required=True)
    source.add_argument("--archive", type=Path)
    source.add_argument("--file", type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--target-asset-name")
    parser.add_argument("--entry-name")
    parser.add_argument("--target-tag", required=True)
    parser.add_argument("--target-version", required=True)
    parser.add_argument("--runtime-id", required=True)
    parser.add_argument("--runtime-variant", required=True, choices=("SelfContained", "NoRuntime"))
    parser.add_argument("--configuration", required=True, choices=("Release", "Beta", "CI"))
    args = parser.parse_args()
    if args.file is not None:
        if not args.target_asset_name or not args.entry_name:
            parser.error("--file requires --target-asset-name and --entry-name")
        manifest = build_file_blockmap(
            args.file,
            args.output,
            target_asset_name=args.target_asset_name,
            entry_name=args.entry_name,
            target_tag=args.target_tag,
            target_version=args.target_version,
            runtime_id=args.runtime_id,
            runtime_variant=args.runtime_variant,
            configuration=args.configuration,
        )
    else:
        manifest = build_blockmap(
            args.archive,
            args.output,
            target_tag=args.target_tag,
            target_version=args.target_version,
            runtime_id=args.runtime_id,
            runtime_variant=args.runtime_variant,
            configuration=args.configuration,
        )
    print(f"Created block map: {manifest}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
