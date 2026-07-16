#!/usr/bin/env python3
"""
Generate binary patches from ALL previous PCL-N release versions to a target tag.

For target V_n and history V_0..V_{n-1}, emits V_i → V_n HDiffPatch files per
runtime variant (RID × SelfContained|NoRuntime × WithPlugin|NoPlugin).
"""

from __future__ import annotations

import argparse
import hashlib
import io
import json
import os
import platform
import re
import shutil
import subprocess
import sys
import tarfile
import tempfile
import time
import urllib.error
import urllib.request
import zipfile
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path

BINARY_NAMES = {
    "win-x64": "PCL.Desktop.exe",
    "win-arm64": "PCL.Desktop.exe",
    "linux-x64": "PCL.Desktop",
    "linux-arm64": "PCL.Desktop",
    "osx-x64": "PCL.Desktop",
    "osx-arm64": "PCL.Desktop",
}

RUNTIME_IDS = list(BINARY_NAMES.keys())
RUNTIME_VARIANTS = [
    "SelfContained_WithPlugin",
    "SelfContained_NoPlugin",
    "NoRuntime_WithPlugin",
    "NoRuntime_NoPlugin",
]


@dataclass
class ReleaseInfo:
    tag: str
    version: str
    prerelease: bool
    assets: dict[str, str]  # name -> download_url


def log(msg: str) -> None:
    print(msg, flush=True)


def normalize_version(tag: str) -> str:
    t = tag.strip()
    if t.lower().startswith("v"):
        t = t[1:]
    plus = t.find("+")
    if plus >= 0:
        t = t[:plus]
    return t


def version_key(tag: str) -> tuple:
    """
    Sort key that is always totally ordered (no mixed str/int compares).

    Semver-like: (0, major, minor, patch, is_prerelease, prerelease_text)
    Non-semver (e.g. ci-latest): (1, 0, 0, 0, 1, raw) so they sort after releases
    but never raise TypeError during sort.
    """
    raw = tag.strip()
    v = normalize_version(raw)
    if v.lower() in {"ci-latest", "latest"} or not v:
        return (1, 0, 0, 0, 1, v.lower())

    core, _, pre = v.partition("-")
    nums: list[int] = []
    for p in core.split("."):
        if p.isdigit():
            nums.append(int(p))
        else:
            return (1, 0, 0, 0, 1, v.lower())
    while len(nums) < 3:
        nums.append(0)
    # is_prerelease: 0 = stable suffix empty or "release", 1 = beta/rc/other
    pre_l = pre.lower()
    is_pre = 0 if pre_l in ("", "release") else 1
    return (0, nums[0], nums[1], nums[2], is_pre, pre_l)


