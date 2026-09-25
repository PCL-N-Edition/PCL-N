"""Derive release identity from Git refs; commit text is always treated as data."""
import argparse
import json
import os
import re
import subprocess
import sys
from pathlib import Path

TAG = re.compile(r"v?((0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*))(?:\.(alpha|beta)\.([1-9]\d*)|\.ci\.([0-9a-f]{6}))?$")

# Asset purposes for the release page; keys mirror eng/release/verify.py FORMATS so the
# guide can never drift from the verified package set.
ASSET_GUIDE = {
    ("win", "setup.exe"): "Windows 安装包（Inno Setup 向导，可选创建桌面快捷方式）",
    ("win", "msi"): "Windows MSI 安装包（适合系统级部署）",
    ("win", "portable.zip"): "Windows 便携版（解压即用，免安装）",
    ("linux", "deb"): "Linux DEB 安装包（Debian / Ubuntu）",
    ("linux", "rpm"): "Linux RPM 安装包（Fedora / openSUSE）",
    ("linux", "AppImage"): "Linux AppImage（免安装单文件）",
    ("linux", "portable.tar.gz"): "Linux 便携版（解压即用）",
    ("osx", "dmg"): "macOS 映像（打开后拖入 Applications）",
    ("osx", "portable.tar.gz"): "macOS 便携版（解压即用）",
}
PLATFORM_NAMES = {"win": "Windows", "linux": "Linux", "osx": "macOS"}
CHANNEL_NAMES = {"stable": "正式版", "alpha": "Alpha 预览版", "beta": "Beta 预览版", "ci": "CI 构建版"}


def identity(ref, sha):
    if not re.fullmatch(r"[0-9a-f]{40,64}", sha):
        raise ValueError("Expected a full Git commit hash")
    if not ref.startswith("refs/tags/"):
        return dict(version=f"2.0.0.ci.{sha[:6]}", prefix="2.0.0", channel="ci", sequence="1", commit=sha[:6], release="false")
    match = TAG.fullmatch(ref.removeprefix("refs/tags/"))
    if not match:
        raise ValueError("Tag must use the canonical dotted XSR version")
    prefix, _, _, _, channel, sequence, commit = match.groups()
    if commit and commit != sha[:6]:
        raise ValueError("CI tag suffix must match the tagged commit")
    return dict(version=ref.removeprefix("refs/tags/").removeprefix("v"), prefix=prefix,
                channel=channel or ("ci" if commit else "stable"), sequence=sequence or "1",
                commit=sha[:6], release="true")


def git(*args):
    return subprocess.check_output(["git", *args], encoding="utf-8").strip()


def changelog(version, previous, sha, base=""):
    # With a previous tag the range is previous..sha. For the FIRST version of this product
    # line the release scope is this branch's own work: the commits since the merge-base with
    # the base branch (e.g. origin/dev). Without a base ref the scope falls back to the tagged
    # commit alone — a bare `git log <sha>` would sweep in the entire repository history,
    # including other product lines, which is not this release's content.
    if previous:
        revision = [f"{previous}..{sha}"]
    elif base:
        try:
            revision = [f"{git('merge-base', base, sha)}..{sha}"]
        except subprocess.CalledProcessError:
            print(f"warning: merge-base with {base} failed; describing only the tagged commit", file=sys.stderr)
            revision = ["-1", sha]
    else:
        revision = ["-1", sha]
    messages = git("log", "--format=%h%x09%B%x00", *revision).split("\0")
    entries = []
    for message in messages:
        if not message.strip():
            continue
        commit, body = message.strip().split("\t", 1)
        lines = body.strip().splitlines()
        entries.append((commit, lines))
    if not entries:
        return f"# 更新内容\n\n- 首个公开发布版本。\n"
    notes = "# 更新内容\n\n"
    for commit, lines in entries:
        notes += f"- {lines[0]} (`{commit}`)\n"
        if len(lines) > 1:
            notes += "\n".join("  " + line for line in lines[1:]) + "\n"
    return notes


