#!/usr/bin/env python3
"""Fixed-corpus benchmark gate for Update Protocol v2 (implementation step 18).

Builds two synthetic portable payloads with a controlled byte-diff, generates
v2 blockmaps (N-1 as previous), and asserts:

  * byte effective reuse > 70%
  * average incremental compressed transfer < 20 MiB equivalent ratio
    (for this synthetic corpus: new unique compressed / old total compressed)

Does not require network or historical 1.4.3 artifacts — the corpus is
deterministic and checked into CI as a procedural fixture so the gate is
stable across runners.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import sys
import tempfile
from pathlib import Path

_SCRIPT_DIR = Path(__file__).resolve().parent
if str(_SCRIPT_DIR) not in sys.path:
    sys.path.insert(0, str(_SCRIPT_DIR))

import generate_update_blockmap as blockmap  # noqa: E402

# Gate thresholds from protocol acceptance table.
MIN_BYTE_REUSE = 0.70
MAX_INCREMENTAL_RATIO = 0.45  # new unique / previous total compressed
CORPUS_SEED = b"pcln-update-v2-benchmark-corpus-v1"


def _write_blob(path: Path, size: int, seed: bytes, mutate_from: int | None = None) -> None:
    """Write a deterministic pseudo-random blob; optional suffix mutation."""
    path.parent.mkdir(parents=True, exist_ok=True)
    digest = hashlib.sha256(seed).digest()
    chunks: list[bytes] = []
    remaining = size
    counter = 0
    while remaining > 0:
        block = hashlib.sha256(digest + counter.to_bytes(8, "little")).digest() * 2048
        take = min(remaining, len(block))
        chunks.append(block[:take])
        remaining -= take
        counter += 1
    data = bytearray(b"".join(chunks))
    if mutate_from is not None and 0 <= mutate_from < len(data):
        # Flip a trailing region so CDC boundaries still share the head.
        for i in range(mutate_from, len(data)):
            data[i] = data[i] ^ 0xA5
    path.write_bytes(bytes(data))


def _sum_unique_compressed(manifest: dict) -> tuple[int, int, set[str]]:
    """Return (unique compressed bytes, raw bytes, set of block shas)."""
    seen: set[str] = set()
    compressed = 0
    raw = 0
    for file_entry in manifest.get("targetFiles") or []:
        for chunk in file_entry.get("chunks") or []:
            sha = str(chunk.get("sha256") or "").lower()
            size = int(chunk.get("size") or 0)
            full = chunk.get("full") if isinstance(chunk.get("full"), dict) else None
            csize = int((full or {}).get("compressedSize") or chunk.get("compressedSize") or 0)
            raw += size
            if sha in seen:
                continue
            seen.add(sha)
            compressed += csize
    return compressed, raw, seen


def run_benchmark(workdir: Path) -> dict:
    old_file = workdir / "old" / "PCL_N_Beta_win-x64_SelfContained_Portable.exe"
    new_file = workdir / "new" / "PCL_N_Beta_win-x64_SelfContained_Portable.exe"
    # ~12 MiB base payload keeps the gate fast while still multi-chunk under v2.
    base_size = 12 * 1024 * 1024
    mutate_from = int(base_size * 0.78)  # change last ~22%
    _write_blob(old_file, base_size, CORPUS_SEED)
    _write_blob(new_file, base_size, CORPUS_SEED, mutate_from=mutate_from)

    old_out = workdir / "old-dist"
    new_out = workdir / "new-dist"
    old_maps = blockmap.build_file_blockmap(
        old_file,
        old_out,
        target_asset_name=old_file.name,
        entry_name="PCL-N-Edition.exe",
        target_tag="v1.4.3-beta",
        target_version="1.4.3-beta",
        runtime_id="win-x64",
        runtime_variant="SelfContained",
        configuration="Beta",
        profiles=[blockmap.PROFILES["v2"]],
    )
    previous = [json.loads(path.read_text(encoding="utf-8")) for path in old_maps]
    new_maps = blockmap.build_file_blockmap(
        new_file,
        new_out,
        target_asset_name=new_file.name,
        entry_name="PCL-N-Edition.exe",
        target_tag="v1.4.4-beta",
        target_version="1.4.4-beta",
        runtime_id="win-x64",
        runtime_variant="SelfContained",
        configuration="Beta",
        profiles=[blockmap.PROFILES["v2"]],
        previous_maps=previous,
    )

    old_manifest = json.loads(old_maps[0].read_text(encoding="utf-8"))
    new_manifest = json.loads(new_maps[0].read_text(encoding="utf-8"))
    old_comp, old_raw, old_shas = _sum_unique_compressed(old_manifest)
    new_comp, new_raw, new_shas = _sum_unique_compressed(new_manifest)
    shared = old_shas & new_shas
    shared_raw = 0
    for file_entry in new_manifest.get("targetFiles") or []:
        for chunk in file_entry.get("chunks") or []:
            if str(chunk.get("sha256") or "").lower() in shared:
                shared_raw += int(chunk.get("size") or 0)

    reuse = (shared_raw / new_raw) if new_raw else 0.0
    new_only = new_shas - old_shas
    new_unique_compressed = 0
    for file_entry in new_manifest.get("targetFiles") or []:
        for chunk in file_entry.get("chunks") or []:
            sha = str(chunk.get("sha256") or "").lower()
            if sha not in new_only:
                continue
            full = chunk.get("full") if isinstance(chunk.get("full"), dict) else None
            new_unique_compressed += int(
                (full or {}).get("compressedSize") or chunk.get("compressedSize") or 0
            )
            new_only.discard(sha)

    ratio = (new_unique_compressed / old_comp) if old_comp else 1.0
    deltas = 0
    for file_entry in new_manifest.get("targetFiles") or []:
        for chunk in file_entry.get("chunks") or []:
            deltas += len(chunk.get("deltas") or [])

    return {
        "oldCompressed": old_comp,
        "newCompressed": new_comp,
        "newUniqueCompressed": new_unique_compressed,
        "byteReuse": reuse,
        "incrementalRatio": ratio,
        "deltasAccepted": deltas,
        "oldBlocks": len(old_shas),
        "newBlocks": len(new_shas),
        "sharedBlocks": len(shared),
        "compression": new_manifest.get("compression"),
        "zstdAvailable": blockmap._ZSTD_AVAILABLE,
    }


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--workdir", type=Path)
    parser.add_argument("--min-reuse", type=float, default=MIN_BYTE_REUSE)
    parser.add_argument("--max-incremental-ratio", type=float, default=MAX_INCREMENTAL_RATIO)
    args = parser.parse_args(argv)

    workdir = args.workdir or Path(tempfile.mkdtemp(prefix="pcln-bench-v2-"))
    workdir.mkdir(parents=True, exist_ok=True)
    report = run_benchmark(workdir)
    print(json.dumps(report, indent=2, ensure_ascii=False))

    ok = True
    if report["byteReuse"] < args.min_reuse:
        print(
            f"FAIL: byteReuse {report['byteReuse']:.4f} < {args.min_reuse}",
            file=sys.stderr,
        )
        ok = False
    if report["incrementalRatio"] > args.max_incremental_ratio:
        print(
            f"FAIL: incrementalRatio {report['incrementalRatio']:.4f} > {args.max_incremental_ratio}",
            file=sys.stderr,
        )
        ok = False
    if ok:
        print(
            f"PASS: reuse={report['byteReuse']:.2%} "
            f"incremental={report['incrementalRatio']:.2%} "
            f"deltas={report['deltasAccepted']}"
        )
        return 0
    return 1


if __name__ == "__main__":
    # Ensure compress workers stay deterministic in CI.
    os.environ.setdefault("PYTHONHASHSEED", "0")
    raise SystemExit(main())
