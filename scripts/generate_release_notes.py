#!/usr/bin/env python3
"""
Build GitHub Release notes: git-cliff changelog + structured asset inventory.

Usage:
  python scripts/generate_release_notes.py --tag v1.1.6-release --repo MuXue1230-owo/PCL-N -o RELEASE_NOTES.md
  python scripts/generate_release_notes.py --tag v1.1.6-release --repo MuXue1230-owo/PCL-N --publish
  python scripts/generate_release_notes.py --tag v1.1.6-release --repo MuXue1230-owo/PCL-N --publish --cleanup-legacy
"""

from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import urllib.error
import urllib.request
from collections import defaultdict
from dataclasses import dataclass
from pathlib import Path

GPG_FOOTER = """\
---

[GPG 签名公钥](https://github.com/MuXue1230-owo/PCL-N/blob/dev/GPG-PUBLIC-KEY.asc)
公钥指纹 `81D9430A309B84272D518584EDF4453F0BBB862E`
"""

BUILD_IDENTITY_START = "<!-- pcln-build-identity:start -->"
BUILD_IDENTITY_END = "<!-- pcln-build-identity:end -->"
COMMIT_RE = re.compile(r"^[0-9a-f]{7,40}$", re.IGNORECASE)

# Current packaging (native installers + portable + updater archives):
#   PCL_N_{Release|Beta}_{rid}_{SelfContained|NoRuntime}[_{WithPlugin|NoPlugin}][_{Installer|Portable}].{ext}
#   ext: zip | tar.gz | msi | exe | dmg | deb | rpm | AppImage
PACKAGE_RE = re.compile(
    r"^PCL_N_(?P<config>Release|Beta|CI)_"
    r"(?P<rid>win-x64|win-arm64|linux-x64|linux-arm64|osx-x64|osx-arm64)_"
    r"(?P<runtime>SelfContained|NoRuntime)"
    r"(?:_(?P<plugin>WithPlugin|NoPlugin))?"
    r"(?:_(?P<kind>Installer|Portable))?"
    r"\.(?P<ext>zip|tar\.gz|msi|exe|dmg|deb|rpm|AppImage)$"
)

# Unique patch names produced by generate_patches.py
PATCH_RE = re.compile(
    r"^(?P<rid>[\w-]+)__(?P<variant>[\w]+)__(?P<from>.+)-to-(?P<to>.+)\.hdiff$"
)

# Ambiguous softprops-era basenames (no rid/variant)
LEGACY_PATCH_RE = re.compile(
    r"^(?P<from>\d[\w.\-]*)-to-(?P<to>\d[\w.\-]*)\.hdiff(?:\.sha256)?$"
)

# Flat manifests from early softprops uploads (exact RID only — not patch-manifest-*).
LEGACY_MANIFEST_RE = re.compile(
    r"^(?P<rid>win-x64|win-arm64|linux-x64|linux-arm64|osx-x64|osx-arm64)_"
    r"(?P<runtime>SelfContained|NoRuntime)_"
    r"(?P<plugin>WithPlugin|NoPlugin)\.json$"
)

RID_ORDER = [
    "win-x64",
    "win-arm64",
    "linux-x64",
    "linux-arm64",
    "osx-x64",
    "osx-arm64",
]
RID_LABEL = {
    "win-x64": "Windows x64",
    "win-arm64": "Windows ARM64",
    "linux-x64": "Linux x64",
    "linux-arm64": "Linux ARM64",
    "osx-x64": "macOS Intel",
    "osx-arm64": "macOS Apple Silicon",
}
RUNTIME_LABEL = {
    "SelfContained": "插件自带运行时",
    "NoRuntime": "插件使用本机 .NET",
}
PLUGIN_LABEL = {
    "WithPlugin": "内嵌插件",
    "NoPlugin": "不含插件",
}
KIND_LABEL = {
    "Installer": "系统安装包",
    "Portable": "便携版",
    None: "更新归档",
    "": "更新归档",
}
EXT_LABEL = {
    "zip": "ZIP",
    "tar.gz": "TAR.GZ",
    "msi": "MSI",
    "exe": "EXE",
    "dmg": "DMG",
    "deb": "DEB",
    "rpm": "RPM",
    "AppImage": "AppImage",
}


