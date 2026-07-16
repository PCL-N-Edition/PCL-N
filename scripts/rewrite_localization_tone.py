#!/usr/bin/env python3
"""
Rewrite PCL.Desktop localization strings to a plain, calm, iOS-like interaction tone.

- Short chrome labels (OK, Cancel, titles under ~12 chars) are mostly kept.
- Tooltips / dialogs / long copy are softened: less marketing, fewer exclamation
  scares, fewer hard “recommended / not recommended” pushes.
"""

from __future__ import annotations

import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

STRING_RE = re.compile(
    r"(<(?:sys:String|x:String)\s+x:Key=\"([^\"]+)\">)(.*?)(</(?:sys:String|x:String)>)",
    re.S,
)


def unescape(s: str) -> str:
    return (
        s.replace("&#x0a;", "\n")
        .replace("&quot;", '"')
        .replace("&amp;", "&")
        .replace("&lt;", "<")
        .replace("&gt;", ">")
        .replace("&#x0A;", "\n")
    )


def escape(s: str) -> str:
    s = (
        s.replace("&", "&amp;")
        .replace("<", "&lt;")
        .replace(">", "&gt;")
        .replace('"', "&quot;")
    )
    # Prefer XML numeric newlines for multi-line resource strings
    s = s.replace("\r\n", "\n").replace("\r", "\n").replace("\n", "&#x0a;")
    return s


# Exact key overrides (highest priority) — bilingual via file-specific maps below.
EN_KEY_OVERRIDES: dict[str, str] = {
    "Setup.Update.Channel.Title": "Update channel",
    "Setup.Update.Channel.Release": "Release",
    "Setup.Update.Channel.Release.ToolTip": "Stable releases with fixes and occasional built-in resource updates.",
    "Setup.Update.Channel.Beta": "Beta",
    "Setup.Update.Channel.Beta.ToolTip": "Preview builds for upcoming changes. They may be less stable.",
    "Setup.Update.Channel.Dev": "CI",
    "Setup.Update.Channel.Dev.ToolTip": "Development builds from recent CI. They can be unstable.\nThese builds are full downloads only.",
    "Setup.Update.Channel.CI": "CI",
    "Setup.Update.Channel.CI.ToolTip": "Development builds from recent CI. They can be unstable.\nThese builds are full downloads only.",
    "Setup.Update.Channel.Beta.Warning.Message": "You’re about to switch to the Beta channel.\n\nBeta builds may include unfinished features and can be less stable. After updating, switching back may require waiting for the next Release or installing Release manually.",
    "Setup.Update.Channel.Common.Warning.Title": "Before you continue",
    "Setup.Update.Channel.Common.Warning.Confirm": "Continue",
    "Setup.Update.Channel.Dev.Warning.Message": "You’re about to switch to the CI channel.\n\nThese builds can be very unstable and may not start. After updating, returning to Release or Beta may require installing that version manually.",
    "Setup.Update.Channel.Dev.FinalConfirm.Title": "Confirm",
    "Setup.Update.Channel.Dev.FinalConfirm.Message": "Switch to the CI channel?\n\nCI builds may have serious issues. After updating, you may need to reinstall the launcher to change channels.\n\nEnter “{0}” to confirm.",
    "Setup.Update.Channel.Dev.FinalConfirm.Submit": "Continue",
    "Setup.Update.Channel.Dev.FinalConfirm.ExpectedInput": "I understand the risks",
    "Setup.Update.Channel.Dev.FinalConfirm.WrongInput": "That doesn’t match. Please try again.",
    "Setup.Update.Auto.Title": "Automatic updates",
    "Setup.Update.Auto.DownloadAndInstall": "Download and install automatically",
    "Setup.Update.Auto.DownloadAndInstall.ToolTip": "Checks for updates, downloads them when available, and installs after you quit.",
    "Setup.Update.Auto.DownloadAndNotify": "Download automatically and notify",
    "Setup.Update.Auto.DownloadAndNotify.ToolTip": "Checks for updates and downloads them when available, then lets you know.",
    "Setup.Update.Auto.NotifyOnly": "Notify only",
    "Setup.Update.Auto.NotifyOnly.ToolTip": "Checks for updates and lets you know when one is available.",
    "Setup.Update.Auto.Disabled": "Off",
    "Setup.Update.Auto.Disabled.ToolTip": "Doesn’t check for updates automatically. You can still check manually.",
    "Setup.Update.Install": "Download and install",
    "Setup.Update.DownloadNow": "Download",
    "Setup.Update.DownloadAndInstall": "Download and install",
    "Setup.Update.Available": "An update is available",
    "Setup.Update.RestartInstall": "Restart to install",
    "Setup.Update.Checking": "Checking for updates…",
    "Setup.Update.Latest": "You’re up to date",
    "Setup.Update.CheckFailed": "Couldn’t check for updates",
    "Setup.Update.CheckAgain": "Check again",
    "Setup.Update.Changelog.Title": "About this update",
    "Setup.Update.Changelog.View": "Learn more",
    "Setup.Update.Changelog.MoreInfo": "Learn more about this update…",
    "Setup.Update.Changelog.Empty": "This update includes bug fixes and improvements.",
    "Setup.Update.Changelog.Unavailable": "This update includes bug fixes and improvements.",
    "Setup.Update.Changelog.Placeholder": "This update includes bug fixes and improvements.\n\nSome changes may vary slightly depending on your device, system version, or how you use the app. For the best experience, download and install when your network connection is stable.\n\nFor full details and a complete list of changes, you can view this update on GitHub.",
    "Setup.Update.DotNetMissing.Title": "Update requires .NET",
    "Setup.Update.DotNetMissing.Message": "An update is available (version {0}), but it needs .NET 8 on this computer.\n\nInstall .NET 8, then try again.\n\nUse the button below to open the download page, then choose {1} under “.NET Desktop Runtime”.",
    "Setup.Update.DotNetMissing.DownloadRuntime": "Get .NET 8",
    "Setup.Update.OtherOptions.Title": "Other options",
    "Setup.Update.OtherOptions.Placeholder": "Additional update options may appear here when available.",
    "Setup.Update.Error.NetworkFailed": "Couldn’t get the latest version information. Check your network connection and try again.",
    "Main.SpecialVersion.CiHint": "This is a CI build of PCL N.\nIt may include unfinished changes.",
    "Launch.Right.CommunityHint.Message": "You’re using PCL N Edition.\n\nIt’s maintained separately from official PCL, and some behavior may differ.",
    "Setup.Ui.Background.Blur.ToolTip": "Strong blur can lower launcher frame rates.",
    "Setup.Ui.Background.OpenFolder.ToolTip": "Place background images or videos in this folder. One will be chosen at random when the launcher opens.\nH.264 video usually plays more smoothly.",
    "Setup.Ui.Basic.BlurMethod.Gaussian.ToolTip": "A higher-quality blur that samples a wider area. It can be slower on some devices.",
}

