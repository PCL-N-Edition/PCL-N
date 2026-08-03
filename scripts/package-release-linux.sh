#!/usr/bin/env bash
# Copyright (c) 2026 PCL N contributors.
# Licensed under the Apache License, Version 2.0.

set -euo pipefail

if [[ $# -ne 5 ]]; then
  echo "Usage: $0 <artifact-directory> <output-directory> <base-name> <version> <x64|arm64>" >&2
  exit 2
fi

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
artifact_dir="$(cd "$1" && pwd)"
output_dir="$2"
base_name="$3"
version_input="$4"
architecture="$5"
binary="$artifact_dir/PCL-N-Edition"

test -s "$binary"
chmod +x "$binary"
mkdir -p "$output_dir"
output_dir="$(cd "$output_dir" && pwd)"

version="$(printf '%s' "$version_input" | sed -nE 's/^v?([0-9]+\.[0-9]+\.[0-9]+).*/\1/p')"
if [[ -z "$version" ]]; then
  echo "Cannot derive a package version from '$version_input'." >&2
  exit 1
fi

case "$architecture" in
  x64)
    deb_arch=amd64
    rpm_arch=x86_64
    appimage_arch=x86_64
    ;;
  arm64)
    deb_arch=arm64
    rpm_arch=aarch64
    appimage_arch=aarch64
    ;;
  *)
    echo "Unsupported architecture: $architecture" >&2
    exit 2
    ;;
esac

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

# Keep the canonical archive unchanged for launchers using the existing updater.
tar -C "$artifact_dir" -czf "$output_dir/${base_name}.tar.gz" .
tar -C "$artifact_dir" -czf "$output_dir/${base_name}_Portable.tar.gz" .

make_launcher_wrapper() {
  local path="$1"
  local install_kind="$2"
  # Parent dirs (e.g. deb_root/usr/bin) are not created by install -Dm on the binary alone.
  mkdir -p "$(dirname "$path")"
  cat >"$path" <<EOF
#!/bin/sh
export PCL_N_INSTALL_KIND=$install_kind
exec /opt/pcl-n/PCL-N-Edition "\$@"
EOF
  chmod 0755 "$path"
}

# Debian package: direct loose-file installation into /opt with a stable /usr/bin entry point.
deb_root="$work/deb"
install -Dm0755 "$binary" "$deb_root/opt/pcl-n/PCL-N-Edition"
make_launcher_wrapper "$deb_root/usr/bin/pcl-n" deb
install -Dm0644 "$repo_root/installer/linux/pcl-n.desktop" "$deb_root/usr/share/applications/pcl-n.desktop"
install -Dm0644 "$repo_root/PCL.Desktop/Assets/icon.png" "$deb_root/usr/share/icons/hicolor/256x256/apps/pcl-n.png"
mkdir -p "$deb_root/DEBIAN"
installed_size="$(du -sk "$deb_root/opt" | awk '{print $1}')"
cat >"$deb_root/DEBIAN/control" <<EOF
Package: pcl-n
Version: $version
Section: games
Priority: optional
Architecture: $deb_arch
Installed-Size: $installed_size
Maintainer: PCL N contributors
Homepage: https://pcln.top/
Description: Next-generation cross-platform Minecraft launcher
EOF
dpkg-deb --build --root-owner-group "$deb_root" "$output_dir/${base_name}_Installer.deb"

# RPM package with the same direct /opt layout.
rpm_top="$work/rpmbuild"
mkdir -p "$rpm_top"/{BUILD,BUILDROOT,RPMS,SOURCES,SPECS,SRPMS}
install -m0755 "$binary" "$rpm_top/SOURCES/PCL-N-Edition"
make_launcher_wrapper "$rpm_top/SOURCES/pcl-n" rpm
install -m0644 "$repo_root/installer/linux/pcl-n.desktop" "$rpm_top/SOURCES/pcl-n.desktop"
install -m0644 "$repo_root/PCL.Desktop/Assets/icon.png" "$rpm_top/SOURCES/pcl-n.png"
cat >"$rpm_top/SPECS/pcl-n.spec" <<'EOF'
Name: pcl-n
Version: __VERSION__
Release: 1%{?dist}
Summary: Next-generation cross-platform Minecraft launcher
License: Apache-2.0
URL: https://pcln.top/
BuildArch: __ARCH__

