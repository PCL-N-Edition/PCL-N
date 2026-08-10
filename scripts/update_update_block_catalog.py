#!/usr/bin/env python3
"""Update the R2 block catalog and emit objects made unreachable by the 14-day window.

Tracks both full CAS blocks (v1 + v2) and VCDIFF delta objects so expired
releases can reclaim unreferenced delta/v2/* keys as well.
"""

from __future__ import annotations

import argparse
import json
from datetime import datetime, timedelta, timezone
from pathlib import Path


FORMAT_VERSION = 1
RETENTION_DAYS = 14


def _timestamp(value: str) -> datetime:
    parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
    if parsed.tzinfo is None:
        parsed = parsed.replace(tzinfo=timezone.utc)
    return parsed.astimezone(timezone.utc)


def _read_catalog(path: Path) -> dict:
    if not path.is_file():
        return {"formatVersion": FORMAT_VERSION, "releases": []}
    value = json.loads(path.read_text(encoding="utf-8"))
    if value.get("formatVersion") != FORMAT_VERSION or not isinstance(value.get("releases"), list):
        raise ValueError("unsupported block catalog")
    return value


_VALID_LAYOUTS = {
    "pcln-blockmap-v1",
    "pcln-blockmap-file-v1",
    "pcln-blockmap-v2",
    "pcln-blockmap-file-v2",
}


def _iter_blockmap_paths(manifest_dir: Path) -> list[Path]:
    # Prefer explicit dual-publish names; avoid matching only one suffix.
    found = {
        *manifest_dir.glob("*.blockmap.json"),
        *manifest_dir.glob("*.blockmap.v2.json"),
    }
    return sorted(found)


def _read_current_blocks_and_deltas(manifest_dir: Path) -> tuple[set[str], set[str]]:
    hashes: set[str] = set()
    deltas: set[str] = set()
    manifests = _iter_blockmap_paths(manifest_dir)
    if not manifests:
        raise ValueError("no block maps found")
    for path in manifests:
        value = json.loads(path.read_text(encoding="utf-8"))
        format_version = value.get("formatVersion")
        layout = value.get("layout")
        if format_version not in {1, 2} or layout not in _VALID_LAYOUTS:
            raise ValueError(f"invalid block map: {path}")
        if format_version == 1 and layout not in {"pcln-blockmap-v1", "pcln-blockmap-file-v1"}:
            raise ValueError(f"invalid block map: {path}")
        if format_version == 2 and layout not in {"pcln-blockmap-v2", "pcln-blockmap-file-v2"}:
            raise ValueError(f"invalid block map: {path}")
        for file in value.get("targetFiles") or []:
            for chunk in file.get("chunks") or []:
                full = chunk.get("full") if isinstance(chunk.get("full"), dict) else None
                sha256 = str((full or {}).get("sha256") or chunk.get("sha256") or "").lower()
                block_path = str((full or {}).get("path") or chunk.get("path") or "")
                expected = f"block/{sha256[:2]}/{sha256}"
                if len(sha256) != 64 or any(ch not in "0123456789abcdef" for ch in sha256):
                    raise ValueError(f"invalid block hash in {path}")
                if block_path != expected:
                    raise ValueError(f"invalid block path in {path}")
                hashes.add(sha256)

                for delta in chunk.get("deltas") or []:
                    if not isinstance(delta, dict):
                        continue
                    delta_path = str(delta.get("path") or "").replace("\\", "/")
                    if not delta_path.startswith("delta/v2/") or ".." in delta_path:
                        raise ValueError(f"invalid delta path in {path}: {delta_path}")
                    deltas.add(delta_path)
    return hashes, deltas


