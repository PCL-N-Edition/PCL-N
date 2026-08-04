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
host_bin="$app/Contents/MacOS/host/PCL-N-Host"
native_dir="$app/Contents/MacOS/native"

if [[ ! -d "$app" ]]; then
  echo "macOS app bundle not found at: $app" >&2
  echo "Artifact tree:" >&2
  find "$artifact_dir" -maxdepth 4 -print >&2 || true
  exit 1
fi

if [[ ! -f "$binary" ]]; then
  echo "Product entry not found at: $binary" >&2
  find "$app" -maxdepth 5 -print >&2 || true
  exit 1
fi
if [[ ! -f "$host_bin" ]]; then
  echo "Scatter AOT host not found at: $host_bin" >&2
  find "$app" -maxdepth 5 -print >&2 || true
  exit 1
fi
if [[ ! -d "$native_dir" ]]; then
  echo "Expanded native/ tree not found at: $native_dir" >&2
  exit 1
fi
if find "$app/Contents/MacOS" -type f -name '*.zip' | grep -q .; then
  echo "App bundle must not contain .zip files (fully expanded scatter):" >&2
  find "$app/Contents/MacOS" -type f -name '*.zip' -print >&2
  exit 1
fi

# Artifact upload/download may drop the executable bit.
chmod +x "$binary" "$host_bin" || true
find "$app/Contents/MacOS" -type f \( -name 'PCL-N-*' -o -name 'pcln-*' \) -exec chmod +x {} + 2>/dev/null || true
test -x "$binary"
mkdir -p "$output_dir"
output_dir="$(cd "$output_dir" && pwd)"

echo "Disk before packaging:"
df -h || true

# Canonical archive for the updater (created before we move the .app).
tar -C "$artifact_dir" -czf "$output_dir/${base_name}.tar.gz" "PCL N.app"
test -s "$output_dir/${base_name}.tar.gz"
echo "Created ${base_name}.tar.gz ($(du -h "$output_dir/${base_name}.tar.gz" | awk '{print $1}'))"

# Staging for DMG contents. Prefer move over ditto to avoid a second full copy of
# large SelfContained bundles (GHA macOS runners previously hit ENOSPC on arm64).
dmg_root="$(mktemp -d "${RUNNER_TEMP:-/tmp}/pcln-dmg.XXXXXX")"
mount_dir="$(mktemp -d "${RUNNER_TEMP:-/tmp}/pcln-mnt.XXXXXX")"
rw_dmg=""
cleanup() {
  if [[ -n "${mount_dir:-}" && -d "$mount_dir" ]]; then
    hdiutil detach "$mount_dir" -force >/dev/null 2>&1 || true
  fi
  rm -rf "${dmg_root:-}" "${mount_dir:-}" ${rw_dmg:+"$rw_dmg"} 2>/dev/null || true
}
trap cleanup EXIT

mv "$app" "$dmg_root/PCL N.app"
ln -s /Applications "$dmg_root/Applications"
# Drop leftover artifact files so hdiutil has more free space.
find "$artifact_dir" -mindepth 1 -maxdepth 1 -exec rm -rf {} + 2>/dev/null || true

echo "Disk before hdiutil:"
df -h || true

app_mb="$(du -sm "$dmg_root" | awk '{print $1}')"
image_mb=$((app_mb + 64))
if (( image_mb < 80 )); then
  image_mb=80
fi

rw_dmg="$output_dir/${base_name}.rw.dmg"
final_dmg="$output_dir/${base_name}_Installer.dmg"
rm -f "$rw_dmg" "$final_dmg"

# Sized RW image + explicit mountpoint (volume names with spaces break path parsing).
hdiutil create \
  -size "${image_mb}m" \
  -fs HFS+ \
  -volname "PCLN" \
  -ov \
  "$rw_dmg"

hdiutil attach \
  -readwrite \
  -noverify \
  -noautoopen \
  -mountpoint "$mount_dir" \
  "$rw_dmg"

test -d "$mount_dir"
ditto "$dmg_root/PCL N.app" "$mount_dir/PCL N.app"
ln -sf /Applications "$mount_dir/Applications"
sync

hdiutil detach "$mount_dir"
# Prevent cleanup from double-detaching a gone mount.
rmdir "$mount_dir" 2>/dev/null || true
mount_dir=""

# Free staging before convert so UDZO compression has room.
rm -rf "$dmg_root"
dmg_root=""

echo "Disk before convert:"
df -h || true

hdiutil convert "$rw_dmg" \
  -format UDZO \
  -imagekey zlib-level=9 \
  -ov \
  -o "$final_dmg"

rm -f "$rw_dmg"
rw_dmg=""

test -s "$final_dmg"
echo "Created $(basename "$final_dmg") ($(du -h "$final_dmg" | awk '{print $1}'))"
echo "Disk after packaging:"
df -h || true
