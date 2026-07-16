#!/usr/bin/env python3
"""Audit en-US / zh-CN keys vs code/XAML usage."""

from __future__ import annotations

import re
from collections import Counter
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DESKTOP = ROOT / "PCL.Desktop"

KEY_ATTR = re.compile(r'x:Key="([^"]+)"')
# Usage patterns
USAGE_PATTERNS = [
    re.compile(r'DynamicResource\s+([A-Za-z0-9_.]+)'),
    re.compile(r'StaticResource\s+([A-Za-z0-9_.]+)'),
    re.compile(r'GetText\(\s*"([^"]+)"'),
    re.compile(r"GetText\(\s*'([^']+)'"),
    re.compile(r'GetResourceText\(\s*"([^"]+)"'),
    re.compile(r'TryGetResource\(\s*"([^"]+)"'),
]


def load_keys(path: Path) -> set[str]:
    text = path.read_text(encoding="utf-8")
    return set(KEY_ATTR.findall(text))


def collect_usages() -> Counter[str]:
    counts: Counter[str] = Counter()
    for path in DESKTOP.rglob("*"):
        if path.suffix.lower() not in {".cs", ".axaml", ".xaml"}:
            continue
        if "Localization" in path.parts and path.name.endswith(".xaml"):
            continue  # don't count key definitions as usage
        try:
            text = path.read_text(encoding="utf-8")
        except OSError:
            continue
        for pat in USAGE_PATTERNS:
            for m in pat.finditer(text):
                counts[m.group(1)] += 1
    return counts


def main() -> None:
    en = load_keys(DESKTOP / "Localization" / "en-US.xaml")
    zh = load_keys(DESKTOP / "Localization" / "zh-CN.xaml")
    usage = collect_usages()
    used = set(usage)

    en_only = en - zh
    zh_only = zh - en
    en_unused = en - used
    zh_unused = zh - used
    en_only_unused = en_only - used
    en_only_used = en_only & used
    used_missing_both = used - en - zh

    print("=== counts ===")
    print(f"en-US keys:     {len(en)}")
    print(f"zh-CN keys:     {len(zh)}")
    print(f"referenced:     {len(used)}")
    print()
    print("=== coverage ===")
    print(f"en only (not in zh):           {len(en_only)}")
    print(f"  of which USED in code/xaml:  {len(en_only_used)}")
    print(f"  of which NEVER referenced:   {len(en_only_unused)}")
    print(f"zh only (not in en):           {len(zh_only)}")
    print(f"en defined but never used:     {len(en_unused)}")
    print(f"zh defined but never used:     {len(zh_unused)}")
    print(f"used but missing in both:      {len(used_missing_both)}")
    print()
    print("=== sample: en-only AND used (need Chinese or intentional EN-only) ===")
    for k in sorted(en_only_used)[:25]:
        print(f"  {k}  (refs={usage[k]})")
    print()
    print("=== sample: en-only AND unused (likely redundant) ===")
    for k in sorted(en_only_unused)[:30]:
        print(f"  {k}")
    print()
    print("=== sample: en unused (including those also in zh) ===")
    for k in sorted(en_unused)[:30]:
        print(f"  {k}")
    print()
    print("=== sample: used missing both catalogs ===")
    for k in sorted(used_missing_both)[:20]:
        print(f"  {k}  (refs={usage[k]})")


if __name__ == "__main__":
    main()
