#!/usr/bin/env python3
"""Minimal VCDIFF (RFC 3284) encoder for PCL N update protocol v2.

Produces non-compressed, default-code-table deltas that match
``LauncherUpdateVcdiff`` on the client (size-0 integers live in the
instruction stream; addresses use SELF mode only for robustness).
"""

from __future__ import annotations

import hashlib
from pathlib import Path
from typing import Iterable

VCDIFF_MAGIC = bytes((0xD6, 0xC3, 0xC4))
VCD_SOURCE = 0x01
ALGORITHM = "vcdiff-rfc3284"

# Admission (protocol §6)
DELTA_RATIO = 0.70
DELTA_MIN_SAVINGS = 16 * 1024
MAX_DELTAS_PER_TARGET = 2
MAX_VCDIFF_CANDIDATES = 3
MAX_SOURCE_WINDOW = 4 * 1024 * 1024
SOURCE_RADIUS = 1  # ±1 chunk; expand to 2 if needed later


def write_int(value: int) -> bytes:
    if value < 0:
        raise ValueError("VCDIFF integer must be non-negative")
    if value == 0:
        return b"\x00"
    parts: list[int] = []
    while True:
        parts.append(value & 0x7F)
        value >>= 7
        if value == 0:
            break
    parts.reverse()
    out = bytearray()
    for index, part in enumerate(parts):
        if index + 1 < len(parts):
            out.append(0x80 | part)
        else:
            out.append(part)
    return bytes(out)


