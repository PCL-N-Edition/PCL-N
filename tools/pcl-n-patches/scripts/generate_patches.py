#!/usr/bin/env python3
"""
Generate binary patches from selected prior PCL-N release versions to a target tag.

Strategy (default):
  • Direct: only the last ``max_from_versions`` (default **10**) predecessors → target
  • Multi-hop for older builds: clients chain patches across releases, e.g. 1→11→21
    (each hop is a “last-10” edge published when that intermediate release was built)

Per runtime variant (RID × SelfContained|NoRuntime). The plugin sidecar is part
of the scatter tree rather than a package-name variant.
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
import stat
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
    "win-x64": "PCL-N-Edition.exe",
    "win-arm64": "PCL-N-Edition.exe",
    "linux-x64": "PCL-N-Edition",
    "linux-arm64": "PCL-N-Edition",
    "osx-x64": "PCL-N-Edition",
    "osx-arm64": "PCL-N-Edition",
}

RUNTIME_IDS = list(BINARY_NAMES.keys())
# Match publish matrix (plugin is opaque scatter sidecar, not a SKU suffix).
RUNTIME_VARIANTS = [
    "SelfContained",
    "NoRuntime",
]

# Files ignored when inventorying a scatter install tree.
IGNORE_NAME_PREFIXES = (".",)
IGNORE_NAMES = {
    "pcln-install-kind",
    ".pcln-old",
    ".pcln-new",
}
IGNORE_SUFFIXES = (".pcln-old", ".pcln-new", ".update")


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


def select_from_versions(
    history_asc: list[ReleaseInfo],
    *,
    max_direct: int = 10,
    hop_interval: int = 10,
) -> tuple[list[ReleaseInfo], dict]:
    """
    Choose which prior versions get a direct patch *to the target*.

    Only the last ``max_direct`` predecessors (default 10) receive a patch to
    this target. Older clients upgrade by multi-hop across intermediate
    releases, e.g. 1→11→21 (each edge was published when that intermediate
    release was built with its own last-N window).

    ``hop_interval`` is recorded for clients as the recommended planning
    stride (does not add extra from→target edges beyond the window).

    Returns (selected ascending, strategy metadata for index.json).
    """
    if max_direct < 1:
        max_direct = 1
    if hop_interval < 1:
        hop_interval = 1

    n = len(history_asc)
    # Sliding window only — keeps asset count bounded (≤ max_direct per variant).
    selected = history_asc[max(0, n - max_direct) :]
    hop_tags = [history_asc[i].tag for i in range(0, n, hop_interval)]
    strategy = {
        "maxDirectFromVersions": max_direct,
        "hopInterval": hop_interval,
        "upgradeMode": "multi-hop",
        "description": (
            f"Only the last {max_direct} versions get a direct patch to this "
            f"release. Older builds should chain patches (e.g. 1→11→21 with "
            f"hopInterval={hop_interval}) using indexes from intermediate releases, "
            f"or fall back to a full download."
        ),
        "selectedFromTags": [r.tag for r in selected],
        "hopAnchorTags": hop_tags,
    }
    return selected, strategy


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


def _should_ignore_rel(rel: str) -> bool:
    name = Path(rel).name
    if name in IGNORE_NAMES:
        return True
    if name.startswith(IGNORE_NAME_PREFIXES) and name not in {"pcln-layout"}:
        # Keep pcln-layout; ignore other dotfiles / staged update helpers.
        if name == "pcln-layout":
            return False
        return True
    if any(name.endswith(suf) for suf in IGNORE_SUFFIXES):
        return True
    if "/.pcln" in rel.replace("\\", "/") or rel.replace("\\", "/").startswith(".pcln"):
        return True
    return False


def extract_tree(archive: Path, dest: Path) -> None:
    """Extract full release package into dest (scatter layout preferred)."""
    if dest.exists():
        shutil.rmtree(dest)
    dest.mkdir(parents=True, exist_ok=True)

    if archive.suffix == ".zip" or archive.name.endswith(".zip"):
        with zipfile.ZipFile(archive) as zf:
            for entry in zf.infolist():
                candidate = (dest / entry.filename).resolve()
                if not candidate.is_relative_to(dest.resolve()):
                    raise ValueError(f"zip entry escapes package root: {entry.filename}")
            zf.extractall(dest)
    else:
        with tarfile.open(archive, mode="r:*") as tf:
            tf.extractall(dest, filter="data")

    # If archive is a single top-level folder (e.g. "PCL N.app" is multi — leave).
    # Flatten one directory when it clearly is the package root with pcln-layout / host.
    children = [p for p in dest.iterdir() if p.name not in {".", ".."}]
    if len(children) == 1 and children[0].is_dir():
        only = children[0]
        marker = only / "pcln-layout"
        host = only / "host"
        entry = list(only.glob("PCL-N-Edition*"))
        mac_entry = only / "Contents" / "MacOS" / "PCL-N-Edition"
        if marker.is_file() or host.is_dir() or entry or mac_entry.is_file():
            for item in only.iterdir():
                target = dest / item.name
                if target.exists():
                    if target.is_dir():
                        shutil.rmtree(target)
                    else:
                        target.unlink()
                shutil.move(str(item), str(target))
            only.rmdir()


def inventory_tree(root: Path) -> dict[str, tuple[str, int]]:
    """relative posix path -> (sha256 hex, size)."""
    inv: dict[str, tuple[str, int]] = {}
    root = root.resolve()
    for path in root.rglob("*"):
        if not path.is_file():
            continue
        rel = path.relative_to(root).as_posix()
        if _should_ignore_rel(rel):
            continue
        inv[rel] = (sha256_file(path), path.stat().st_size)
    if not inv:
        raise FileNotFoundError(f"empty inventory under {root}")
    return inv


def manifest_sha256(inv: dict[str, tuple[str, int]]) -> str:
    lines = [f"{p}\t{h}\t{s}" for p, (h, s) in sorted(inv.items())]
    blob = ("\n".join(lines) + "\n").encode("utf-8")
    return hashlib.sha256(blob).hexdigest()


def binary_relative_path(rid: str) -> Path:
    if rid.startswith("osx-"):
        return Path("Contents") / "MacOS" / "PCL-N-Edition"
    return Path(BINARY_NAMES.get(rid, "PCL-N-Edition"))


def package_layout(root: Path, rid: str) -> str:
    """Return the on-disk update layout contract for compatibility checks."""
    marker_candidates = [root / "pcln-layout"]
    if rid.startswith("osx-"):
        marker_candidates.insert(0, root / "Contents" / "MacOS" / "pcln-layout")
    for marker in marker_candidates:
        if marker.is_file():
            value = marker.read_text(encoding="utf-8", errors="replace").strip()
            return value or "scatter-unknown"

    files = [path for path in root.rglob("*") if path.is_file()]
    binary = root / binary_relative_path(rid)
    if binary.is_file() and len(files) == 1:
        return "legacy-single-file"
    return "legacy-multifile"


def patch_is_worth_shipping(patch_size: int, full_size: int, max_ratio: float) -> bool:
    """Require a material download saving over the canonical full package."""
    if patch_size < 0 or full_size <= 0:
        return False
    return patch_size < full_size and (patch_size / full_size) < max_ratio


def safe_patch_member(rel: str) -> str:
    # Do not encode the path by replacing separators: a/b and a__b would
    # otherwise collide.  The readable suffix is diagnostic only; the digest
    # is the stable, collision-resistant identity used inside the bundle.
    normalized = rel.replace("\\", "/")
    digest = hashlib.sha256(normalized.encode("utf-8")).hexdigest()
    suffix = Path(normalized).suffix[:16]
    return digest + suffix


def build_scatter_patch_zip(
    hdiffz: Path,
    from_root: Path,
    to_root: Path,
    from_inv: dict[str, tuple[str, int]],
    to_inv: dict[str, tuple[str, int]],
    zip_path: Path,
    from_version: str,
    to_version: str,
) -> tuple[dict, int]:
    """
    Build a patches.zip with files.json + per-file hdiffs/blobs.
    Returns (files_json_dict, zip_size).
    """
    work = zip_path.parent / (zip_path.stem + ".work")
    if work.exists():
        shutil.rmtree(work)
    patches_dir = work / "patches"
    blobs_dir = work / "blobs"
    patches_dir.mkdir(parents=True)
    blobs_dir.mkdir(parents=True)

    ops: list[dict] = []
    all_paths = sorted(set(from_inv) | set(to_inv))
    for rel in all_paths:
        in_from = rel in from_inv
        in_to = rel in to_inv
        if in_from and in_to:
            fh, fs = from_inv[rel]
            th, ts = to_inv[rel]
            if fh == th:
                continue
            patch_member = f"patches/{safe_patch_member(rel)}.hdiff"
            patch_file = work / patch_member
            run_hdiff(hdiffz, from_root / rel, to_root / rel, patch_file)
            patch_size = patch_file.stat().st_size
            # A compressed per-file delta can be larger than the target file
            # (already-compressed archives are the common case). Store the new
            # file instead so a scatter update never pays that penalty.
            if patch_size >= ts:
                patch_file.unlink()
                blob_member = f"blobs/{safe_patch_member(rel)}"
                blob_file = work / blob_member
                shutil.copy2(to_root / rel, blob_file)
                ops.append(
                    {
                        "path": rel,
                        "op": "replace",
                        "blob": blob_member,
                        "blobSha256": th,
                        "blobSize": ts,
                        "fromSha256": fh,
                        "toSha256": th,
                        "fromSize": fs,
                        "toSize": ts,
                    }
                )
            else:
                ops.append(
                    {
                        "path": rel,
                        "op": "hdiff",
                        "patch": patch_member,
                        "patchSha256": sha256_file(patch_file),
                        "patchSize": patch_size,
                        "fromSha256": fh,
                        "toSha256": th,
                        "fromSize": fs,
                        "toSize": ts,
                    }
                )
        elif in_to:
            th, ts = to_inv[rel]
            blob_member = f"blobs/{safe_patch_member(rel)}"
            blob_file = work / blob_member
            blob_file.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(to_root / rel, blob_file)
            ops.append(
                {
                    "path": rel,
                    "op": "add",
                    "blob": blob_member,
                    "blobSha256": th,
                    "blobSize": ts,
                    "toSha256": th,
                    "toSize": ts,
                }
            )
        else:
            fh, fs = from_inv[rel]
            ops.append(
                {
                    "path": rel,
                    "op": "delete",
                    "fromSha256": fh,
                    "fromSize": fs,
                }
            )

    files_json = {
        "formatVersion": 1,
        "layout": "scatter",
        "fromVersion": from_version,
        "toVersion": to_version,
        "fromManifestSha256": manifest_sha256(from_inv),
        "toManifestSha256": manifest_sha256(to_inv),
        "ops": ops,
        "targetFiles": [
            {
                "path": p,
                "sha256": h,
                "size": s,
                "unixMode": stat.S_IMODE((to_root / p).stat().st_mode),
            }
            for p, (h, s) in sorted(to_inv.items())
        ],
    }
    (work / "files.json").write_text(
        json.dumps(files_json, indent=2) + "\n", encoding="utf-8"
    )

    if zip_path.exists():
        zip_path.unlink()
    with zipfile.ZipFile(zip_path, "w", compression=zipfile.ZIP_DEFLATED) as zf:
        for path in work.rglob("*"):
            if path.is_file():
                zf.write(path, path.relative_to(work).as_posix())
    shutil.rmtree(work, ignore_errors=True)
    return files_json, zip_path.stat().st_size


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
    parser = argparse.ArgumentParser(
        description=(
            "Generate HDiff patches to a target PCL-N version "
            "(default: last 10 versions; older clients multi-hop e.g. 1→11→21)"
        )
    )
    parser.add_argument("--source-repo", default="MuXue1230-owo/PCL-N")
    parser.add_argument("--target-tag", required=True, help="e.g. v1.0.0")
    parser.add_argument("--out-dir", type=Path, required=True)
    parser.add_argument("--cache-dir", type=Path, default=None)
    parser.add_argument("--tools-dir", type=Path, default=None)
    parser.add_argument(
        "--max-from-versions",
        type=int,
        default=10,
        help="Max recent versions with a direct patch to target (default: 10)",
    )
    parser.add_argument(
        "--hop-interval",
        type=int,
        default=10,
        help=(
            "Client multi-hop planning stride (e.g. 1→11→21 when N=10). "
            "Recorded in index strategy metadata (default: 10)"
        ),
    )
    parser.add_argument("--rids", nargs="*", default=RUNTIME_IDS)
    parser.add_argument("--variants", nargs="*", default=RUNTIME_VARIANTS)
    parser.add_argument("--include-prerelease-history", action="store_true")
    parser.add_argument(
        "--max-patch-ratio",
        type=float,
        default=0.80,
        help=(
            "Publish a patch only when it is smaller than this fraction of the "
            "full package (default: 0.80)."
        ),
    )
    args = parser.parse_args()
    if not 0 < args.max_patch_ratio <= 1:
        parser.error("--max-patch-ratio must be greater than 0 and at most 1")

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

    history_all = [
        r for r in releases
        if is_patchable(r) and version_key(r.tag) < version_key(target.tag)
    ]
    if not args.include_prerelease_history and not target.prerelease:
        history_all = [r for r in history_all if not r.prerelease and version_key(r.tag)[4] == 0]

    history, strategy = select_from_versions(
        history_all,
        max_direct=args.max_from_versions,
        hop_interval=args.hop_interval,
    )
    log(
        f"Target: {target.tag}  |  patchable history: {len(history_all)}  |  "
        f"selected from: {len(history)}  "
        f"(max_direct={args.max_from_versions}, hop_interval={args.hop_interval})"
    )
    log(f"  from tags: {', '.join(r.tag for r in history) or '(none)'}")

    target_cfg = configuration_for_tag(target.tag, target.prerelease)
    generated_at = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    all_variants_manifest: list[dict] = []
    patch_count = 0
    skip_count = 0
    incompatible_layout_count = 0
    inefficient_patch_count = 0

    for rid in args.rids:
        binary_name = BINARY_NAMES.get(rid, "PCL-N-Edition.exe" if rid.startswith("win") else "PCL-N-Edition")
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
            t_root = cache_dir / "tree" / target.tag / rid / variant
            if not t_root.is_dir() or not any(t_root.iterdir()):
                extract_tree(t_archive, t_root)
            t_bin = t_root / binary_relative_path(rid)
            if not t_bin.is_file():
                raise FileNotFoundError(
                    f"target package {t_name} has no root entry {binary_name}"
                )
            t_inv = inventory_tree(t_root)
            t_manifest_sha = manifest_sha256(t_inv)
            t_sha = sha256_file(t_bin)
            t_size = t_bin.stat().st_size
            t_archive_size = t_archive.stat().st_size
            target_layout = package_layout(t_root, rid)

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
                    f_root = cache_dir / "tree" / from_rel.tag / rid / variant
                    if not f_root.is_dir() or not any(f_root.iterdir()):
                        extract_tree(f_archive, f_root)
                    f_bin = f_root / binary_relative_path(rid)
                    if not f_bin.is_file():
                        raise FileNotFoundError(
                            f"source package {f_name} has no root entry {binary_name}"
                        )
                except Exception as exc:  # noqa: BLE001
                    log(f"  skip {from_rel.tag}: {exc}")
                    continue

                f_inv = inventory_tree(f_root)
                f_manifest_sha = manifest_sha256(f_inv)
                f_sha = sha256_file(f_bin)
                f_size = f_bin.stat().st_size
                source_layout = package_layout(f_root, rid)
                if source_layout != target_layout:
                    log(
                        f"  skip {from_rel.tag}: incompatible package layout "
                        f"{source_layout!r} → {target_layout!r}; client must use full package"
                    )
                    incompatible_layout_count += 1
                    continue
                if f_manifest_sha == t_manifest_sha:
                    log(f"  skip {from_rel.tag}: identical package tree")
                    continue

                # Include rid + variant in the basename so softprops/action-gh-release
                # (which flattens paths) does not collide across matrix dimensions.
                patch_name = (
                    f"{rid}__{variant}__"
                    f"{normalize_version(from_rel.tag)}-to-{normalize_version(target.tag)}.patch.zip"
                )
                patch_rel = Path("patches") / rid / variant / patch_name
                patch_path = args.out_dir / patch_rel
                try:
                    files_manifest, p_size = build_scatter_patch_zip(
                        hdiffz,
                        f_root,
                        t_root,
                        f_inv,
                        t_inv,
                        patch_path,
                        normalize_version(from_rel.tag),
                        normalize_version(target.tag),
                    )
                except Exception as exc:  # noqa: BLE001
                    log(f"  scatter patch failed {from_rel.tag}: {exc}")
                    continue

                ratio = p_size / t_archive_size if t_archive_size else 1.0
                if not patch_is_worth_shipping(
                    p_size, t_archive_size, args.max_patch_ratio
                ):
                    log(
                        f"  drop {from_rel.tag}: patch bundle {p_size} ({ratio:.1%}) "
                        f"does not beat full archive by the required "
                        f"{(1 - args.max_patch_ratio):.0%}"
                    )
                    patch_path.unlink(missing_ok=True)
                    inefficient_patch_count += 1
                    continue

                p_sha = sha256_file(patch_path)
                ratio = round(ratio, 4)
                log(f"  OK {from_rel.tag} → {target.tag}: {p_size} bytes ({ratio:.1%} of full)")
                patches_meta.append(
                    {
                        "fromVersion": normalize_version(from_rel.tag),
                        "fromTag": from_rel.tag,
                        "algorithm": "hdiffpatch-scatter-v1",
                        "layout": "scatter",
                        "fileName": patch_rel.as_posix(),
                        "sha256": p_sha,
                        "size": p_size,
                        "fromSha256": f_sha,
                        "fromSize": f_size,
                        "fromManifestSha256": files_manifest["fromManifestSha256"],
                        "targetManifestSha256": files_manifest["toManifestSha256"],
                        "operationCount": len(files_manifest["ops"]),
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
                "targetArchiveSize": t_archive_size,
                "targetManifestSha256": t_manifest_sha,
                "targetFileCount": len(t_inv),
                "patches": patches_meta,
            }
            all_variants_manifest.append(variant_manifest)
            man_path = args.out_dir / "manifests" / f"{rid}_{variant}.json"
            man_path.parent.mkdir(parents=True, exist_ok=True)
            man_path.write_text(json.dumps(variant_manifest, indent=2) + "\n", encoding="utf-8")

    index = {
        "formatVersion": 3,
        "targetVersion": normalize_version(target.tag),
        "targetTag": target.tag,
        "generatedAt": generated_at,
        "sourceRepo": args.source_repo,
        "algorithmDefault": "hdiffpatch-scatter-v1",
        "strategy": strategy,
        "variants": all_variants_manifest,
        "stats": {
            "historyVersionsAvailable": len(history_all),
            "historyVersionsSelected": len(history),
            "patchesGenerated": patch_count,
            "variantsSkipped": skip_count,
            "incompatibleLayoutsSkipped": incompatible_layout_count,
            "inefficientPatchesSkipped": inefficient_patch_count,
            "maxPatchRatio": args.max_patch_ratio,
        },
    }

    if history and patch_count == 0:
        log(
            "No beneficial compatible patches were generated; publishing an empty "
            "patch index so clients cleanly fall back to the full package."
        )
    (args.out_dir / "index.json").write_text(json.dumps(index, indent=2) + "\n", encoding="utf-8")
    log(f"Done. patches={patch_count} → {args.out_dir / 'index.json'}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