ZH_KEY_OVERRIDES: dict[str, str] = {
    "Setup.Update.Channel.Title": "更新通道",
    "Setup.Update.Channel.Release": "正式版",
    "Setup.Update.Channel.Release.ToolTip": "较为稳定的版本，包含问题修复，以及部分内置资源更新。",
    "Setup.Update.Channel.Beta": "测试版",
    "Setup.Update.Channel.Beta.ToolTip": "用于提前体验新变化的测试版本，稳定性可能较低。",
    "Setup.Update.Channel.Dev": "CI 通道",
    "Setup.Update.Channel.Dev.ToolTip": "来自近期 CI 的开发构建，可能不稳定。\n此通道仅提供完整下载。",
    "Setup.Update.Channel.CI": "CI 通道",
    "Setup.Update.Channel.CI.ToolTip": "来自近期 CI 的开发构建，可能不稳定。\n此通道仅提供完整下载。",
    "Setup.Update.Auto.Title": "自动更新",
    "Setup.Update.Auto.DownloadAndInstall": "自动下载并安装",
    "Setup.Update.Auto.DownloadAndInstall.ToolTip": "自动检查并下载可用更新，退出启动器后完成安装。",
    "Setup.Update.Auto.DownloadAndNotify": "自动下载并通知",
    "Setup.Update.Auto.DownloadAndNotify.ToolTip": "自动检查并下载可用更新，完成后通知你。",
    "Setup.Update.Auto.NotifyOnly": "仅通知",
    "Setup.Update.Auto.NotifyOnly.ToolTip": "自动检查可用更新，并在有更新时通知你。",
    "Setup.Update.Auto.Disabled": "关闭",
    "Setup.Update.Auto.Disabled.ToolTip": "不自动检查更新。你仍可以手动检查。",
    "Setup.Update.CheckAgain": "再次检查",
    "Setup.Update.DownloadNow": "下载",
    "Setup.Update.DownloadAndInstall": "下载并安装",
    "Setup.Update.Install": "下载并安装",
    "Setup.Update.Available": "有可用更新",
    "Setup.Update.Latest": "已是最新版本",
    "Setup.Update.Changelog.MoreInfo": "进一步了解此更新…",
    "Setup.Update.Changelog.View": "进一步了解",
    "Setup.Update.Changelog.Placeholder": "此更新包含问题修复与改进。\n\n部分内容可能因设备、系统版本或使用方式而略有不同。建议在网络状况良好时完成下载与安装。\n\n有关此更新的完整说明与变更列表，可在 GitHub 上查看。",
    "Setup.Update.OtherOptions.Title": "其他选项",
    "Setup.Update.OtherOptions.Placeholder": "如有其他更新选项，将显示在这里。",
    "Launch.Right.CommunityHint.Message": "你正在使用 PCL N Edition。\n\n它与官方 PCL 分开维护，部分体验可能不同。",
    "Setup.Launch.InstanceIsolation.All.ToolTip": "不同实例之间的存档、Mod、资源包等互不共享。\n原版实例之间的存档也无法共用。",
    "Setup.Launch.Memory.Warning.Java32Bit": "32 位 Java 最多只能分配约 1 GB 内存。可考虑改用 64 位 Java。",
    "Setup.Ui.Background.OpenFolder.ToolTip": "将背景图片或视频放入此文件夹。打开启动器时会随机选用其中之一。\nH.264 编码的视频通常更流畅。",
    "Setup.Ui.Background.Refresh.ToolTip": "随机选用文件夹中的一张图片或视频。\nH.264 编码的视频通常更流畅。",
    "Setup.Ui.Basic.BlurMethod.Gaussian.ToolTip": "在较大范围内进行加权采样，效果更细，速度可能较慢。",
    "Setup.Ui.Homepage.Preset.DailyModpack": "每日整合包（作者：wkea）",
    "Setup.Ui.Homepage.Preset.McSkin": "Minecraft 皮肤（作者：wkea）",
}