def downloads_section(version):
    """One bullet per released package, grouped by platform, mirroring verify.py's set."""
    lines = ["## 下载", "",
             "每个平台选择**一个**包即可；`portable` 为免安装便携版。",
             "安装包为所有用户安装，需要管理员授权；macOS 请打开 DMG 内的 NexaCL.pkg。",
             "所有文件的 SHA256 校验值见附件 `SHA256SUMS`。", ""]
    for platform in ("win", "linux", "osx"):
        lines += [f"### {PLATFORM_NAMES[platform]}", ""]
        for arch in ("x64", "arm64"):
            for extension in ("setup.exe", "msi", "portable.zip") if platform == "win" else \
                             ("deb", "rpm", "AppImage", "portable.tar.gz") if platform == "linux" else \
                             ("dmg", "portable.tar.gz"):
                name = f"Nexa-{version}-{platform}-{arch}.{extension}"
                purpose = ASSET_GUIDE.get((platform, extension), extension)
                lines.append(f"- `{name}` — {purpose}")
        lines.append("")
    lines.append("`SHA256SUMS` 列出全部文件的哈希。每个包和清单均附有 `.asc` 签名。")
    lines.append("验证发布者时，先核对仓库 `GPG-PUBLIC-KEY.asc` 的指纹为 "
                 "`5701218D69B531E1A7ED35BB6E31F5974A273AEE`，导入公钥后运行 "
                 "`gpg --verify SHA256SUMS.asc SHA256SUMS`，再运行 `sha256sum -c SHA256SUMS`。")
    lines.append("OpenPGP 签名不等同于 Windows 代码签名或 macOS 公证。")
    return lines


def release_body(data, changelog_text, previous):
    version = data["version"]
    channel_label = CHANNEL_NAMES.get(data["channel"], data["channel"])
    # changelog() carries its own "# 更新内容" H1 for standalone use; the release body
    # already supplies the section header, so drop it here to avoid a doubled heading.
    changelog_bullets = re.sub(r"^# 更新内容\s*", "", changelog_text)
    lines = [f"# Nexa {version} {channel_label}", "", *downloads_section(version),
             "## 更新内容", "", changelog_bullets.rstrip(), ""]
    if previous:
        lines += [f"**完整变更**：`{previous}` → `{version}`", ""]
    return "\n".join(lines)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--ref", default=os.environ.get("GITHUB_REF", ""))
    parser.add_argument("--sha", default=os.environ.get("GITHUB_SHA", ""))
    parser.add_argument("--base", default=os.environ.get("RELEASE_BASE", ""),
                        help="branch scoping the first release's changelog, e.g. origin/dev")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    sha = git("rev-parse", f"{args.sha}^{{commit}}")
    data = identity(args.ref, sha)
    # Only tags in this product's own version line (same dotted prefix, e.g. 2.0.0.*) can be
    # a previous release; the repository also carries legacy tags from other product lines
    # (e.g. 2.10.x) that must never scope this product's changelog.
    prefix = data["prefix"]
    previous = next((tag for tag in git("tag", "--merged", sha, "--sort=-creatordate").splitlines()
                     if (bare := tag.removeprefix("v")) != prefix and bare.startswith(prefix + ".")
                     and TAG.fullmatch(tag) and git("rev-parse", f"{tag}^{{commit}}") != sha), None)
    changelog_text = changelog(data["version"], previous, sha, args.base)
    body = release_body(data, changelog_text, previous)
    args.output.mkdir(parents=True, exist_ok=True)
    (args.output / "metadata.json").write_text(json.dumps(data, indent=2), encoding="utf-8")
    (args.output / "CHANGELOG.md").write_text(changelog_text, encoding="utf-8")
    (args.output / "RELEASE.md").write_text(body, encoding="utf-8")
    if output := os.environ.get("GITHUB_OUTPUT"):
        with open(output, "a", encoding="utf-8") as stream:
            for key, value in data.items():
                stream.write(f"{key}={value}\n")


if __name__ == "__main__":
    main()
