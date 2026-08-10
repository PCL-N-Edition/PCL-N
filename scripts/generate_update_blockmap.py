#!/usr/bin/env python3
"""Build signed content-addressed block maps for scatter and single-file updates.

Supports dual FastCDC profiles:

  v1  pcln-fastcdc-v1  256 KiB / 1 MiB / 2 MiB  →  *.blockmap.json
  v2  pcln-fastcdc-v2  128 KiB / 512 KiB / 1 MiB →  *.blockmap.v2.json

Both maps share the same CAS block tree (compressed raw chunk, SHA-256 of
decompressed identity). v1 always uses gzip; v2 prefers zstd when smaller.
"""

from __future__ import annotations

import argparse
import gzip
import hashlib
import json
import shutil
import stat
import sys
import tarfile
import tempfile
import zipfile
from concurrent.futures import ThreadPoolExecutor, as_completed
from pathlib import Path
from typing import Any

_SCRIPT_DIR = Path(__file__).resolve().parent
if str(_SCRIPT_DIR) not in sys.path:
    sys.path.insert(0, str(_SCRIPT_DIR))

from pcln_vcdiff import attach_v2_deltas  # noqa: E402

try:
    import zstandard as _zstd

    _ZSTD_AVAILABLE = True
except ImportError:
    _zstd = None  # type: ignore[assignment]
    _ZSTD_AVAILABLE = False


UINT64_MASK = (1 << 64) - 1

# v1 masks: early 21 / late 19 for avg ≈ 2^20
# v2 masks: early 20 / late 18 for avg ≈ 2^19 (same spacing)
PROFILES: dict[str, dict[str, Any]] = {
    "v1": {
        "name": "v1",
        "format_version": 1,
        "layout": "pcln-blockmap-v1",
        "file_layout": "pcln-blockmap-file-v1",
        "algorithm": "pcln-fastcdc-v1",
        "min": 256 * 1024,
        "avg": 1024 * 1024,
        "max": 2 * 1024 * 1024,
        "early_mask": (1 << 21) - 1,
        "late_mask": (1 << 19) - 1,
        "suffix": ".blockmap.json",
        "include_chunking": False,
    },
    "v2": {
        "name": "v2",
        "format_version": 2,
        "layout": "pcln-blockmap-v2",
        "file_layout": "pcln-blockmap-file-v2",
        "algorithm": "pcln-fastcdc-v2",
        "min": 128 * 1024,
        "avg": 512 * 1024,
        "max": 1024 * 1024,
        "early_mask": (1 << 20) - 1,
        "late_mask": (1 << 18) - 1,
        "suffix": ".blockmap.v2.json",
        "include_chunking": True,
    },
}

COMPRESSION_GZIP = "gzip"
COMPRESSION_ZSTD = "zstd"

# Last release line that still dual-publishes pcln-fastcdc-v1 maps.
# From 1.4.8 onward only v2 is generated; older clients keep using the v1 maps
# already published through 1.4.7 (shared CAS blocks remain in R2).
LAST_V1_BLOCKMAP_VERSION = (1, 4, 7)

# Compress workers for hash→gzip/zstd→write pipeline (protocol v2 §13).
COMPRESS_WORKERS = 4


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


def _detect_codec(payload: bytes) -> str:
    if payload.startswith(b"\x1f\x8b"):
        return COMPRESSION_GZIP
    if payload.startswith(b"\x28\xb5\x2f\xfd"):
        return COMPRESSION_ZSTD
    return COMPRESSION_GZIP


def _compress_raw(raw: bytes, preferred: str) -> tuple[bytes, str]:
    """Return (compressed_bytes, codec). v2 prefers zstd when smaller than gzip."""
    gzip_bytes = gzip.compress(raw, compresslevel=9, mtime=0)
    if preferred != COMPRESSION_ZSTD or not _ZSTD_AVAILABLE:
        return gzip_bytes, COMPRESSION_GZIP
    assert _zstd is not None
    zstd_bytes = _zstd.ZstdCompressor(level=10).compress(raw)
    # Prefer zstd whenever it is not worse than gzip (protocol §20).
    if len(zstd_bytes) <= len(gzip_bytes):
        return zstd_bytes, COMPRESSION_ZSTD
    return gzip_bytes, COMPRESSION_GZIP


def _flush_chunk(
    data: bytes | bytearray,
    output_root: Path,
    preferred_codec: str,
) -> tuple[dict, bool]:
    raw = bytes(data)
    sha256 = hashlib.sha256(raw).hexdigest()
    relative = Path("block") / sha256[:2] / sha256
    target = output_root / relative
    created = False
    if target.exists():
        payload = target.read_bytes()
        codec = _detect_codec(payload)
        compressed_size = len(payload)
    else:
        compressed, codec = _compress_raw(raw, preferred_codec)
        compressed_size = len(compressed)
        target.parent.mkdir(parents=True, exist_ok=True)
        temporary = target.with_suffix(".tmp")
        temporary.write_bytes(compressed)
        temporary.replace(target)
        created = True
    return (
        {
            "sha256": sha256,
            "size": len(raw),
            "compressedSize": compressed_size,
            "path": relative.as_posix(),
            "compression": codec,
        },
        created,
    )


