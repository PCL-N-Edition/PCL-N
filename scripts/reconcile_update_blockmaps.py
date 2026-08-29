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
import threading
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


def codec_from_content_type(content_type: str | None) -> str | None:
    normalized = (content_type or "").split(";", 1)[0].strip().lower()
    if normalized in {"application/gzip", "application/x-gzip"}:
        return "gzip"
    if normalized in {"application/zstd", "application/zstandard"}:
        return "zstd"
    return None


def magic_for_codec(codec: str) -> bytes:
    return GZIP_MAGIC if codec == "gzip" else ZSTD_MAGIC


def _iter_full_blocks(document: dict[str, Any]):
    for file_entry in document.get("targetFiles") or []:
        if not isinstance(file_entry, dict):
            continue
        for chunk in file_entry.get("chunks") or []:
            if not isinstance(chunk, dict):
                continue
            full = chunk.get("full")
            yield full if isinstance(full, dict) else chunk


_probe_throttle_until = 0.0
_probe_throttle_lock = threading.Lock()


def _extend_probe_throttle(seconds: float) -> None:
    # Cloudflare throttles the CAS inspection endpoint in penalty windows that
    # outlast per-request backoff. Share one cooldown across all probe workers
    # so the sweep stops hammering entirely until the window likely reopens.
    global _probe_throttle_until
    with _probe_throttle_lock:
        _probe_throttle_until = max(_probe_throttle_until, time.time() + seconds)


def _wait_out_probe_throttle() -> None:
    while True:
        with _probe_throttle_lock:
            remaining = _probe_throttle_until - time.time()
        if remaining <= 0:
            return
        time.sleep(min(remaining, 5.0))


def _probe_with_retry(client, key: str, attempts: int = 30) -> tuple[bytes, int] | None:
    last_error: Exception | None = None
    for attempt in range(attempts):
        _wait_out_probe_throttle()
        try:
            return client.inspect_object(key, 4)
        except Exception as exc:  # noqa: BLE001
            last_error = exc
            if attempt + 1 >= attempts:
                break
            if "throttled" in str(exc).lower():
                _extend_probe_throttle(min(60.0, 8.0 * (2 ** min(attempt, 3))))
                continue
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
    remote_metadata=None,
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

    metadata = remote_metadata if remote_metadata is not None else client.list_object_metadata("block/")
    probes: dict[str, tuple[bytes, int] | None] = {}
    needs_probe: list[str] = []
    for key, blocks in references.items():
        item = metadata.get(key)
        if item is None:
            probes[key] = None
            continue
        declared = {
            (str(block.get("compression") or "gzip").lower(), int(block.get("compressedSize") or 0))
            for block in blocks
        }
        stored_codec = codec_from_content_type(getattr(item, "content_type", None))
        stored_size = getattr(item, "size", None)
        if stored_size is not None and len(declared) == 1:
            declared_codec, declared_size = next(iter(declared))
            if stored_codec is not None:
                probes[key] = (magic_for_codec(stored_codec), int(stored_size))
                continue
            if declared_size == int(stored_size):
                # Legacy objects were uploaded as application/octet-stream. A
                # matching compressed size is sufficient to avoid thousands of
                # per-object GETs; all new objects carry an explicit codec type.
                probes[key] = (magic_for_codec(declared_codec), int(stored_size))
                continue
        needs_probe.append(key)

    print(
        f"reconcile inventory: remote={len(metadata)} references={len(references)} "
        f"prefix_probes={len(needs_probe)}",
        flush=True,
    )
    completed = 0
    with ThreadPoolExecutor(max_workers=max(1, min(24, concurrency))) as pool:
        futures = {pool.submit(_probe_with_retry, client, key): key for key in needs_probe}
        for future in as_completed(futures):
            key = futures[future]
            probes[key] = future.result()
            completed += 1
            if completed == 1 or completed % 100 == 0 or completed == len(needs_probe):
                print(f"reconcile probes: {completed}/{len(needs_probe)}", flush=True)

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