def soften_en(text: str) -> str:
    if not text or len(text) < 12:
        return text

    original = text
    t = text

    # Exclamation → period (keep ? )
    if t.count("!") >= 1 and len(t) > 20:
        t = t.replace("!!!", ".")
        t = t.replace("!!", ".")
        t = re.sub(r"!+(?=\s|$)", ".", t)

    replacements = [
        (r"\bstrongly recommended\b", "recommended"),
        (r"\bStrongly recommended\b", "Recommended"),
        (r"\bstrongly discouraged\b", "not suggested"),
        (r"\bStrongly discouraged\b", "Not suggested"),
        (r"\bhighly recommended\b", "recommended"),
        (r"\bHighly recommended\b", "Recommended"),
        (r"\bnot recommended\b", "optional"),
        (r"\bNot recommended\b", "Optional"),
        (r"\b\(not recommended\)", ""),
        (r"\bSuitable for most users\.?", ""),
        (r"\bSuitable for users who[^.]*\.", ""),
        (r"\bSuitable for highly technical users\.?", ""),
        (r"\bonly recommended for[^.]*\.", ""),
        (r"\bOnly recommended for[^.]*\.", ""),
        (r"\bThis option is only recommended for[^.]*\.", ""),
        (r"\bUnless you are creating a (?:server )?modpack[^.]*\.", ""),
        (r"\bIf you are creating a modpack[^.!]*[.!]?", ""),
        (r"\bplease use the Release version!?", "consider using Release."),
        (r"\bPlease use the Release version!?", "Consider using Release."),
        (r"\bboost your productivity\b", "help you work"),
        (r"\bdelightful\b", "simple"),
        (r"\brefreshing new design\b", "updated design"),
        (r"\bcompletely rebuilds the core experience\b", "changes core behavior"),
        (r"\bbrings a refreshing\b", "includes an"),
        (r"\bserious FPS drops\b", "lower frame rates"),
        (r"\bPlease use it cautiously\.?", "Use it carefully."),
        (r"\bmay cause serious\b", "may cause"),
        (r"\bunpredictable consequences\b", "unexpected issues"),
        (r"\bface unpredictable\b", "run into unexpected"),
        (r"\bCRITICAL\b", "Important"),
        (r"\bCRITICAL BUG\b", "important issue"),
        (r"\bYou will not be able to\b", "You may not be able to"),
        (r"\bmust\b", "need to"),
        (r"\bMUST\b", "need to"),
        (r"\bNever\b", "Avoid"),
        (r"\balways consider\b", "consider"),
        (r"\bAlways consider\b", "Consider"),
        (r"\bFor the best experience,?\s*", ""),
        (r"\bIn general,?\s*", ""),
        (r"\s{2,}", " "),
    ]
    for pat, rep in replacements:
        t = re.sub(pat, rep, t, flags=re.IGNORECASE if pat[:2] != r"\b" or True else 0)

    # Clean leftover whitespace / empty lines from removals
    lines = [ln.strip() for ln in t.replace("\r", "").split("\n")]
    cleaned: list[str] = []
    for ln in lines:
        ln = re.sub(r"\s{2,}", " ", ln).strip(" ;")
        if not ln:
            if cleaned and cleaned[-1] != "":
                cleaned.append("")
            continue
        cleaned.append(ln)
    while cleaned and cleaned[-1] == "":
        cleaned.pop()
    t = "\n".join(cleaned).strip()

    # If we over-deleted, keep original
    if len(t) < max(8, len(original) // 5):
        return original
    return t


def soften_zh(text: str) -> str:
    if not text or len(text) < 8:
        return text
    original = text
    t = text

    if t.count("！") >= 1 and len(t) > 12:
        t = t.replace("！！！", "。").replace("！！", "。")
        t = re.sub(r"！+", "。", t)

    replacements = [
        (r"强烈建议", "建议"),
        (r"强烈不推荐", "一般不建议"),
        (r"强烈推荐", "建议"),
        (r"不推荐[！!。]?", "可选。"),
        (r"（不推荐）", ""),
        (r"\(不推荐\)", ""),
        (r"一般不推荐。", "一般不建议。"),
        (r"适合大多数用户使用。", ""),
        (r"适合希望帮助测试且有一定技术能力的用户使用。", ""),
        (r"适合高度技术性用户。", ""),
        (r"如果你正在制作整合包[，,].*", ""),
        (r"除非你在制作服务器整合包.*", ""),
        (r"令人眼前一亮的", ""),
        (r"令人愉快的", ""),
        (r"助你提高工作效率。", ""),
        (r"完全重构了基础体验，", ""),
        (r"使用了最新开发技术，", ""),
        (r"将会进行功能的早期测试，", ""),
        (r"可能极不稳定", "可能不稳定"),
        (r"严重掉帧", "帧率下降"),
        (r"请务必", "请"),
        (r"必须", "需要"),
        (r"千万不要", "请避免"),
        (r"永远", ""),
    ]
    for pat, rep in replacements:
        t = re.sub(pat, rep, t)

    lines = [ln.strip() for ln in t.replace("\r", "").split("\n")]
    cleaned: list[str] = []
    for ln in lines:
        ln = re.sub(r"[ \t]{2,}", " ", ln).strip(" ，；")
        if not ln:
            if cleaned and cleaned[-1] != "":
                cleaned.append("")
            continue
        cleaned.append(ln)
    while cleaned and cleaned[-1] == "":
        cleaned.pop()
    t = "\n".join(cleaned).strip()
    if len(t) < max(4, len(original) // 5):
        return original
    return t


def should_process(key: str, value: str) -> bool:
    if key.startswith("Localization.Meta"):
        return False
    # Keep pure icons / short tokens
    if len(value.strip()) <= 2 and not any("\u4e00" <= c <= "\u9fff" for c in value):
        return False
    return True


def rewrite_value(key: str, value: str, lang: str) -> str:
    overrides = EN_KEY_OVERRIDES if lang == "en" else ZH_KEY_OVERRIDES
    if key in overrides:
        return overrides[key]

    if not should_process(key, value):
        return value

    # Short labels: light touch only
    if len(value) < 18 and "\n" not in value:
        if lang == "en":
            v = value
            v = re.sub(r"\s*\(not recommended\)\s*", "", v, flags=re.I)
            v = re.sub(r"\s*\(recommended\)\s*", "", v, flags=re.I)
            return v.strip() or value
        v = value
        v = v.replace("（不推荐）", "").replace("(不推荐)", "")
        v = v.replace("（推荐）", "").replace("(推荐)", "")
        return v.strip() or value

    if lang == "en":
        return soften_en(value)
    return soften_zh(value)


def process_file(path: Path, lang: str) -> tuple[int, int]:
    text = path.read_text(encoding="utf-8")
    changed = 0
    total = 0

    def repl(m: re.Match[str]) -> str:
        nonlocal changed, total
        open_tag, key, body, close_tag = m.group(1), m.group(2), m.group(3), m.group(4)
        total += 1
        plain = unescape(body)
        new_plain = rewrite_value(key, plain, lang)
        if new_plain != plain:
            changed += 1
        return open_tag + escape(new_plain) + close_tag

    new_text = STRING_RE.sub(repl, text)
    if new_text != text:
        path.write_text(new_text, encoding="utf-8", newline="\n")
    return total, changed


def main() -> int:
    targets = [
        (ROOT / "PCL.Desktop" / "Localization" / "en-US.xaml", "en"),
        (ROOT / "PCL.Desktop" / "Themes" / "PclTheme.axaml", "zh"),
    ]
    for path, lang in targets:
        if not path.is_file():
            print(f"missing: {path}", file=sys.stderr)
            return 1
        total, changed = process_file(path, lang)
        print(f"{path.relative_to(ROOT)}: {changed}/{total} strings updated ({lang})")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
