#!/usr/bin/env python3
"""Update the static website download catalog without using the GitHub API."""

from __future__ import annotations

import argparse
import datetime as dt
import json
import re
from pathlib import Path
from typing import Any

VERSION_RE = re.compile(r"^v?(\d+)\.(\d+)\.(\d+)(?:[-+].*)?$")
RETENTION_DAYS = 14


def parse_version(tag: str) -> tuple[int, int, int]:
    match = VERSION_RE.match(tag.strip())
    if not match:
        raise ValueError(f"Unsupported release tag: {tag}")
    return tuple(int(match.group(index)) for index in range(1, 4))


def parse_timestamp(value: str) -> dt.datetime:
    normalized = value.strip().replace("Z", "+00:00")
    parsed = dt.datetime.fromisoformat(normalized)
    if parsed.tzinfo is None:
        parsed = parsed.replace(tzinfo=dt.timezone.utc)
    return parsed.astimezone(dt.timezone.utc)


def display_label(tag: str, channel: str) -> str:
    major, minor, patch = parse_version(tag)
    suffix = " Beta" if channel == "beta" else ""
    return f"{major}.{minor}.{patch}{suffix}"


def load_catalog(path: Path) -> dict[str, Any]:
    if not path.exists():
        return {"schemaVersion": 1, "versions": []}
    data = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(data, dict) or not isinstance(data.get("versions", []), list):
        raise ValueError("Existing download catalog has an invalid shape.")
    return data


def update_catalog(
    data: dict[str, Any],
    *,
    tag: str,
    channel: str,
    published_at: dt.datetime,
) -> dict[str, Any]:
    incoming_version = parse_version(tag)
    versions = [item for item in data.get("versions", []) if isinstance(item, dict)]

    same_channel = [
        item
        for item in versions
        if item.get("channel") == channel and item.get("tag") != tag and isinstance(item.get("tag"), str)
    ]
    if same_channel:
        newest = max(parse_version(str(item["tag"])) for item in same_channel)
        if incoming_version <= newest:
            raise ValueError(
                f"Version numbers must increase within the {channel} channel: "
                f"incoming {incoming_version} is not newer than {newest}."
            )

    cutoff = published_at - dt.timedelta(days=RETENTION_DAYS)
    valid_versions: list[tuple[dict[str, Any], dt.datetime]] = []
    for item in versions:
        if item.get("tag") == tag:
            continue
        timestamp = item.get("publishedAt")
        if not isinstance(timestamp, str):
            continue
        try:
            item_channel = str(item.get("channel") or "")
            if item_channel not in ("release", "beta"):
                continue
            parse_version(str(item["tag"]))
            valid_versions.append((item, parse_timestamp(timestamp)))
        except (KeyError, TypeError, ValueError):
            continue

    # Keep the newest entry of every other channel even after the 14-day
    # rollback window. Otherwise publishing a beta would make an older but
    # still-current stable release disappear from the public download page.
    latest_by_channel: dict[str, dict[str, Any]] = {}
    for item, timestamp in valid_versions:
        item_channel = str(item["channel"])
        current = latest_by_channel.get(item_channel)
        if current is None or (
            parse_version(str(item["tag"])), timestamp
        ) > (
            parse_version(str(current["tag"])),
            parse_timestamp(str(current["publishedAt"])),
        ):
            latest_by_channel[item_channel] = item

    retained = [
        item
        for item, timestamp in valid_versions
        if timestamp >= cutoff or latest_by_channel.get(str(item["channel"])) is item
    ]

    retained.append(
        {
            "id": tag,
            "label": display_label(tag, channel),
            "tag": tag,
            "channel": channel,
            "packaging": "v2",
            "supportsPluginChoice": False,
            "publishedAt": published_at.astimezone(dt.timezone.utc).isoformat().replace("+00:00", "Z"),
        }
    )
    retained.sort(
        key=lambda item: (
            parse_timestamp(str(item["publishedAt"])),
            parse_version(str(item["tag"])),
        ),
        reverse=True,
    )
    return {
        "schemaVersion": 1,
        "retentionDays": RETENTION_DAYS,
        "generatedAt": published_at.astimezone(dt.timezone.utc).isoformat().replace("+00:00", "Z"),
        "versions": retained,
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--file", type=Path, required=True)
    parser.add_argument("--tag", required=True)
    parser.add_argument("--channel", choices=("release", "beta"), required=True)
    parser.add_argument("--published-at", required=True)
    args = parser.parse_args()

    published_at = parse_timestamp(args.published_at)
    updated = update_catalog(
        load_catalog(args.file),
        tag=args.tag,
        channel=args.channel,
        published_at=published_at,
    )
    args.file.parent.mkdir(parents=True, exist_ok=True)
    args.file.write_text(json.dumps(updated, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