def update_catalog(
    catalog: dict,
    *,
    tag: str,
    channel: str,
    published_at: str,
    blocks: set[str],
    objects: list[str],
    now: datetime,
    deltas: set[str] | None = None,
) -> tuple[dict, list[str]]:
    current = {
        "tag": tag,
        "channel": channel,
        "publishedAt": _timestamp(published_at).isoformat().replace("+00:00", "Z"),
        "blocks": sorted(blocks),
        "deltas": sorted(deltas or ()),
        "objects": sorted(objects),
    }
    replaced = [entry for entry in catalog.get("releases", []) if entry.get("tag") == tag]
    previous = [entry for entry in catalog.get("releases", []) if entry.get("tag") != tag]
    candidates = [*previous, current]
    cutoff = now.astimezone(timezone.utc) - timedelta(days=RETENTION_DAYS)
    retained: list[dict] = []
    expired: list[dict] = list(replaced)
    for entry in candidates:
        try:
            published = _timestamp(str(entry.get("publishedAt") or ""))
        except (TypeError, ValueError):
            expired.append(entry)
            continue
        if entry.get("tag") == tag or published >= cutoff:
            retained.append(entry)
        else:
            expired.append(entry)

    retained_hashes = {
        str(sha256)
        for entry in retained
        for sha256 in entry.get("blocks") or []
    }
    expired_hashes = {
        str(sha256)
        for entry in expired
        for sha256 in entry.get("blocks") or []
    }
    deletions = {
        f"block/{sha256[:2]}/{sha256}"
        for sha256 in expired_hashes - retained_hashes
        if len(sha256) == 64 and all(ch in "0123456789abcdef" for ch in sha256)
    }

    retained_deltas = {
        str(path)
        for entry in retained
        for path in entry.get("deltas") or []
    }
    for entry in expired:
        for path in entry.get("deltas") or []:
            if (
                isinstance(path, str)
                and path not in retained_deltas
                and path.startswith("delta/v2/")
                and ".." not in path
            ):
                deletions.add(path)

    retained_objects = {
        str(object_key)
        for entry in retained
        for object_key in entry.get("objects") or []
    }
    for entry in expired:
        for object_key in entry.get("objects") or []:
            if (isinstance(object_key, str) and object_key not in retained_objects and
                    object_key.startswith("releases/") and ".." not in object_key):
                deletions.add(object_key)

    retained.sort(key=lambda entry: (_timestamp(entry["publishedAt"]), entry["tag"]))
    result = {
        "formatVersion": FORMAT_VERSION,
        "retentionDays": RETENTION_DAYS,
        "updatedAt": now.astimezone(timezone.utc).isoformat().replace("+00:00", "Z"),
        "releases": retained,
    }
    return result, sorted(deletions)


def inventory_gc_deletions(catalog: dict, remote_keys: set[str]) -> list[str]:
    """
    Protocol v2 §19: after retention window GC of catalog entries, also sweep
    remote inventory keys that no retained release references (full + delta).
    """
    retained_blocks = {
        str(sha)
        for entry in catalog.get("releases") or []
        for sha in entry.get("blocks") or []
        if isinstance(sha, str) and len(sha) == 64
    }
    retained_deltas = {
        str(path)
        for entry in catalog.get("releases") or []
        for path in entry.get("deltas") or []
        if isinstance(path, str)
    }
    deletions: list[str] = []
    for key in remote_keys:
        key = key.replace("\\", "/")
        if key in {"block/catalog.json"} or key.startswith("block/catalog"):
            continue
        if key.startswith("block/") and key.count("/") == 2:
            sha = key.rsplit("/", 1)[-1].lower()
            if len(sha) == 64 and sha not in retained_blocks:
                deletions.append(key)
            continue
        if key.startswith("delta/v2/") and key not in retained_deltas:
            deletions.append(key)
    return sorted(set(deletions))


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--catalog", required=True, type=Path)
    parser.add_argument("--manifest-dir", required=True, type=Path)
    parser.add_argument("--asset-dir", required=True, type=Path)
    parser.add_argument("--tag", required=True)
    parser.add_argument("--channel", required=True, choices=("release", "beta", "ci"))
    parser.add_argument("--published-at", required=True)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--delete-list", required=True, type=Path)
    parser.add_argument(
        "--remote-keys",
        type=Path,
        help="Optional inventory file (one R2 key per line) for full mark-and-sweep GC (§19)",
    )
    parser.add_argument("--now")
    args = parser.parse_args()

    objects = [
        f"releases/{args.tag}/{path.name}"
        for path in sorted(args.asset_dir.iterdir())
        if path.is_file()
    ]
    blocks, deltas = _read_current_blocks_and_deltas(args.manifest_dir)
    previous_catalog = _read_catalog(args.catalog)
    catalog, deletions = update_catalog(
        previous_catalog,
        tag=args.tag,
        channel=args.channel,
        published_at=args.published_at,
        blocks=blocks,
        deltas=deltas,
        objects=objects,
        now=_timestamp(args.now) if args.now else datetime.now(timezone.utc),
    )

    # Full inventory sweep only when we already have a populated catalog history.
    # An empty previous catalog must not mass-delete pre-existing R2 objects.
    if (
        args.remote_keys
        and args.remote_keys.is_file()
        and (previous_catalog.get("releases") or [])
        and (catalog.get("releases") or [])
    ):
        remote = {
            line.strip().replace("\\", "/")
            for line in args.remote_keys.read_text(encoding="utf-8").splitlines()
            if line.strip() and not line.strip().startswith("#")
        }
        inventory = inventory_gc_deletions(catalog, remote)
        deletions = sorted(set(deletions) | set(inventory))
        print(f"inventory_gc candidates={len(inventory)}")
    elif args.remote_keys:
        print("inventory_gc skipped (need prior catalog history)")

    args.output.write_text(json.dumps(catalog, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    args.delete_list.write_text("".join(f"{key}\n" for key in deletions), encoding="utf-8")
    print(
        f"retained={len(catalog['releases'])} blocks={len(blocks)} "
        f"deltas={len(deltas)} deletions={len(deletions)}"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