def package_kind_rank(kind: str | None) -> int:
    # Prefer installers, then portable, then updater archives in the inventory.
    if kind == "Installer":
        return 0
    if kind == "Portable":
        return 1
    return 2


@dataclass
class Asset:
    name: str
    size: int
    download_url: str
    browser_url: str


def log(msg: str) -> None:
    print(msg, flush=True)


def human_size(n: int) -> str:
    if n < 1024:
        return f"{n} B"
    for unit, div in (("KB", 1024), ("MB", 1024**2), ("GB", 1024**3)):
        v = n / div
        if v < 1024 or unit == "GB":
            return f"{v:.1f} {unit}" if unit != "KB" else f"{v:.0f} {unit}"
    return f"{n} B"


def github_api(url: str, token: str | None, method: str = "GET", data: bytes | None = None) -> object:
    req = urllib.request.Request(
        url,
        data=data,
        method=method,
        headers={
            "User-Agent": "PCL-N-ReleaseNotes/1.0",
            "Accept": "application/vnd.github+json",
            "X-GitHub-Api-Version": "2022-11-28",
        },
    )
    if token:
        req.add_header("Authorization", f"Bearer {token}")
    if data is not None:
        req.add_header("Content-Type", "application/json")
    with urllib.request.urlopen(req, timeout=120) as resp:
        raw = resp.read()
        if not raw:
            return {}
        return json.loads(raw.decode("utf-8"))


def fetch_release(repo: str, tag: str, token: str | None) -> dict:
    return github_api(f"https://api.github.com/repos/{repo}/releases/tags/{tag}", token)  # type: ignore[return-value]


def list_assets(release: dict) -> list[Asset]:
    out: list[Asset] = []
    for a in release.get("assets") or []:
        name = a.get("name") or ""
        if not name:
            continue
        out.append(
            Asset(
                name=name,
                size=int(a.get("size") or 0),
                download_url=a.get("url") or "",
                browser_url=a.get("browser_download_url") or "",
            )
        )
    out.sort(key=lambda x: x.name.lower())
    return out


def run_git_cliff(repo_root: Path, tag: str | None, out_path: Path) -> str:
    cliff = shutil.which("git-cliff")
    if not cliff:
        raise FileNotFoundError("git-cliff not found on PATH")

    cmd = [cliff, "--latest", "-o", str(out_path)]
    # When on a detached tag checkout, --latest still works with annotated/lightweight tags.
    if tag:
        # Prefer range ending at this tag if previous tag exists; --latest uses newest tag.
        # Pass tag context via env only; git-cliff --latest is enough for release tags.
        pass
    log(f"Running: {' '.join(cmd)}")
    proc = subprocess.run(cmd, cwd=repo_root, capture_output=True, text=True)
    if proc.returncode != 0:
        raise RuntimeError(
            f"git-cliff failed ({proc.returncode}): {proc.stderr or proc.stdout}"
        )
    text = out_path.read_text(encoding="utf-8")
    return strip_gpg_footer(text).rstrip() + "\n"


def strip_gpg_footer(text: str) -> str:
    """Remove GPG block if present (moved to a fixed footer after inventory)."""
    markers = (
        "[GPG 签名公钥]",
        "GPG-PUBLIC-KEY.asc",
        "公钥指纹",
    )
    lines = text.splitlines()
    cut = None
    for i, line in enumerate(lines):
        if any(m in line for m in markers):
            # include preceding --- if any
            cut = i
            while cut > 0 and lines[cut - 1].strip() in {"", "---"}:
                cut -= 1
            break
    if cut is None:
        return text
    return "\n".join(lines[:cut]).rstrip() + "\n"


