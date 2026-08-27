#!/usr/bin/env python3
"""Reconcile signed block-map metadata with immutable CAS objects already in R2.

CAS identity is the SHA-256 of the uncompressed bytes. Older releases stored a
gzip representation at the same key where newer producers may prefer zstd. An
immutable upload correctly keeps the first representation, so manifests must
describe that canonical object rather than the newly generated local encoding.
"""

from __future__ import annotations

import argparse
import json
import sys
import time
from concurrent.futures import ThreadPoolExecutor, as_completed
from pathlib import Path
from typing import Any


GZIP_MAGIC = b"\x1f\x8b"
ZSTD_MAGIC = b"\x28\xb5\x2f\xfd"


def detect_codec(prefix: bytes) -> str | None:
    if prefix.startswith(GZIP_MAGIC):
        return "gzip"
    if prefix.startswith(ZSTD_MAGIC):
        return "zstd"
    return None


def _iter_full_blocks(document: dict[str, Any]):
    for file_entry in document.get("targetFiles") or []:
        if not isinstance(file_entry, dict):
            continue
        for chunk in file_entry.get("chunks") or []:
            if not isinstance(chunk, dict):
                continue
            full = chunk.get("full")
            yield full if isinstance(full, dict) else chunk


def _probe_with_retry(client, key: str, attempts: int = 8) -> tuple[bytes, int] | None:
    last_error: Exception | None = None
    for attempt in range(attempts):
        try:
            return client.inspect_object(key, 4)
        except Exception as exc:  # noqa: BLE001
            last_error = exc
            if attempt + 1 >= attempts:
                break
            time.sleep(min(12.0, 0.35 * (2**attempt)))
    assert last_error is not None
    raise RuntimeError(f"cannot inspect remote CAS object {key}: {last_error}") from last_error


def reconcile(
    manifest_dir: Path,
    *,
    client,
    local_root: Path | None = None,
    apply: bool,
    require_remote: bool,
    concurrency: int = 8,
) -> tuple[int, int, int]:
    paths = sorted({*manifest_dir.glob("*.blockmap.json"), *manifest_dir.glob("*.blockmap.v2.json")})
    if not paths:
        raise ValueError(f"no blockmaps in {manifest_dir}")

    documents: dict[Path, dict[str, Any]] = {}
    references: dict[str, list[dict[str, Any]]] = {}
    for path in paths:
        document = json.loads(path.read_text(encoding="utf-8"))
        documents[path] = document
        for block in _iter_full_blocks(document):
            key = str(block.get("path") or "").replace("\\", "/")
            if not key.startswith("block/"):
                continue
            references.setdefault(key, []).append(block)

    probes: dict[str, tuple[bytes, int] | None] = {}
    with ThreadPoolExecutor(max_workers=max(1, min(24, concurrency))) as pool:
        futures = {pool.submit(_probe_with_retry, client, key): key for key in references}
        for future in as_completed(futures):
            key = futures[future]
            probes[key] = future.result()

    mismatches = 0
    missing = 0
    errors: list[str] = []
    for key, blocks in references.items():
        probe = probes.get(key)
        if probe is None and local_root is not None:
            local_path = local_root / key
            if local_path.is_file():
                with local_path.open("rb") as handle:
                    probe = (handle.read(4), local_path.stat().st_size)
        if probe is None:
            missing += 1
            if require_remote:
                errors.append(f"missing remote CAS object: {key}")
            continue

        prefix, stored_size = probe
        codec = detect_codec(prefix)
        if codec is None:
            errors.append(f"unsupported CAS block header: {key} ({prefix.hex()})")
            continue
        for block in blocks:
            declared_codec = str(block.get("compression") or "gzip").lower()
            declared_size = int(block.get("compressedSize") or 0)
            if declared_codec == codec and declared_size == stored_size:
                continue
            mismatches += 1
            if apply:
                block["compression"] = codec
                block["compressedSize"] = stored_size
            else:
                errors.append(
                    f"CAS metadata mismatch: {key} "
                    f"declared={declared_codec}/{declared_size} actual={codec}/{stored_size}"
                )

    if errors:
        raise ValueError("\n".join(errors[:80]))

    if apply and mismatches:
        for path, document in documents.items():
            stats = document.get("stats")
            if isinstance(stats, dict):
                stats["referencedCompressedBytes"] = sum(
                    int(block.get("compressedSize") or 0)
                    for block in _iter_full_blocks(document)
                )
            temporary = path.with_suffix(path.suffix + ".tmp")
            temporary.write_text(
                json.dumps(document, ensure_ascii=False, indent=2) + "\n",
                encoding="utf-8",
            )
            temporary.replace(path)

    print(
        f"reconcile: maps={len(paths)} unique_blocks={len(references)} "
        f"mismatched_refs={mismatches} missing={missing} apply={apply}"
    )
    return len(references), mismatches, missing


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest-dir", type=Path, required=True)
    parser.add_argument("--local-root", type=Path)
    parser.add_argument("--apply", action="store_true")
    parser.add_argument("--require-remote", action="store_true")
    parser.add_argument("--concurrency", type=int, default=8)
    args = parser.parse_args(argv)

    scripts = Path(__file__).resolve().parent
    if str(scripts) not in sys.path:
        sys.path.insert(0, str(scripts))
    from upload_r2_cas import resolve_client

    try:
        reconcile(
            args.manifest_dir,
            client=resolve_client(),
            local_root=args.local_root,
            apply=args.apply,
            require_remote=args.require_remote,
            concurrency=args.concurrency,
        )
    except (OSError, ValueError, RuntimeError, json.JSONDecodeError) as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
