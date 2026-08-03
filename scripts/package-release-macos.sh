#!/usr/bin/env bash
# Copyright (c) 2026 PCL N contributors.
# Licensed under the Apache License, Version 2.0.

set -euo pipefail

if [[ $# -ne 3 ]]; then
  echo "Usage: $0 <artifact-directory> <output-directory> <base-name>" >&2
  exit 2
fi

artifact_dir="$(cd "$1" && pwd)"
output_dir="$2"
base_name="$3"
app="$artifact_dir/PCL N.app"
binary="$app/Contents/MacOS/PCL-N-Edition"

test -d "$app"
test -x "$binary"
mkdir -p "$output_dir"
output_dir="$(cd "$output_dir" && pwd)"

# Keep the canonical archive unchanged for launchers using the existing updater.
tar -C "$artifact_dir" -czf "$output_dir/${base_name}.tar.gz" "PCL N.app"

dmg_root="$(mktemp -d)"
trap 'rm -rf "$dmg_root"' EXIT
ditto "$app" "$dmg_root/PCL N.app"
ln -s /Applications "$dmg_root/Applications"

hdiutil create \
  -volname "PCL N" \
  -srcfolder "$dmg_root" \
  -format UDZO \
  -ov \
  "$output_dir/${base_name}_Installer.dmg"

test -s "$output_dir/${base_name}.tar.gz"
test -s "$output_dir/${base_name}_Installer.dmg"