def strip_build_identity(text: str) -> str:
    """Remove a previously generated build-identity block before recomposing notes."""
    pattern = re.compile(
        re.escape(BUILD_IDENTITY_START) + r".*?" + re.escape(BUILD_IDENTITY_END),
        re.DOTALL,
    )
    return pattern.sub("", text).rstrip() + "\n"


def build_identity(source_commit: str | None) -> str:
    if not source_commit:
        return ""
    return "\n".join(
        [
            BUILD_IDENTITY_START,
            "<details>",
            "<summary>构建身份</summary>",
            "",
            "```text",
            f"commit: {source_commit}",
            "```",
            "",
            "</details>",
            BUILD_IDENTITY_END,
        ]
    )


def md_link(label: str, url: str | None) -> str:
    """Markdown link; falls back to code span when URL is missing."""
    if url:
        # Escape pipes so table cells stay intact
        safe_label = label.replace("|", "\\|")
        return f"[{safe_label}]({url})"
    return f"`{label}`"


def build_inventory(assets: list[Asset], tag: str) -> str:
    by_name = {a.name: a for a in assets}

    packages: list[tuple[re.Match[str], Asset]] = []
    unique_patches: list[tuple[re.Match[str], Asset]] = []
    legacy_patches: list[Asset] = []
    # package basename (without .asc) -> signature asset
    sig_by_package: dict[str, Asset] = {}
    indexes: list[Asset] = []
    manifests: list[Asset] = []
    other: list[Asset] = []

    for a in assets:
        if a.name.endswith(".asc"):
            sig_by_package[a.name[: -len(".asc")]] = a
            continue
        if a.name.endswith(".build.json"):
            # Machine-readable source identity is consumed by the launcher and
            # intentionally omitted from the human-facing inventory.
            continue
        m = PACKAGE_RE.match(a.name)
        if m:
            packages.append((m, a))
            continue
        m = PATCH_RE.match(a.name)
        if m:
            unique_patches.append((m, a))
            continue
        if LEGACY_PATCH_RE.match(a.name):
            legacy_patches.append(a)
            continue
        if a.name in {"patch-index.json", "index.json"}:
            indexes.append(a)
            continue
        if a.name.startswith("patch-manifest-") and a.name.endswith(".json"):
            manifests.append(a)
            continue
        if LEGACY_MANIFEST_RE.match(a.name):
            other.append(a)  # legacy flat manifests
            continue
        if a.name.endswith(".sha256"):
            # checksum companions; summarized under patches
            continue
        other.append(a)

    lines: list[str] = []
    lines.append("## 📦 发布清单")
    lines.append("")
    lines.append(f"面向 **`{tag}`** 的下载说明。请优先使用下表中的**完整安装包**；增量补丁仅用于自动/手动从旧版升级。")
    lines.append("")
    lines.append("### 完整安装包")
    lines.append("")
    lines.append(
        "| 平台 | 运行时 | 类型 | 格式 | 大小 | 文件 | 校验 |"
    )
    lines.append("| --- | --- | --- | --- | ---: | --- | :---: |")

    def rid_rank(rid: str) -> int:
        try:
            return RID_ORDER.index(rid)
        except ValueError:
            return 99

    packages.sort(
        key=lambda t: (
            rid_rank(t[0].group("rid")),
            0 if t[0].group("runtime") == "SelfContained" else 1,
            package_kind_rank(t[0].group("kind")),
            t[0].group("ext"),
            t[1].name,
        )
    )

    if not packages:
        lines.append("| — | — | — | — | — | *（尚未上传完整包）* | — |")
    else:
        for m, a in packages:
            rid = m.group("rid")
            runtime = m.group("runtime")
            kind = m.group("kind")
            ext = m.group("ext")
            file_cell = md_link(a.name, a.browser_url or None)
            sig = sig_by_package.get(a.name)
            if sig and sig.browser_url:
                verify_cell = md_link("GPG", sig.browser_url)
            elif sig:
                verify_cell = md_link("GPG", None)
            else:
                verify_cell = "—"
            lines.append(
                f"| {RID_LABEL.get(rid, rid)} "
                f"| {RUNTIME_LABEL.get(runtime, runtime)} "
                f"| {KIND_LABEL.get(kind, kind or '更新归档')} "
                f"| {EXT_LABEL.get(ext, ext)} "
                f"| {human_size(a.size)} "
                f"| {file_cell} "
                f"| {verify_cell} |"
            )

    lines.append("")
    lines.append("<details>")
    lines.append("<summary>安装包命名与选型说明</summary>")
    lines.append("")
    lines.append("- **系统安装包 (`_Installer.*`)**：Windows MSI/EXE、macOS DMG、Linux DEB/RPM/AppImage；写入系统标准目录，受包管理器/签名保护。")
    lines.append("- **AppImage**：浏览器/GitHub 下载不会保留可执行位，首次运行前请 `chmod +x *.AppImage`。")
    lines.append("- **便携版 (`_Portable.*`)**：单文件/可移动目录，可放在任意可写路径。")
    lines.append("- **更新归档 (`.zip` / `.tar.gz`)**：启动器增量/完整更新使用的规范归档；与安装包内容同源。")
    lines.append("- **SelfContained / 插件自带运行时**：NativeAOT 本体附带插件 CoreCLR，插件可离线运行，推荐大多数用户。")
    lines.append("- **NoRuntime / 插件使用本机 .NET**：NativeAOT 本体不携带插件 CoreCLR；启动器本体可直接运行，使用插件时需已安装匹配的 .NET 运行时。")
    lines.append("- 当前发布包均内嵌 PCL.Plugin；历史版本可能带 `_WithPlugin` / `_NoPlugin` 后缀。")
    lines.append("- **文件**列可直接下载；**校验**列为 OpenPGP 分离签名（`.asc`）。")
    lines.append("")
    lines.append("</details>")
    lines.append("")

    # Patches summary
    lines.append("### 增量补丁（HDiff）")
    lines.append("")
    if unique_patches:
        # group by from→to and by rid
        from_to: dict[tuple[str, str], int] = defaultdict(int)
        by_rid: dict[str, int] = defaultdict(int)
        total_size = 0
        for m, a in unique_patches:
            from_to[(m.group("from"), m.group("to"))] += 1
            by_rid[m.group("rid")] += 1
            total_size += a.size

        index_name = "patch-index.json" if "patch-index.json" in by_name else (
            "index.json" if "index.json" in by_name else None
        )
        lines.append(
            f"共 **{len(unique_patches)}** 个补丁文件"
            f"（约 {human_size(total_size)}，另含 `.sha256` 校验文件）。"
        )
        if index_name:
            idx = by_name[index_name]
            lines.append(
                f"- 总索引：{md_link(index_name, idx.browser_url or None)}"
                f"（{human_size(idx.size)}）"
            )
        if manifests:
            lines.append(
                f"- 分平台清单：`patch-manifest-*.json`（{len(manifests)} 个）"
            )

        lines.append(
            "- 命名规则：`{runtimeId}__{variant}__{fromVersion}-to-{toVersion}.hdiff`"
        )
        lines.append("")
        lines.append("| 来源版本 → 目标 | 补丁数 |")
        lines.append("| --- | ---: |")
        for (frm, to), n in sorted(from_to.items(), key=lambda x: (x[0][0], x[0][1])):
            lines.append(f"| `{frm}` → `{to}` | {n} |")
        lines.append("")
        lines.append("<details>")
        lines.append("<summary>按平台补丁数量</summary>")
        lines.append("")
        lines.append("| 平台 | 补丁数 |")
        lines.append("| --- | ---: |")
        for rid in sorted(by_rid.keys(), key=rid_rank):
            lines.append(f"| {RID_LABEL.get(rid, rid)} | {by_rid[rid]} |")
        lines.append("")
        lines.append("</details>")
    else:
        lines.append("*本版本暂无结构化增量补丁（或尚未生成）。*")
    lines.append("")

    if legacy_patches:
        lines.append("### 遗留 / 歧义资源（请勿使用）")
        lines.append("")
        lines.append(
            "以下文件来自早期上传命名冲突，**不区分平台/变体**，客户端与手动升级均不应依赖："
        )
        lines.append("")
        for a in legacy_patches[:20]:
            lines.append(f"- `{a.name}`（{human_size(a.size)}）")
        if len(legacy_patches) > 20:
            lines.append(f"- … 另有 {len(legacy_patches) - 20} 个类似文件")
        lines.append("")

    if other:
        # Only show non-noise others
        notable = [
            a
            for a in other
            if not LEGACY_MANIFEST_RE.match(a.name)
        ]
        legacy_man = [a for a in other if LEGACY_MANIFEST_RE.match(a.name)]
        if notable:
            lines.append("### 其他文件")
            lines.append("")
            for a in notable:
                lines.append(f"- `{a.name}`（{human_size(a.size)}）")
            lines.append("")
        if legacy_man and not unique_patches:
            pass
        elif legacy_man:
            lines.append(
                f"<sub>另有 {len(legacy_man)} 个旧版扁平 manifest（已由 `patch-manifest-*.json` 取代）。</sub>"
            )
            lines.append("")

    # Quick stats
    pkg_count = len(packages)
    sig_count = len(sig_by_package)
    lines.append("### 资源统计")
    lines.append("")
    lines.append(
        f"- 完整安装包：**{pkg_count}** · GPG 签名：**{sig_count}** · "
        f"结构化补丁：**{len(unique_patches)}** · 资源总数：**{len(assets)}**"
    )
    lines.append("")
    return "\n".join(lines)


