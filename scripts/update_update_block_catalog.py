#!/usr/bin/env python3
"""Update the R2 block catalog and emit objects made unreachable by the 14-day window."""

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


def _read_current_blocks(manifest_dir: Path) -> set[str]:
    hashes: set[str] = set()
    manifests = sorted(manifest_dir.glob("*.blockmap.json"))
    if not manifests:
        raise ValueError("no block maps found")
    for path in manifests:
        value = json.loads(path.read_text(encoding="utf-8"))
        if value.get("formatVersion") != 1 or value.get("layout") != "pcln-blockmap-v1":
            raise ValueError(f"invalid block map: {path}")
        for file in value.get("targetFiles") or []:
            for chunk in file.get("chunks") or []:
                sha256 = str(chunk.get("sha256") or "").lower()
                expected = f"block/{sha256[:2]}/{sha256}"
                if len(sha256) != 64 or any(ch not in "0123456789abcdef" for ch in sha256):
                    raise ValueError(f"invalid block hash in {path}")
                if chunk.get("path") != expected:
                    raise ValueError(f"invalid block path in {path}")
                hashes.add(sha256)
    return hashes


def update_catalog(
    catalog: dict,
    *,
    tag: str,
    channel: str,
    published_at: str,
    blocks: set[str],
    objects: list[str],
    now: datetime,
) -> tuple[dict, list[str]]:
    current = {
        "tag": tag,
        "channel": channel,
        "publishedAt": _timestamp(published_at).isoformat().replace("+00:00", "Z"),
        "blocks": sorted(blocks),
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
    parser.add_argument("--now")
    args = parser.parse_args()

    objects = [
        f"releases/{args.tag}/{path.name}"
        for path in sorted(args.asset_dir.iterdir())
        if path.is_file()
    ]
    catalog, deletions = update_catalog(
        _read_catalog(args.catalog),
        tag=args.tag,
        channel=args.channel,
        published_at=args.published_at,
        blocks=_read_current_blocks(args.manifest_dir),
        objects=objects,
        now=_timestamp(args.now) if args.now else datetime.now(timezone.utc),
    )
    args.output.write_text(json.dumps(catalog, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    args.delete_list.write_text("".join(f"{key}\n" for key in deletions), encoding="utf-8")
    print(f"retained={len(catalog['releases'])} deletions={len(deletions)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
