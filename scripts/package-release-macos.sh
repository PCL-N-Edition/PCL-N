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

if [[ ! -d "$app" ]]; then
  echo "macOS app bundle not found at: $app" >&2
  echo "Artifact tree:" >&2
  find "$artifact_dir" -maxdepth 4 -print >&2 || true
  exit 1
fi

if [[ ! -f "$binary" ]]; then
  echo "Launcher binary not found at: $binary" >&2
  find "$app" -maxdepth 4 -print >&2 || true
  exit 1
fi

# Artifact upload/download may drop the executable bit.
chmod +x "$binary"
test -x "$binary"
mkdir -p "$output_dir"
output_dir="$(cd "$output_dir" && pwd)"

echo "Disk before packaging:"
df -h "$artifact_dir" "$output_dir" /var/folders 2>/dev/null || df -h

# Keep the canonical archive unchanged for launchers using the existing updater.
# Create tar.gz while the app still lives under artifact_dir.
tar -C "$artifact_dir" -czf "$output_dir/${base_name}.tar.gz" "PCL N.app"
test -s "$output_dir/${base_name}.tar.gz"
echo "Created ${base_name}.tar.gz ($(du -h "$output_dir/${base_name}.tar.gz" | awk '{print $1}'))"

# Stage DMG contents without a second full copy of the .app (ditto doubles disk use
# and hdiutil needs another temporary image — that exhausted GHA macOS arm64 runners).
dmg_root="$(mktemp -d "${RUNNER_TEMP:-/tmp}/pcln-dmg.XXXXXX")"
cleanup() {
  # Detach any leftover volume mounts for this staging tree.
  if [[ -n "${dmg_mount:-}" && -d "$dmg_mount" ]]; then
    hdiutil detach "$dmg_mount" -force >/dev/null 2>&1 || true
  fi
  rm -rf "$dmg_root" "${rw_dmg:-}" 2>/dev/null || true
}
trap cleanup EXIT

# Move (not copy) the app into the staging folder after tar.gz is safe.
mv "$app" "$dmg_root/PCL N.app"
ln -s /Applications "$dmg_root/Applications"

# Drop empty artifact residue so hdiutil has more free space.
find "$artifact_dir" -mindepth 1 -maxdepth 1 -exec rm -rf {} + 2>/dev/null || true

echo "Disk before hdiutil:"
df -h "$dmg_root" "$output_dir" 2>/dev/null || df -h

# Size a sparse RW image with headroom, copy once into the mounted volume, then
# convert to compressed UDZO. This avoids hdiutil -srcfolder holding multiple
# full-size intermediates at once.
app_mb="$(du -sm "$dmg_root" | awk '{print $1}')"
# +64 MiB headroom for catalog + Applications link + filesystem overhead.
image_mb=$((app_mb + 64))
if (( image_mb < 80 )); then
  image_mb=80
fi

rw_dmg="$output_dir/${base_name}.rw.dmg"
final_dmg="$output_dir/${base_name}_Installer.dmg"
rm -f "$rw_dmg" "$final_dmg"

hdiutil create \
  -size "${image_mb}m" \
  -fs HFS+ \
  -volname "PCL N" \
  -ov \
  "$rw_dmg"

dmg_mount="$(hdiutil attach -readwrite -noverify -noautoopen "$rw_dmg" | awk 'END{print $NF}')"
if [[ -z "$dmg_mount" || ! -d "$dmg_mount" ]]; then
  echo "Failed to attach temporary DMG." >&2
  exit 1
fi

# Single copy into the mounted volume (source is then deletable).
ditto "$dmg_root/PCL N.app" "$dmg_mount/PCL N.app"
ln -sf /Applications "$dmg_mount/Applications"
sync

hdiutil detach "$dmg_mount"
dmg_mount=""

# Free the staging tree before convert (convert needs room for compressed output).
rm -rf "$dmg_root"
dmg_root=""

echo "Disk before convert:"
df -h "$output_dir" 2>/dev/null || df -h

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
df -h "$output_dir" 2>/dev/null || df -h