def chunk_file(
    path: Path,
    output_root: Path,
    profile: dict[str, Any] | None = None,
) -> tuple[str, int, list[dict], int, int]:
    """
    Single sequential CDC scan; compress/hash/write is pipelined on a worker pool
    (protocol v2 §13–14).
    """
    profile = profile or PROFILES["v1"]
    min_chunk = int(profile["min"])
    avg_chunk = int(profile["avg"])
    max_chunk = int(profile["max"])
    early_mask = int(profile["early_mask"])
    late_mask = int(profile["late_mask"])
    preferred_codec = COMPRESSION_ZSTD if profile.get("name") == "v2" else COMPRESSION_GZIP

    file_hash = hashlib.sha256()
    file_size = 0
    rolling = 0
    buffer = bytearray()
    # Preserve CDC order: list of (order_index, future)
    futures: list[Any] = []
    order = 0

    def submit_chunk(payload: bytes) -> None:
        nonlocal order
        index = order
        order += 1
        futures.append((index, pool.submit(_flush_chunk, payload, output_root, preferred_codec)))

    with ThreadPoolExecutor(max_workers=COMPRESS_WORKERS) as pool:
        with path.open("rb") as source:
            while data := source.read(256 * 1024):
                file_hash.update(data)
                file_size += len(data)
                for value in data:
                    buffer.append(value)
                    rolling = ((rolling << 1) + GEAR_TABLE[value]) & UINT64_MASK
                    length = len(buffer)
                    if length < min_chunk:
                        continue
                    mask = early_mask if length < avg_chunk else late_mask
                    if (rolling & mask) == 0 or length >= max_chunk:
                        submit_chunk(bytes(buffer))
                        buffer.clear()
                        rolling = 0
        if buffer or order == 0:
            submit_chunk(bytes(buffer))

        results: list[tuple[dict, bool] | None] = [None] * len(futures)
        for index, future in futures:
            results[index] = future.result()

    chunks: list[dict] = []
    created_blocks = 0
    created_bytes = 0
    for item in results:
        assert item is not None
        chunk, created = item
        chunks.append(chunk)
        if created:
            created_blocks += 1
            created_bytes += int(chunk["compressedSize"])
    return file_hash.hexdigest(), file_size, chunks, created_blocks, created_bytes


def _manifest_sha256(files: list[dict]) -> str:
    canonical = "".join(
        f"{entry['path']}\t{entry['sha256']}\t{entry['size']}\n"
        for entry in sorted(files, key=lambda item: item["path"])
    )
    return hashlib.sha256(canonical.encode("utf-8")).hexdigest()


def parse_version_tuple(version: str) -> tuple[int, int, int] | None:
    """Parse ``1.4.8-beta`` / ``v1.4.8`` → (1, 4, 8). Returns None if not semver-like."""
    text = (version or "").strip()
    if text.lower().startswith("v"):
        text = text[1:]
    core = text.split("-", 1)[0].split("+", 1)[0]
    parts = core.split(".")
    if len(parts) < 2:
        return None
    try:
        major = int(parts[0])
        minor = int(parts[1])
        patch = int(parts[2]) if len(parts) >= 3 else 0
        return (major, minor, patch)
    except ValueError:
        return None


def should_emit_v1_blockmap(target_version: str, configuration: str = "") -> bool:
    """
    True when this release should still emit ``*.blockmap.json`` (v1).

    Policy:
      * versioned releases ≤ 1.4.7 → yes (dual publish)
      * versioned releases ≥ 1.4.8 → no (v2 only; reuse existing 1.4.7-era v1 CAS)
      * CI / ci-latest → no (rolling hosts already prefer v2)
      * unparseable version → yes (safe dual default for unknown tags)
    """
    config = (configuration or "").strip()
    version = (target_version or "").strip()
    if config.upper() == "CI" or version.lower() in {"ci-latest", "ci"}:
        return False
    parsed = parse_version_tuple(version)
    if parsed is None:
        return True
    return parsed <= LAST_V1_BLOCKMAP_VERSION


def default_profile_arg(target_version: str, configuration: str = "") -> str:
    return "both" if should_emit_v1_blockmap(target_version, configuration) else "v2"


def _resolve_profiles(profile_arg: str) -> list[dict[str, Any]]:
    key = (profile_arg or "both").strip().lower()
    if key == "both":
        return [PROFILES["v1"], PROFILES["v2"]]
    if key == "auto":
        raise ValueError("resolve auto via default_profile_arg before _resolve_profiles")
    if key not in PROFILES:
        raise ValueError(f"unknown profile: {profile_arg}")
    return [PROFILES[key]]


