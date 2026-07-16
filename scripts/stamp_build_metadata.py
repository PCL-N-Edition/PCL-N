#!/usr/bin/env python3
"""
Stamp PCL.Desktop/metadata.json for CI builds (channel / version / commit).

Does not commit; mutates the working copy so the embedded resource reflects
Release / Beta / CI without hand-editing before every publish.

Examples:
  python scripts/stamp_build_metadata.py --channel release --tag v1.1.6-release
  python scripts/stamp_build_metadata.py --channel beta --tag v1.1.7-beta
  python scripts/stamp_build_metadata.py --channel ci --sha abcdef1 --branch dev
"""

from __future__ import annotations

import argparse
import json
import os
import re
import sys
from pathlib import Path

CHANNEL_SUFFIX = {
    "release": "release",
    "stable": "release",
    "beta": "beta",
    "ci": "ci",
    "dev": "ci",
}

# v1.1.6-release | 1.1.6-beta | v1.1.6 | 1.1.6+build
TAG_RE = re.compile(
    r"^v?(?P<base>\d+(?:\.\d+){0,3})(?:-(?P<suffix>[A-Za-z0-9._-]+))?(?:\+.*)?$",
    re.IGNORECASE,
)


def log(msg: str) -> None:
    print(msg, flush=True)


def normalize_channel(raw: str) -> str:
    key = (raw or "").strip().lower()
    if key not in CHANNEL_SUFFIX:
        raise SystemExit(
            f"Unknown channel '{raw}'. Expected one of: {', '.join(sorted(CHANNEL_SUFFIX))}"
        )
    return CHANNEL_SUFFIX[key]


def parse_tag(tag: str | None) -> tuple[str | None, str | None]:
    if not tag:
        return None, None
    t = tag.strip()
    if t.lower().startswith("refs/tags/"):
        t = t[len("refs/tags/") :]
    m = TAG_RE.match(t)
    if not m:
        log(f"Warning: could not parse version from tag '{tag}'")
        return None, None
    return m.group("base"), (m.group("suffix") or "").lower() or None


def main() -> int:
    parser = argparse.ArgumentParser(description="Stamp PCL.Desktop metadata for a channel build")
    parser.add_argument(
        "--metadata",
        type=Path,
        default=Path("PCL.Desktop/metadata.json"),
        help="Path to metadata.json",
    )
    parser.add_argument(
        "--channel",
        required=True,
        help="release | beta | ci (also accepts stable/dev aliases)",
    )
    parser.add_argument("--tag", default=os.environ.get("PCL_VERSION_TAG") or os.environ.get("GITHUB_REF_NAME"))
    parser.add_argument("--sha", default=os.environ.get("PCL_GITHUB_SHA") or os.environ.get("GITHUB_SHA") or "")
    parser.add_argument(
        "--branch",
        default=os.environ.get("PCL_GIT_BRANCH")
        or os.environ.get("GITHUB_REF_NAME")
        or os.environ.get("GITHUB_HEAD_REF")
        or "",
    )
    parser.add_argument(
        "--base-version",
        default=os.environ.get("PCL_VERSION_BASE"),
        help="Override version.base (else derived from --tag or keep file)",
    )
    parser.add_argument(
        "--code",
        type=int,
        default=None,
        help="Override version.code integer (else keep file)",
    )
    parser.add_argument(
        "--github-output",
        action="store_true",
        help="Write version/channel keys to $GITHUB_OUTPUT",
    )
    args = parser.parse_args()

    path: Path = args.metadata
    if not path.is_file():
        raise SystemExit(f"metadata.json not found: {path}")

    channel_suffix = normalize_channel(args.channel)
    tag_base, tag_suffix = parse_tag(args.tag)

    data = json.loads(path.read_text(encoding="utf-8"))
    version = data.setdefault("version", {})
    if not isinstance(version, dict):
        raise SystemExit("metadata.json: version must be an object")

    # Prefer explicit base, then tag, then existing file value.
    if args.base_version:
        version["base"] = args.base_version.strip().lstrip("v")
    elif tag_base:
        version["base"] = tag_base

    # Channel always wins for suffix (tag suffix is only a hint / consistency check).
    version["suffix"] = channel_suffix
    if tag_suffix and tag_suffix not in {channel_suffix, "stable"} and channel_suffix != "ci":
        log(
            f"Warning: tag suffix '{tag_suffix}' differs from channel '{channel_suffix}' "
            f"(using channel)"
        )

    if args.code is not None:
        version["code"] = int(args.code)

    sha = (args.sha or "").strip()
    if sha:
        data["commit"] = sha[:40]

    branch = (args.branch or "").strip()
    gh_ref = os.environ.get("GITHUB_REF") or ""
    known_branches = {"dev", "main", "master", "beta"}
    if branch in known_branches:
        data["branch"] = branch
    elif gh_ref.startswith("refs/heads/"):
        data["branch"] = gh_ref.removeprefix("refs/heads/")
    elif channel_suffix == "ci":
        data["branch"] = branch or "dev"
    elif branch and not TAG_RE.match(branch):
        data["branch"] = branch
    # else keep existing branch field from metadata template

    path.write_text(json.dumps(data, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")

    base = str(version.get("base") or "0.0.0")
    suffix = str(version.get("suffix") or channel_suffix)
    code = version.get("code", 0)
    display = f"{base} {suffix}".strip()
    informational = f"{base}-{suffix}"
    if sha:
        informational = f"{informational}+{sha[:7]}"
    assembly_version = base if re.fullmatch(r"\d+(?:\.\d+){0,3}", base) else "0.0.0"

    log(f"Stamped {path}:")
    log(f"  channel/suffix = {suffix}")
    log(f"  version.base   = {base}")
    log(f"  version.code   = {code}")
    log(f"  commit         = {data.get('commit')}")
    log(f"  branch         = {data.get('branch')}")
    log(f"  display        = {display}")
    log(f"  informational  = {informational}")

    if args.github_output:
        out = os.environ.get("GITHUB_OUTPUT")
        if out:
            with open(out, "a", encoding="utf-8") as fh:
                fh.write(f"channel={suffix}\n")
                fh.write(f"version_base={base}\n")
                fh.write(f"version_suffix={suffix}\n")
                fh.write(f"version_code={code}\n")
                fh.write(f"display_version={display}\n")
                fh.write(f"informational_version={informational}\n")
                fh.write(f"assembly_version={assembly_version}\n")

    # Also export for subsequent steps via env file when present.
    env_file = os.environ.get("GITHUB_ENV")
    if env_file:
        with open(env_file, "a", encoding="utf-8") as fh:
            fh.write(f"PCL_STAMPED_VERSION_BASE={base}\n")
            fh.write(f"PCL_STAMPED_VERSION_SUFFIX={suffix}\n")
            fh.write(f"PCL_STAMPED_INFORMATIONAL_VERSION={informational}\n")
            fh.write(f"PCL_STAMPED_DISPLAY_VERSION={display}\n")
            fh.write(f"PCL_STAMPED_ASSEMBLY_VERSION={assembly_version}\n")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