def sha256_hex(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def encode(source: bytes, target: bytes) -> bytes:
    """Encode target relative to source as a single-window VCDIFF delta."""
    data = bytearray()
    inst = bytearray()
    addr = bytearray()

    position = 0
    while position < len(target):
        best_offset = -1
        best_length = 0
        # Greedy longest match of at least 4 bytes in source.
        if source:
            max_len = len(target) - position

            def match_at(src_index: int) -> int:
                length = 0
                while (
                    length < max_len
                    and src_index + length < len(source)
                    and source[src_index + length] == target[position + length]
                ):
                    length += 1
                return length

            # Prefer same absolute offset first (CDC windows often still align).
            if position < len(source):
                best_length = match_at(position)
                best_offset = position if best_length >= 4 else -1

            # Also try a modest neighborhood search around the target position.
            if best_length < 64:
                center = min(position, len(source) - 1) if source else 0
                radius = min(len(source), 64 * 1024)
                start = max(0, center - radius)
                end = min(len(source), center + radius)
                step = 1 if (end - start) <= 128 * 1024 else 8
                for src_index in range(start, end, step):
                    length = match_at(src_index)
                    if length > best_length:
                        best_length = length
                        best_offset = src_index
                        if best_length >= 256:
                            break

        if best_length >= 4 and best_offset >= 0:
            # Opcode 19 = COPY size 0, mode 0 (SELF) in default table.
            inst.append(19)
            inst.extend(write_int(best_length))
            addr.extend(write_int(best_offset))
            position += best_length
            continue

        # ADD one or more unmatched bytes (opcode 1 = ADD size 0).
        run_start = position
        position += 1
        while position < len(target):
            # Stop ADD when a profitable COPY starts.
            if source and _quick_match_len(source, target, position) >= 4:
                break
            if position - run_start >= 64:
                break
            position += 1
        add_len = position - run_start
        inst.append(1)
        inst.extend(write_int(add_len))
        data.extend(target[run_start:position])

    # Window with entire source as source window at pos 0.
    body = bytearray()
    body.append(VCD_SOURCE)
    body.extend(write_int(len(source)))
    body.extend(write_int(0))  # source position

    delta_encoding = bytearray()
    delta_encoding.extend(write_int(len(target)))  # target window length
    delta_encoding.append(0)  # delta indicator
    delta_encoding.extend(write_int(len(data)))
    delta_encoding.extend(write_int(len(inst)))
    delta_encoding.extend(write_int(len(addr)))
    delta_encoding.extend(data)
    delta_encoding.extend(inst)
    delta_encoding.extend(addr)

    body.extend(write_int(len(delta_encoding)))
    body.extend(delta_encoding)

    return VCDIFF_MAGIC + bytes((0,)) + bytes(body)


def _quick_match_len(source: bytes, target: bytes, position: int) -> int:
    best = 0
    limit = min(len(source), 4096)
    for src_index in range(0, limit, 32):
        length = 0
        while (
            position + length < len(target)
            and src_index + length < len(source)
            and source[src_index + length] == target[position + length]
        ):
            length += 1
            if length >= 4:
                return length
        best = max(best, length)
    return best


def admit_delta(*, full_compressed_size: int, delta_size: int) -> bool:
    if full_compressed_size <= 0 or delta_size <= 0:
        return False
    if delta_size > int(full_compressed_size * DELTA_RATIO):
        return False
    if full_compressed_size - delta_size < DELTA_MIN_SAVINGS:
        return False
    return True


def source_window_chunks(
    old_chunks: list[dict],
    center_index: int,
    *,
    radius: int = SOURCE_RADIUS,
    max_bytes: int = MAX_SOURCE_WINDOW,
) -> tuple[list[str], int]:
    """Return (sha256 list, total raw size) for ±radius around center_index."""
    if not old_chunks or center_index < 0 or center_index >= len(old_chunks):
        return [], 0
    start = max(0, center_index - radius)
    end = min(len(old_chunks), center_index + radius + 1)
    selected: list[dict] = []
    total = 0
    for chunk in old_chunks[start:end]:
        size = int(chunk["size"])
        if total + size > max_bytes and selected:
            break
        selected.append(chunk)
        total += size
    return [str(chunk["sha256"]) for chunk in selected], total


def pick_center_indices(
    new_offset: int,
    new_size: int,
    old_chunks: list[dict],
    *,
    limit: int = MAX_VCDIFF_CANDIDATES,
) -> list[int]:
    """Score old chunks by offset distance then size similarity."""
    if not old_chunks:
        return []
    scored: list[tuple[int, int, int]] = []
    old_offset = 0
    for index, chunk in enumerate(old_chunks):
        size = int(chunk["size"])
        distance = abs(old_offset - new_offset)
        size_delta = abs(size - new_size)
        scored.append((distance, size_delta, index))
        old_offset += size
    scored.sort()
    return [index for _, _, index in scored[:limit]]


def load_raw_block(output_root: Path, sha256: str) -> bytes | None:
    path = output_root / "block" / sha256[:2] / sha256
    if not path.is_file():
        return None
    try:
        payload = path.read_bytes()
        if payload.startswith(b"\x1f\x8b"):
            import gzip

            return gzip.decompress(payload)
        if payload.startswith(b"\x28\xb5\x2f\xfd"):
            try:
                import zstandard as zstd
            except ImportError:
                return None
            return zstd.ZstdDecompressor().decompress(payload)
        # Legacy/unknown: try gzip then zstd.
        try:
            import gzip

            return gzip.decompress(payload)
        except OSError:
            try:
                import zstandard as zstd

                return zstd.ZstdDecompressor().decompress(payload)
            except Exception:  # noqa: BLE001
                return None
    except OSError:
        return None


def write_delta_file(output_root: Path, target_sha: str, source_sha: str, delta: bytes) -> str:
    relative = Path("delta") / "v2" / target_sha[:2] / target_sha / f"{source_sha}.vcdiff"
    target = output_root / relative
    target.parent.mkdir(parents=True, exist_ok=True)
    temporary = target.with_suffix(target.suffix + ".tmp")
    temporary.write_bytes(delta)
    temporary.replace(target)
    return relative.as_posix()


def build_source_bytes(output_root: Path, chunk_shas: Iterable[str]) -> bytes | None:
    parts: list[bytes] = []
    for sha in chunk_shas:
        raw = load_raw_block(output_root, sha)
        if raw is None:
            return None
        parts.append(raw)
    return b"".join(parts)


def attach_v2_deltas(
    entries: list[dict],
    *,
    output_root: Path,
    previous_maps: list[dict],
) -> dict[str, int]:
    """
    For each new chunk missing from previous CAS, try up to 3 source windows
    from previous maps (same path preferred). Mutates chunk dicts into nested
    full/deltas form. Returns stats.
    """
    stats = {"candidates": 0, "accepted": 0, "skipped_no_source": 0}

    # previous path -> ordered chunks with offsets
    previous_by_path: list[dict[str, list[dict]]] = []
    for previous in previous_maps:
        by_path: dict[str, list[dict]] = {}
        for file_entry in previous.get("targetFiles") or []:
            path = str(file_entry.get("path") or "")
            chunks = list(file_entry.get("chunks") or [])
            if path and chunks:
                by_path[path] = chunks
        previous_by_path.append(by_path)

    for file_entry in entries:
        path = str(file_entry.get("path") or "")
        new_chunks: list[dict] = file_entry.get("chunks") or []
        new_offset = 0
        enriched: list[dict] = []
        for chunk in new_chunks:
            target_sha = str(chunk["sha256"])
            target_size = int(chunk["size"])
            full_compressed = int(chunk["compressedSize"])
            full_path = str(chunk["path"])

            full_codec = str(chunk.get("compression") or "gzip")
            nested = {
                "sha256": target_sha,
                "size": target_size,
                # Flat fields retained for older readers / client normalize.
                "compressedSize": full_compressed,
                "path": full_path,
                "compression": full_codec,
                "full": {
                    "path": full_path,
                    "compressedSize": full_compressed,
                    "compression": full_codec,
                },
                "deltas": [],
            }

            # Exact CAS hit in a previous map → no delta needed (client reuses).
            already_known = any(
                any(str(old.get("sha256")) == target_sha for old in (by_path.get(path) or []))
                for by_path in previous_by_path
            )
            if already_known or not previous_by_path:
                # Still emit nested full so v2 maps share one schema.
                enriched.append(nested)
                new_offset += target_size
                continue

            target_raw = load_raw_block(output_root, target_sha)
            if target_raw is None:
                enriched.append(nested)
                new_offset += target_size
                continue

            accepted: list[dict] = []
            tried_windows: set[str] = set()
            for by_path in previous_by_path:
                old_chunks = by_path.get(path) or []
                for center in pick_center_indices(new_offset, target_size, old_chunks):
                    if len(accepted) >= MAX_DELTAS_PER_TARGET:
                        break
                    window_shas, window_size = source_window_chunks(old_chunks, center)
                    if not window_shas:
                        stats["skipped_no_source"] += 1
                        continue
                    source_raw = build_source_bytes(output_root, window_shas)
                    if source_raw is None:
                        stats["skipped_no_source"] += 1
                        continue
                    window_sha = sha256_hex(source_raw)
                    if window_sha in tried_windows:
                        continue
                    tried_windows.add(window_sha)
                    stats["candidates"] += 1
                    try:
                        delta = encode(source_raw, target_raw)
                    except (ValueError, OSError):
                        continue
                    if not admit_delta(full_compressed_size=full_compressed, delta_size=len(delta)):
                        continue
                    delta_path = write_delta_file(output_root, target_sha, window_sha, delta)
                    accepted.append(
                        {
                            "algorithm": ALGORITHM,
                            "sourceChunks": window_shas,
                            "sourceSha256": window_sha,
                            "sourceSize": window_size,
                            "path": delta_path,
                            "size": len(delta),
                        }
                    )
                    stats["accepted"] += 1
                    if len(accepted) >= MAX_DELTAS_PER_TARGET:
                        break
                if len(accepted) >= MAX_DELTAS_PER_TARGET:
                    break

            nested["deltas"] = accepted
            enriched.append(nested)
            new_offset += target_size

        file_entry["chunks"] = enriched

    return stats