def compose_notes(changelog: str, inventory: str, source_commit: str | None = None) -> str:
    parts = [changelog.rstrip()]
    identity = build_identity(source_commit)
    if identity:
        parts.extend(["", identity])
    if inventory:
        parts.extend(["", inventory.rstrip()])
    parts.extend(["", GPG_FOOTER.strip(), ""])
    return "\n".join(parts)


def publish_body(repo: str, tag: str, body: str, token: str | None) -> None:
    release = fetch_release(repo, tag, token)
    release_id = release.get("id")
    if not release_id:
        raise RuntimeError(f"Release not found for tag {tag}")
    payload = json.dumps({"body": body}).encode("utf-8")
    github_api(
        f"https://api.github.com/repos/{repo}/releases/{release_id}",
        token,
        method="PATCH",
        data=payload,
    )
    log(f"Updated release body for {tag}")


def cleanup_legacy_assets(repo: str, release: dict, assets: list[Asset], token: str | None) -> int:
    """Delete ambiguous legacy patch/manifest assets when newer names exist."""
    names = {a.name for a in assets}
    has_structured = any(PATCH_RE.match(n) for n in names)
    has_patch_index = "patch-index.json" in names
    has_new_manifests = any(n.startswith("patch-manifest-") for n in names)

    to_delete: list[Asset] = []
    for a in assets:
        if has_structured and LEGACY_PATCH_RE.match(a.name):
            to_delete.append(a)
            continue
        if has_patch_index and a.name == "index.json":
            to_delete.append(a)
            continue
        if has_new_manifests and LEGACY_MANIFEST_RE.match(a.name):
            to_delete.append(a)
            continue

    if not to_delete:
        log("No legacy assets to clean up")
        return 0

    # Map name -> asset id from raw release
    id_by_name = {
        a.get("name"): a.get("id")
        for a in (release.get("assets") or [])
        if a.get("name") and a.get("id")
    }
    deleted = 0
    for a in to_delete:
        aid = id_by_name.get(a.name)
        if not aid:
            continue
        url = f"https://api.github.com/repos/{repo}/releases/assets/{aid}"
        try:
            github_api(url, token, method="DELETE")
            log(f"  deleted legacy asset: {a.name}")
            deleted += 1
        except urllib.error.HTTPError as exc:
            log(f"  failed to delete {a.name}: {exc}")
    log(f"Deleted {deleted} legacy assets")
    return deleted