def _load_previous_maps(paths: list[Path] | None) -> list[dict[str, Any]]:
    maps: list[dict[str, Any]] = []
    if not paths:
        return maps
    for path in paths:
        path = path.resolve()
        if not path.is_file():
            raise FileNotFoundError(path)
        value = json.loads(path.read_text(encoding="utf-8"))
        if not isinstance(value, dict) or not isinstance(value.get("targetFiles"), list):
            raise ValueError(f"invalid previous blockmap: {path}")
        maps.append(value)
    return maps


def _write_manifest(
    *,
    profile: dict[str, Any],
    layout: str,
    entries: list[dict],
    output_root: Path,
    stem: str,
    target_asset_name: str,
    target_tag: str,
    target_version: str,
    runtime_id: str,
    runtime_variant: str,
    configuration: str,
    total_blocks: int,
    referenced_compressed_bytes: int,
    created_blocks: int,
    created_bytes: int,
    delta_stats: dict[str, int] | None = None,
) -> Path:
    # Map-level default: v1 always gzip; v2 advertises zstd when available (per-block may still be gzip).
    map_compression = (
        COMPRESSION_ZSTD
        if profile.get("name") == "v2" and _ZSTD_AVAILABLE
        else COMPRESSION_GZIP
    )
    manifest: dict[str, Any] = {
        "formatVersion": profile["format_version"],
        "layout": layout,
        "algorithm": profile["algorithm"],
        "compression": map_compression,
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
            "fileCount": len(entries),
            "blockReferences": total_blocks,
            "referencedCompressedBytes": referenced_compressed_bytes,
            "newUniqueBlocks": created_blocks,
            "newUniqueCompressedBytes": created_bytes,
            "chunkMin": profile["min"],
            "chunkAverage": profile["avg"],
            "chunkMax": profile["max"],
        },
    }
    if profile.get("include_chunking"):
        manifest["chunking"] = {
            "min": profile["min"],
            "avg": profile["avg"],
            "max": profile["max"],
        }
    if delta_stats:
        manifest["stats"]["deltaCandidates"] = delta_stats.get("candidates", 0)
        manifest["stats"]["deltasAccepted"] = delta_stats.get("accepted", 0)
    manifest_path = output_root / "manifests" / f"{stem}{profile['suffix']}"
    manifest_path.parent.mkdir(parents=True, exist_ok=True)
    manifest_path.write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    return manifest_path