%description
PCL N is a next-generation cross-platform Minecraft launcher.

%install
mkdir -p %{buildroot}/opt/pcl-n
mkdir -p %{buildroot}/usr/bin
mkdir -p %{buildroot}/usr/share/applications
mkdir -p %{buildroot}/usr/share/icons/hicolor/256x256/apps
install -m0755 %{_sourcedir}/PCL-N-Edition %{buildroot}/opt/pcl-n/PCL-N-Edition
install -m0755 %{_sourcedir}/pcl-n %{buildroot}/usr/bin/pcl-n
install -m0644 %{_sourcedir}/pcl-n.desktop %{buildroot}/usr/share/applications/pcl-n.desktop
install -m0644 %{_sourcedir}/pcl-n.png %{buildroot}/usr/share/icons/hicolor/256x256/apps/pcl-n.png

%files
/opt/pcl-n/PCL-N-Edition
/usr/bin/pcl-n
/usr/share/applications/pcl-n.desktop
/usr/share/icons/hicolor/256x256/apps/pcl-n.png
EOF
sed -i "s/__VERSION__/$version/g; s/__ARCH__/$rpm_arch/g" "$rpm_top/SPECS/pcl-n.spec"
rpmbuild -bb --define "_topdir $rpm_top" --target "$rpm_arch" "$rpm_top/SPECS/pcl-n.spec"
rpm_file="$(find "$rpm_top/RPMS" -type f -name '*.rpm' -print -quit)"
test -n "$rpm_file"
cp "$rpm_file" "$output_dir/${base_name}_Installer.rpm"

# AppImage AppDir. APPIMAGE/APPDIR are supplied by the type-2 runtime, and
# PCL_N_INSTALL_KIND prevents the launcher from replacing its read-only payload.
app_dir="$work/PCL-N.AppDir"
install -Dm0755 "$binary" "$app_dir/usr/bin/PCL-N-Edition"
install -Dm0644 "$repo_root/installer/linux/pcl-n.desktop" "$app_dir/pcl-n.desktop"
install -Dm0644 "$repo_root/PCL.Desktop/Assets/icon.png" "$app_dir/pcl-n.png"
ln -s pcl-n.png "$app_dir/.DirIcon"
cat >"$app_dir/AppRun" <<'EOF'
#!/bin/sh
export PCL_N_INSTALL_KIND=appimage
exec "$APPDIR/usr/bin/PCL-N-Edition" "$@"
EOF
chmod 0755 "$app_dir/AppRun"

appimagetool="${APPIMAGETOOL:-$(command -v appimagetool || true)}"
if [[ -z "$appimagetool" ]]; then
  echo "appimagetool is required (set APPIMAGETOOL or add it to PATH)." >&2
  exit 1
fi
appimage_out="$output_dir/${base_name}_Installer.AppImage"
ARCH="$appimage_arch" "$appimagetool" --appimage-extract-and-run \
  "$app_dir" "$appimage_out"
# GitHub Releases / browser downloads drop Unix mode bits, but keep the
# artifact executable on the runner so local CI/smoke and tar-based
# redistributions preserve the bit when possible.
chmod a+x "$appimage_out"
if [[ ! -x "$appimage_out" ]]; then
  echo "AppImage is not executable after packaging: $appimage_out" >&2
  ls -la "$appimage_out" >&2 || true
  exit 1
fi
# ELF AppImages must be type-2 (offset header); reject empty/corrupt output.
test -s "$appimage_out"
file "$appimage_out" || true

for package in \
  "$output_dir/${base_name}.tar.gz" \
  "$output_dir/${base_name}_Portable.tar.gz" \
  "$output_dir/${base_name}_Installer.deb" \
  "$output_dir/${base_name}_Installer.rpm" \
  "$appimage_out"; do
  test -s "$package"
done