def main() -> int:
    parser = argparse.ArgumentParser(description="Generate PCL-N release notes with asset inventory")
    parser.add_argument("--repo", default=os.environ.get("GITHUB_REPOSITORY", "MuXue1230-owo/PCL-N"))
    parser.add_argument("--tag", required=True, help="Release tag, e.g. v1.1.6-release")
    parser.add_argument("--repo-root", type=Path, default=Path.cwd())
    parser.add_argument("-o", "--output", type=Path, default=Path("RELEASE_NOTES.md"))
    parser.add_argument(
        "--changelog-file",
        type=Path,
        default=None,
        help="Use existing changelog markdown instead of running git-cliff",
    )
    parser.add_argument("--publish", action="store_true", help="PATCH the GitHub release body")
    parser.add_argument(
        "--source-commit",
        default="",
        help="Exact source commit used to build the release assets",
    )
    parser.add_argument(
        "--cleanup-legacy",
        action="store_true",
        help="Delete ambiguous legacy patch/manifest assets when structured ones exist",
    )
    parser.add_argument(
        "--skip-inventory",
        action="store_true",
        help="Only emit changelog (+ GPG footer)",
    )
    args = parser.parse_args()

    source_commit = args.source_commit.strip().lower()
    if source_commit and not COMMIT_RE.fullmatch(source_commit):
        parser.error("--source-commit must be a 7-40 character hexadecimal Git commit")

    token = os.environ.get("GH_TOKEN") or os.environ.get("GITHUB_TOKEN")
    repo_root = args.repo_root.resolve()

    if args.changelog_file:
        changelog = strip_build_identity(
            strip_gpg_footer(args.changelog_file.read_text(encoding="utf-8"))
        )
    else:
        tmp = args.output.with_suffix(".cliff.md")
        try:
            changelog = run_git_cliff(repo_root, args.tag, tmp)
        except FileNotFoundError:
            log("git-cliff missing; falling back to current release body (changelog section only)")
            try:
                rel = fetch_release(args.repo, args.tag, token)
                body = rel.get("body") or ""
                # Keep everything before inventory heading if re-running
                if "## 📦 发布清单" in body:
                    body = body.split("## 📦 发布清单", 1)[0]
                changelog = strip_build_identity(strip_gpg_footer(body)).rstrip() + "\n"
                if not changelog.strip():
                    changelog = f"## {args.tag.lstrip('v')} 更新一览\n\n*(changelog unavailable — install git-cliff)*\n"
            except Exception as exc:  # noqa: BLE001
                log(f"Could not fetch existing body: {exc}")
                changelog = f"## {args.tag.lstrip('v')} 更新一览\n\n*(changelog unavailable)*\n"

    release: dict = {}
    assets: list[Asset] = []
    try:
        release = fetch_release(args.repo, args.tag, token)
        assets = list_assets(release)
        log(f"Loaded {len(assets)} assets from {args.repo}@{args.tag}")
    except Exception as exc:  # noqa: BLE001
        log(f"Warning: could not list assets: {exc}")

    if args.cleanup_legacy and release:
        cleanup_legacy_assets(args.repo, release, assets, token)
        # refresh after cleanup
        release = fetch_release(args.repo, args.tag, token)
        assets = list_assets(release)
        log(f"Assets after cleanup: {len(assets)}")

    if args.skip_inventory:
        notes = compose_notes(changelog, "", source_commit)
    else:
        inventory = build_inventory(assets, args.tag)
        notes = compose_notes(changelog, inventory, source_commit)

    args.output.write_text(notes, encoding="utf-8")
    log(f"Wrote {args.output} ({len(notes)} chars)")

    if args.publish:
        if not token:
            log("ERROR: GH_TOKEN/GITHUB_TOKEN required for --publish")
            return 2
        publish_body(args.repo, args.tag, notes, token)

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