def build_blockmap(
    archive: Path,
    output_root: Path,
    *,
    target_tag: str,
    target_version: str,
    runtime_id: str,
    runtime_variant: str,
    configuration: str,
    profiles: list[dict[str, Any]] | None = None,
    previous_maps: list[dict[str, Any]] | None = None,
) -> list[Path]:
    archive = archive.resolve()
    output_root = output_root.resolve()
    if not archive.is_file():
        raise FileNotFoundError(archive)
    output_root.mkdir(parents=True, exist_ok=True)
    selected = profiles or [PROFILES["v1"], PROFILES["v2"]]

    with tempfile.TemporaryDirectory(prefix="pcln-blockmap-") as temporary:
        tree = Path(temporary) / "tree"
        _safe_extract(archive, tree)
        _flatten_package_root(tree)
        file_paths = [
            path
            for path in sorted(tree.rglob("*"))
            if path.is_file() and not _should_ignore(path)
        ]
        if not file_paths:
            raise ValueError(f"update archive has no files: {archive}")

        stem = archive.name[:-7] if archive.name.endswith(".tar.gz") else archive.stem
        results: list[Path] = []
        for profile in selected:
            entries: list[dict] = []
            total_blocks = 0
            referenced_compressed_bytes = 0
            created_blocks = 0
            created_bytes = 0
            for path in file_paths:
                relative = path.relative_to(tree).as_posix()
                sha256, size, chunks, new_count, new_bytes = chunk_file(path, output_root, profile)
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
            delta_stats = None
            if profile.get("name") == "v2":
                # Always normalize v2 chunks to nested full (+ optional deltas).
                # Only same-algorithm previous maps may supply source windows —
                # falling back to v1 maps makes pure-Python VCDIFF thrash for hours.
                compatible = [
                    previous
                    for previous in (previous_maps or [])
                    if previous.get("algorithm") in {None, profile["algorithm"], "pcln-fastcdc-v2"}
                ]
                delta_stats = attach_v2_deltas(
                    entries,
                    output_root=output_root,
                    previous_maps=compatible,
                )
                print(
                    f"VCDIFF stats: candidates={delta_stats.get('candidates', 0)} "
                    f"accepted={delta_stats.get('accepted', 0)} "
                    f"previous_maps={len(compatible)}"
                )
            results.append(
                _write_manifest(
                    profile=profile,
                    layout=profile["layout"],
                    entries=entries,
                    output_root=output_root,
                    stem=stem,
                    target_asset_name=archive.name,
                    target_tag=target_tag,
                    target_version=target_version,
                    runtime_id=runtime_id,
                    runtime_variant=runtime_variant,
                    configuration=configuration,
                    total_blocks=total_blocks,
                    referenced_compressed_bytes=referenced_compressed_bytes,
                    created_blocks=created_blocks,
                    created_bytes=created_bytes,
                    delta_stats=delta_stats,
                )
            )
    return results


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
    profiles: list[dict[str, Any]] | None = None,
    previous_maps: list[dict[str, Any]] | None = None,
) -> list[Path]:
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
    selected = profiles or [PROFILES["v1"], PROFILES["v2"]]
    stem = target_asset_name[:-4] if target_asset_name.lower().endswith(".exe") else Path(target_asset_name).stem
    results: list[Path] = []
    for profile in selected:
        sha256, size, chunks, created_blocks, created_bytes = chunk_file(source, output_root, profile)
        entries = [
            {
                "path": normalized_entry,
                "sha256": sha256,
                "size": size,
                "unixMode": stat.S_IMODE(source.stat().st_mode),
                "chunks": chunks,
            }
        ]
        delta_stats = None
        if profile.get("name") == "v2":
            compatible = [
                previous
                for previous in (previous_maps or [])
                if previous.get("algorithm") in {None, profile["algorithm"], "pcln-fastcdc-v2"}
            ]
            delta_stats = attach_v2_deltas(
                entries,
                output_root=output_root,
                previous_maps=compatible,
            )
            print(
                f"VCDIFF stats: candidates={delta_stats.get('candidates', 0)} "
                f"accepted={delta_stats.get('accepted', 0)} "
                f"previous_maps={len(compatible)}"
            )
        results.append(
            _write_manifest(
                profile=profile,
                layout=profile["file_layout"],
                entries=entries,
                output_root=output_root,
                stem=stem,
                target_asset_name=target_asset_name,
                target_tag=target_tag,
                target_version=target_version,
                runtime_id=runtime_id,
                runtime_variant=runtime_variant,
                configuration=configuration,
                total_blocks=len(chunks),
                referenced_compressed_bytes=sum(chunk["compressedSize"] for chunk in chunks),
                created_blocks=created_blocks,
                created_bytes=created_bytes,
                delta_stats=delta_stats,
            )
        )
    return results


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
    parser.add_argument(
        "--profile",
        default="auto",
        choices=("v1", "v2", "both", "auto"),
        help=(
            "FastCDC profile(s) to emit. "
            "auto (default): both for target ≤ 1.4.7, v2-only for ≥ 1.4.8 / CI"
        ),
    )
    parser.add_argument(
        "--previous-blockmap",
        action="append",
        default=[],
        type=Path,
        help="Previous version blockmap(s) for VCDIFF source windows (v2 only; repeatable, max useful=2)",
    )
    args = parser.parse_args()
    profile_key = args.profile
    if profile_key == "auto":
        profile_key = default_profile_arg(args.target_version, args.configuration)
        print(
            f"profile auto -> {profile_key} "
            f"(target={args.target_version}; last v1 dual-publish={'.'.join(map(str, LAST_V1_BLOCKMAP_VERSION))})"
        )
    profiles = _resolve_profiles(profile_key)
    previous_maps = _load_previous_maps(args.previous_blockmap[:2] if args.previous_blockmap else None)
    if args.file is not None:
        if not args.target_asset_name or not args.entry_name:
            parser.error("--file requires --target-asset-name and --entry-name")
        manifests = build_file_blockmap(
            args.file,
            args.output,
            target_asset_name=args.target_asset_name,
            entry_name=args.entry_name,
            target_tag=args.target_tag,
            target_version=args.target_version,
            runtime_id=args.runtime_id,
            runtime_variant=args.runtime_variant,
            configuration=args.configuration,
            profiles=profiles,
            previous_maps=previous_maps,
        )
    else:
        manifests = build_blockmap(
            args.archive,
            args.output,
            target_tag=args.target_tag,
            target_version=args.target_version,
            runtime_id=args.runtime_id,
            runtime_variant=args.runtime_variant,
            configuration=args.configuration,
            profiles=profiles,
            previous_maps=previous_maps,
        )
    for manifest in manifests:
        print(f"Created block map: {manifest}")
    return 0


if __name__ == "__main__":
    # Windows CI runners default to cp1252; avoid UnicodeEncodeError on logs.
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding="utf-8", errors="replace")  # type: ignore[attr-defined]
        except Exception:  # noqa: BLE001
            pass
    raise SystemExit(main())