def sha256_file(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


def github_request(url: str, token: str | None) -> object:
    req = urllib.request.Request(url, headers={"User-Agent": "PCL-N-Patches/1.0", "Accept": "application/vnd.github+json"})
    if token:
        req.add_header("Authorization", f"Bearer {token}")
    with urllib.request.urlopen(req, timeout=120) as resp:
        return json.load(resp)


def download_file(url: str, dest: Path, token: str | None) -> None:
    dest.parent.mkdir(parents=True, exist_ok=True)
    req = urllib.request.Request(url, headers={"User-Agent": "PCL-N-Patches/1.0"})
    if token:
        req.add_header("Authorization", f"Bearer {token}")
    with urllib.request.urlopen(req, timeout=600) as resp, dest.open("wb") as out:
        shutil.copyfileobj(resp, out)


def list_releases(repo: str, token: str | None) -> list[ReleaseInfo]:
    releases: list[ReleaseInfo] = []
    page = 1
    while True:
        url = f"https://api.github.com/repos/{repo}/releases?per_page=100&page={page}"
        batch = github_request(url, token)
        if not isinstance(batch, list) or not batch:
            break
        for item in batch:
            if item.get("draft"):
                continue
            tag = item.get("tag_name") or ""
            assets = {
                a["name"]: a["browser_download_url"]
                for a in (item.get("assets") or [])
                if a.get("name") and a.get("browser_download_url")
            }
            releases.append(
                ReleaseInfo(
                    tag=tag,
                    version=normalize_version(tag),
                    prerelease=bool(item.get("prerelease")),
                    assets=assets,
                )
            )
        if len(batch) < 100:
            break
        page += 1
    releases.sort(key=lambda r: version_key(r.tag))
    return releases


def asset_name(configuration: str, rid: str, variant: str, ext: str) -> str:
    # Matches PCL-N publish naming:
    # PCL_N_Release_win-x64_SelfContained_WithPlugin.zip
    return f"PCL_N_{configuration}_{rid}_{variant}.{ext}"


def pick_asset(release: ReleaseInfo, configuration: str, rid: str, variant: str) -> tuple[str, str] | None:
    """Return (asset_name, url) or None."""
    preferred_ext = "zip" if rid.startswith("win-") else "tar.gz"
    names = [
        asset_name(configuration, rid, variant, preferred_ext),
        asset_name(configuration, rid, variant, "zip"),
        asset_name(configuration, rid, variant, "tar.gz"),
        # Legacy names without plugin suffix
        f"PCL_N_{configuration}_{rid}_{variant.split('_')[0]}.{preferred_ext}",
        f"PCL_N_{configuration}_{rid}_SelfContained.{preferred_ext}",
        f"PCL_N_{configuration}_{rid}_NoRuntime.{preferred_ext}",
    ]
    for name in names:
        if name in release.assets:
            return name, release.assets[name]
    # Fuzzy: contains rid + key parts of variant
    keys = [rid] + variant.lower().split("_")
    for name, url in release.assets.items():
        lower = name.lower()
        if all(k.lower() in lower for k in keys if k) and (
            lower.endswith(".zip") or lower.endswith(".tar.gz")
        ):
            return name, url
    return None


def extract_binary(archive: Path, binary_name: str, dest: Path) -> None:
    dest.parent.mkdir(parents=True, exist_ok=True)
    if dest.exists():
        dest.unlink()

    if archive.suffix == ".zip" or archive.name.endswith(".zip"):
        with zipfile.ZipFile(archive) as zf:
            # Prefer exact name; else first matching basename
            candidates = [n for n in zf.namelist() if Path(n).name == binary_name]
            if not candidates:
                candidates = [n for n in zf.namelist() if Path(n).name.lower() == binary_name.lower()]
            if not candidates:
                raise FileNotFoundError(f"{binary_name} not in {archive.name}: {zf.namelist()[:20]}")
            with zf.open(candidates[0]) as src, dest.open("wb") as out:
                shutil.copyfileobj(src, out)
        return

    # tar.gz
    with tarfile.open(archive, mode="r:*") as tf:
        members = [m for m in tf.getmembers() if Path(m.name).name == binary_name]
        if not members:
            members = [m for m in tf.getmembers() if Path(m.name).name.lower() == binary_name.lower()]
        if not members:
            raise FileNotFoundError(f"{binary_name} not in {archive.name}")
        f = tf.extractfile(members[0])
        if f is None:
            raise FileNotFoundError(f"cannot extract {members[0].name}")
        with dest.open("wb") as out:
            shutil.copyfileobj(f, out)


def find_hdiffz(tools_dir: Path) -> Path:
    names = ["hdiffz.exe", "hdiffz"]
    for n in names:
        p = tools_dir / n
        if p.is_file():
            return p
    from shutil import which

    for n in names:
        w = which(n)
        if w:
            return Path(w)
    raise FileNotFoundError("hdiffz not found. Run scripts/bootstrap_hdiffpatch.py")


def run_hdiff(hdiffz: Path, old: Path, new: Path, patch: Path) -> None:
    patch.parent.mkdir(parents=True, exist_ok=True)
    if patch.exists():
        patch.unlink()
    # hdiffz [-s|-c-zstd-…] old new out_diff
    # Default compressed diff is fine for size.
    cmd = [str(hdiffz), "-s-64", str(old), str(new), str(patch)]
    log("  " + " ".join(cmd))
    proc = subprocess.run(cmd, check=False, capture_output=True, text=True)
    if proc.returncode != 0:
        raise RuntimeError(
            f"hdiffz failed ({proc.returncode}): {proc.stderr or proc.stdout}"
        )


def configuration_for_tag(tag: str, prerelease: bool) -> str:
    # PCL-N uses Release for stable, Beta for prerelease assets.
    return "Beta" if prerelease or "beta" in tag.lower() or "rc" in tag.lower() else "Release"


def main() -> int:
    parser = argparse.ArgumentParser(description="Generate all-from-history patches to a target PCL-N version")
    parser.add_argument("--source-repo", default="MuXue1230-owo/PCL-N")
    parser.add_argument("--target-tag", required=True, help="e.g. v1.0.0")
    parser.add_argument("--out-dir", type=Path, required=True)
    parser.add_argument("--cache-dir", type=Path, default=None)
    parser.add_argument("--tools-dir", type=Path, default=None)
    parser.add_argument("--max-from-versions", type=int, default=100)
    parser.add_argument("--rids", nargs="*", default=RUNTIME_IDS)
    parser.add_argument("--variants", nargs="*", default=RUNTIME_VARIANTS)
    parser.add_argument("--include-prerelease-history", action="store_true")
    parser.add_argument("--skip-if-patch-larger-than-full", action="store_true", default=True)
    args = parser.parse_args()

    token = os.environ.get("GH_TOKEN") or os.environ.get("GITHUB_TOKEN")
    root = Path(__file__).resolve().parents[1]
    tools_dir = args.tools_dir or (root / "tools" / "hdiffpatch")
    cache_dir = args.cache_dir or (args.out_dir / ".cache")
    cache_dir.mkdir(parents=True, exist_ok=True)
    args.out_dir.mkdir(parents=True, exist_ok=True)

    try:
        hdiffz = find_hdiffz(tools_dir)
    except FileNotFoundError:
        log("Bootstrapping HDiffPatch…")
        boot = subprocess.run(
            [sys.executable, str(root / "scripts" / "bootstrap_hdiffpatch.py"), "--tools-dir", str(tools_dir)],
            check=False,
        )
        if boot.returncode != 0:
            return boot.returncode
        hdiffz = find_hdiffz(tools_dir)

    log(f"Listing releases from {args.source_repo}…")
    try:
        releases = list_releases(args.source_repo, token)
    except urllib.error.HTTPError as exc:
        log(f"GitHub API error: {exc}")
        return 1

    target = next((r for r in releases if r.tag == args.target_tag or r.tag.lstrip("v") == args.target_tag.lstrip("v")), None)
    if target is None:
        log(f"Target tag not found: {args.target_tag}")
        log("Available: " + ", ".join(r.tag for r in releases[-20:]))
        return 1

    # Skip rolling CI tags and other non-semver markers from patch history.
    def is_patchable(rel: ReleaseInfo) -> bool:
        key = version_key(rel.tag)
        return key[0] == 0  # semver-like only

    history = [
        r for r in releases
        if is_patchable(r) and version_key(r.tag) < version_key(target.tag)
    ]
    if not args.include_prerelease_history and not target.prerelease:
        history = [r for r in history if not r.prerelease and version_key(r.tag)[4] == 0]
    history = history[-args.max_from_versions :]
    log(f"Target: {target.tag}  |  history versions: {len(history)}")

    target_cfg = configuration_for_tag(target.tag, target.prerelease)
    generated_at = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    all_variants_manifest: list[dict] = []
    patch_count = 0
    skip_count = 0

    for rid in args.rids:
        binary_name = BINARY_NAMES.get(rid, "PCL.Desktop.exe" if rid.startswith("win") else "PCL.Desktop")
        for variant in args.variants:
            log(f"=== {rid} / {variant} ===")
            target_asset = pick_asset(target, target_cfg, rid, variant)
            if target_asset is None:
                # try opposite configuration naming
                alt_cfg = "Beta" if target_cfg == "Release" else "Release"
                target_asset = pick_asset(target, alt_cfg, rid, variant)
            if target_asset is None:
                log(f"  skip: no target asset for {rid}/{variant}")
                skip_count += 1
                continue

            t_name, t_url = target_asset
            t_archive = cache_dir / target.tag / t_name
            if not t_archive.is_file():
                log(f"  download target {t_name}")
                download_file(t_url, t_archive, token)
            t_bin = cache_dir / "bin" / target.tag / rid / variant / binary_name
            if not t_bin.is_file():
                extract_binary(t_archive, binary_name, t_bin)
            t_sha = sha256_file(t_bin)
            t_size = t_bin.stat().st_size

            patches_meta: list[dict] = []
            for from_rel in history:
                from_cfg = configuration_for_tag(from_rel.tag, from_rel.prerelease)
                from_asset = pick_asset(from_rel, from_cfg, rid, variant)
                if from_asset is None:
                    alt = "Beta" if from_cfg == "Release" else "Release"
                    from_asset = pick_asset(from_rel, alt, rid, variant)
                if from_asset is None:
                    log(f"  skip {from_rel.tag}: no matching asset")
                    continue

                f_name, f_url = from_asset
                f_archive = cache_dir / from_rel.tag / f_name
                try:
                    if not f_archive.is_file():
                        log(f"  download from {from_rel.tag}: {f_name}")
                        download_file(f_url, f_archive, token)
                    f_bin = cache_dir / "bin" / from_rel.tag / rid / variant / binary_name
                    if not f_bin.is_file():
                        extract_binary(f_archive, binary_name, f_bin)
                except Exception as exc:  # noqa: BLE001
                    log(f"  skip {from_rel.tag}: {exc}")
                    continue

                f_sha = sha256_file(f_bin)
                f_size = f_bin.stat().st_size
                if f_sha == t_sha:
                    log(f"  skip {from_rel.tag}: identical binary")
                    continue

                # Include rid + variant in the basename so softprops/action-gh-release
                # (which flattens paths) does not collide across matrix dimensions.
                patch_name = (
                    f"{rid}__{variant}__"
                    f"{normalize_version(from_rel.tag)}-to-{normalize_version(target.tag)}.hdiff"
                )
                patch_rel = Path("patches") / rid / variant / patch_name
                patch_path = args.out_dir / patch_rel
                try:
                    run_hdiff(hdiffz, f_bin, t_bin, patch_path)
                except Exception as exc:  # noqa: BLE001
                    log(f"  hdiff failed {from_rel.tag}: {exc}")
                    continue

                p_size = patch_path.stat().st_size
                p_sha = sha256_file(patch_path)
                (patch_path.with_suffix(patch_path.suffix + ".sha256")).write_text(
                    f"{p_sha}  {patch_name}\n", encoding="utf-8"
                )

                if args.skip_if_patch_larger_than_full and p_size >= t_size:
                    log(f"  drop {from_rel.tag}: patch {p_size} >= full {t_size}")
                    patch_path.unlink(missing_ok=True)
                    continue

                ratio = round(p_size / t_size, 4) if t_size else 1.0
                log(f"  OK {from_rel.tag} → {target.tag}: {p_size} bytes ({ratio:.1%} of full)")
                patches_meta.append(
                    {
                        "fromVersion": normalize_version(from_rel.tag),
                        "fromTag": from_rel.tag,
                        "algorithm": "hdiffpatch",
                        "fileName": patch_rel.as_posix(),
                        "sha256": p_sha,
                        "size": p_size,
                        "fromSha256": f_sha,
                        "fromSize": f_size,
                        "compressionRatio": ratio,
                    }
                )
                patch_count += 1

            variant_manifest = {
                "runtimeId": rid,
                "runtimeVariant": variant,
                "configuration": target_cfg,
                "targetAssetName": t_name,
                "targetBinaryName": binary_name,
                "targetSha256": t_sha,
                "targetSize": t_size,
                "patches": patches_meta,
            }
            all_variants_manifest.append(variant_manifest)
            man_path = args.out_dir / "manifests" / f"{rid}_{variant}.json"
            man_path.parent.mkdir(parents=True, exist_ok=True)
            man_path.write_text(json.dumps(variant_manifest, indent=2) + "\n", encoding="utf-8")

    index = {
        "formatVersion": 1,
        "targetVersion": normalize_version(target.tag),
        "targetTag": target.tag,
        "generatedAt": generated_at,
        "sourceRepo": args.source_repo,
        "algorithmDefault": "hdiffpatch",
        "variants": all_variants_manifest,
        "stats": {
            "historyVersions": len(history),
            "patchesGenerated": patch_count,
            "variantsSkipped": skip_count,
        },
    }
    (args.out_dir / "index.json").write_text(json.dumps(index, indent=2) + "\n", encoding="utf-8")
    log(f"Done. patches={patch_count} → {args.out_dir / 'index.json'}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
