#!/usr/bin/env python3
"""
Stamp PCL.Desktop/metadata.json for CI builds (channel / version / commit / code).

Does not commit; mutates the working copy so the embedded resource reflects
Release / Beta / CI without hand-editing before every publish.

Version rules (default):
  • version.base  ← from --tag (e.g. v1.1.7-release → 1.1.7)
  • version.suffix ← channel (release | beta | ci)
  • version.code  ← auto:
        max(
          max code found in git tags' metadata.json + current file,
          semver_floor(base)   # major*100000 + minor*1000 + patch
        )
        then +1 if that value was already used for a *different* base
        (same tag rebuild keeps a stable code via semver_floor)

Examples:
  python scripts/stamp_build_metadata.py --channel release --tag v1.1.7-release
  python scripts/stamp_build_metadata.py --channel beta --tag v1.1.7-beta
  python scripts/stamp_build_metadata.py --channel ci --sha abcdef1 --branch dev
"""

from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
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


def parse_semver_parts(base: str) -> tuple[int, int, int]:
    parts: list[int] = []
    for p in (base or "").split("."):
        parts.append(int(p) if p.isdigit() else 0)
    while len(parts) < 3:
        parts.append(0)
    return parts[0], parts[1], parts[2]


def semver_floor_code(base: str) -> int:
    """
    Deterministic monotonic code from semver base.

    1.1.6  → 101006
    1.1.7  → 101007
    1.2.0  → 102000
    2.0.0  → 200000

    Always greater than legacy hand-maintained codes (~200) once major>=1 and minor/patch grow.
    """
    major, minor, patch = parse_semver_parts(base)
    return major * 100_000 + minor * 1_000 + patch


def git_show_metadata(tag: str, rel_path: str) -> dict | None:
    proc = subprocess.run(
        ["git", "show", f"{tag}:{rel_path}"],
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
    )
    if proc.returncode != 0 or not proc.stdout.strip():
        return None
    try:
        data = json.loads(proc.stdout)
    except json.JSONDecodeError:
        return None
    return data if isinstance(data, dict) else None


def list_version_tags() -> list[str]:
    proc = subprocess.run(
        ["git", "tag", "-l", "v*"],
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
    )
    if proc.returncode != 0:
        return []
    tags = [t.strip() for t in proc.stdout.splitlines() if t.strip()]
    # Prefer PCL-N style tags; still accept any v-semver.
    return tags


def collect_historical_codes(metadata_rel: str, current_code: int) -> tuple[int, dict[str, int]]:
    """
    Returns (max_code, map of version.base -> max code seen for that base).
    """
    max_code = max(0, int(current_code or 0))
    by_base: dict[str, int] = {}
    for tag in list_version_tags():
        data = git_show_metadata(tag, metadata_rel)
        if not data:
            continue
        ver = data.get("version") or {}
        if not isinstance(ver, dict):
            continue
        code = ver.get("code")
        base = str(ver.get("base") or "").strip()
        if not isinstance(code, int):
            try:
                code = int(code)  # type: ignore[arg-type]
            except (TypeError, ValueError):
                continue
        max_code = max(max_code, code)
        if base:
            by_base[base] = max(by_base.get(base, 0), code)
    return max_code, by_base


def resolve_auto_code(
    base: str,
    current_file_code: int,
    metadata_rel: str,
    *,
    explicit: int | None,
) -> tuple[int, str]:
    if explicit is not None:
        return int(explicit), "explicit --code"

    hist_max, by_base = collect_historical_codes(metadata_rel, current_file_code)
    floor = semver_floor_code(base)

    # Same base already published: reuse its highest code (stable rebuilds).
    if base in by_base and by_base[base] > 0:
        reused = by_base[base]
        # Still never go below floor so newer patch numbers win over tiny legacy codes.
        code = max(reused, floor)
        return code, f"reuse base {base} (hist={reused}, floor={floor})"

    # New base: at least floor, and strictly greater than any historical code.
    code = max(floor, hist_max + 1, current_file_code + 1 if current_file_code else floor)
    # Prefer clean floor when it already clears history (typical after legacy 2xx).
    if floor > hist_max:
        code = floor
        return code, f"semver floor {floor} (> hist_max {hist_max})"
    return code, f"hist_max+1 ({hist_max}+1), floor={floor}"


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
        default=os.environ.get("PCL_VERSION_CODE"),
        help="Integer override, or 'auto' (default) to derive from tag/history",
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

    previous_base = str(version.get("base") or "").strip()
    try:
        previous_code = int(version.get("code") or 0)
    except (TypeError, ValueError):
        previous_code = 0

    # Prefer explicit base, then tag, then existing file value.
    if args.base_version:
        version["base"] = args.base_version.strip().lstrip("v")
    elif tag_base:
        version["base"] = tag_base
    elif not previous_base:
        version["base"] = "0.0.0"

    # Channel always wins for suffix (tag suffix is only a hint / consistency check).
    version["suffix"] = channel_suffix
    if tag_suffix and tag_suffix not in {channel_suffix, "stable"} and channel_suffix != "ci":
        log(
            f"Warning: tag suffix '{tag_suffix}' differs from channel '{channel_suffix}' "
            f"(using channel)"
        )

    base = str(version.get("base") or "0.0.0")
    metadata_rel = path.as_posix()
    # Normalize to repo-relative if possible
    try:
        metadata_rel = path.resolve().relative_to(Path.cwd().resolve()).as_posix()
    except ValueError:
        pass

    code_arg = args.code
    explicit_code: int | None = None
    if code_arg is not None and str(code_arg).strip() != "" and str(code_arg).strip().lower() != "auto":
        try:
            explicit_code = int(str(code_arg).strip())
        except ValueError as exc:
            raise SystemExit(f"Invalid --code '{code_arg}' (use integer or 'auto')") from exc

    # CI without a version tag: keep template code unless floor is higher.
    if channel_suffix == "ci" and not tag_base and explicit_code is None:
        floor = semver_floor_code(base)
        code = max(previous_code, floor)
        code_reason = f"ci keep/floor (file={previous_code}, floor={floor})"
    else:
        code, code_reason = resolve_auto_code(
            base,
            previous_code,
            metadata_rel,
            explicit=explicit_code,
        )
    version["code"] = int(code)

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

    suffix = str(version.get("suffix") or channel_suffix)
    display = f"{base} {suffix}".strip()
    informational = f"{base}-{suffix}"
    if sha:
        informational = f"{informational}+{sha[:7]}"
    assembly_version = base if re.fullmatch(r"\d+(?:\.\d+){0,3}", base) else "0.0.0"

    log(f"Stamped {path}:")
    log(f"  channel/suffix = {suffix}")
    log(f"  version.base   = {base}" + (f"  (from tag {args.tag})" if tag_base else ""))
    log(f"  version.code   = {code}  [{code_reason}]")
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

    env_file = os.environ.get("GITHUB_ENV")
    if env_file:
        with open(env_file, "a", encoding="utf-8") as fh:
            fh.write(f"PCL_STAMPED_VERSION_BASE={base}\n")
            fh.write(f"PCL_STAMPED_VERSION_SUFFIX={suffix}\n")
            fh.write(f"PCL_STAMPED_VERSION_CODE={code}\n")
            fh.write(f"PCL_STAMPED_INFORMATIONAL_VERSION={informational}\n")
            fh.write(f"PCL_STAMPED_DISPLAY_VERSION={display}\n")
            fh.write(f"PCL_STAMPED_ASSEMBLY_VERSION={assembly_version}\n")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
