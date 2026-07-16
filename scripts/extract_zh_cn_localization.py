#!/usr/bin/env python3
"""
Extract all <x:String> entries from PclTheme.axaml into Localization/zh-CN.xaml,
then remove those string resources from the theme (styles only).
"""

from __future__ import annotations

import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
THEME = ROOT / "PCL.Desktop" / "Themes" / "PclTheme.axaml"
OUT = ROOT / "PCL.Desktop" / "Localization" / "zh-CN.xaml"

STRING_RE = re.compile(
    r"[ \t]*<x:String\s+x:Key=\"([^\"]+)\">(.*?)</x:String>\s*\n?",
    re.S,
)


def unescape(s: str) -> str:
    return (
        s.replace("&#x0a;", "\n")
        .replace("&#x0A;", "\n")
        .replace("&quot;", '"')
        .replace("&amp;", "&")
        .replace("&lt;", "<")
        .replace("&gt;", ">")
    )


def escape(s: str) -> str:
    s = (
        s.replace("&", "&amp;")
        .replace("<", "&lt;")
        .replace(">", "&gt;")
        .replace('"', "&quot;")
    )
    return s.replace("\r\n", "\n").replace("\r", "\n").replace("\n", "&#x0a;")


def main() -> int:
    theme = THEME.read_text(encoding="utf-8")
    entries: list[tuple[str, str]] = []
    for m in STRING_RE.finditer(theme):
        entries.append((m.group(1), unescape(m.group(2))))

    if not entries:
        print("No x:String entries found in PclTheme.axaml")
        return 1

    # Stable order: as found in theme
    lines = [
        "<ResourceDictionary",
        '    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"',
        '    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"',
        '    xmlns:sys="clr-namespace:System;assembly=mscorlib"',
        '    xml:space="preserve">',
        "",
        "    <!-- Meta -->",
        "",
        '    <sys:String x:Key="Localization.Meta.Code">zh-CN</sys:String>',
        '    <sys:String x:Key="Localization.Meta.Name">简体中文</sys:String>',
        "",
        "    <!-- Extracted from Themes/PclTheme.axaml -->",
        "",
    ]
    seen: set[str] = set()
    for key, val in entries:
        if key in seen:
            continue
        seen.add(key)
        lines.append(f'    <sys:String x:Key="{key}">{escape(val)}</sys:String>')
    lines.append("")
    lines.append("</ResourceDictionary>")
    lines.append("")

    OUT.parent.mkdir(parents=True, exist_ok=True)
    OUT.write_text("\n".join(lines), encoding="utf-8", newline="\n")
    print(f"Wrote {OUT.relative_to(ROOT)} ({len(seen)} strings)")

    new_theme, n = STRING_RE.subn("", theme)
    # Clean accidental blank runs inside Resources
    new_theme = re.sub(r"\n{3,}", "\n\n", new_theme)
    THEME.write_text(new_theme, encoding="utf-8", newline="\n")
    print(f"Removed {n} x:String entries from {THEME.relative_to(ROOT)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
