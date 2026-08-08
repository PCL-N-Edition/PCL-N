#!/usr/bin/env bash
# Assemble fully-expanded multi-file (完全散包) release layout.
#
# User-facing entry keeps product name (PCL-N-Edition[.exe]).
# No payload.zip / native-runtime.zip / sidecar.zip in the final tree —
# dependencies are extracted at assemble (CI) time so installers only copy files.
#
# Usage:
#   assemble-release-layout.sh \
#     --out DIR \
#     --host PATH \
#     --product-name PCL-N-Edition.exe \
#     --native-zip PATH \
#     [--sidecar-zip PATH] \
#     --launcher PATH \
#     --crash PATH

set -euo pipefail

out=""
host=""
product_name=""
native_zip=""
sidecar_zip=""
launcher=""
crash=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --out) out="$2"; shift 2 ;;
    --host) host="$2"; shift 2 ;;
    --product-name) product_name="$2"; shift 2 ;;
    --native-zip) native_zip="$2"; shift 2 ;;
    --sidecar-zip) sidecar_zip="$2"; shift 2 ;;
    --launcher) launcher="$2"; shift 2 ;;
    --crash) crash="$2"; shift 2 ;;
    *) echo "Unknown arg: $1" >&2; exit 2 ;;
  esac
done

if [[ -z "$out" || -z "$host" || -z "$product_name" || -z "$native_zip" || -z "$launcher" ]]; then
  echo "Missing required arguments." >&2
  exit 2
fi

test -s "$host"
test -s "$native_zip"
test -s "$launcher"

if command -v python3 >/dev/null 2>&1; then PY=python3; else PY=python; fi

rm -rf "$out"
mkdir -p "$out/host" "$out/crash" "$out/native"

# Product entry = C launcher (same name users always double-click).
cp -f "$launcher" "$out/$product_name"
chmod +x "$out/$product_name" || true

if [[ "$product_name" == *.exe ]]; then
  host_name="PCL-N-Host.exe"
  crash_name="pcln-crash-handler.exe"
  sidecar_exe="PCL.Plugin.Sidecar.exe"
else
  host_name="PCL-N-Host"
  crash_name="pcln-crash-handler"
  sidecar_exe="PCL.Plugin.Sidecar"
fi
cp -f "$host" "$out/host/$host_name"
chmod +x "$out/host/$host_name" || true

if [[ -n "$crash" && -f "$crash" ]]; then
  cp -f "$crash" "$out/crash/$crash_name"
  chmod +x "$out/crash/$crash_name" || true
fi

# Fully expand native runtime (was formerly embedded zip / runtime zip).
"$PY" -c "
import zipfile
from pathlib import Path
dest = Path(r'''$out/native''')
dest.mkdir(parents=True, exist_ok=True)
with zipfile.ZipFile(r'''$native_zip''') as z:
    z.extractall(dest)
print('Expanded native-runtime into', dest)
"
# Sanity: expect at least one native library file
native_count="$(find "$out/native" -type f | wc -l | tr -d ' ')"
if [[ "$native_count" -lt 1 ]]; then
  echo "native/ tree is empty after extract" >&2
  exit 1
fi

if [[ -n "$sidecar_zip" && -f "$sidecar_zip" ]]; then
  mkdir -p "$out/sidecar"
  "$PY" -c "
import zipfile
from pathlib import Path
dest = Path(r'''$out/sidecar''')
dest.mkdir(parents=True, exist_ok=True)
with zipfile.ZipFile(r'''$sidecar_zip''') as z:
    z.extractall(dest)
print('Expanded sidecar into', dest)
"
  # Flatten one nested folder level if zip root is a single directory.
  if [[ ! -f "$out/sidecar/$sidecar_exe" ]]; then
    found="$(find "$out/sidecar" -type f -name "$sidecar_exe" | head -n 1 || true)"
    if [[ -n "$found" ]]; then
      nested="$(dirname "$found")"
      if [[ "$nested" != "$out/sidecar" ]]; then
        shopt -s dotglob nullglob
        for item in "$nested"/*; do
          base="$(basename "$item")"
          mv -f "$item" "$out/sidecar/$base"
        done
        rmdir "$nested" 2>/dev/null || true
      fi
    fi
  fi
  if [[ -f "$out/sidecar/$sidecar_exe" ]]; then
    chmod +x "$out/sidecar/$sidecar_exe" || true
  else
    echo "warning: $sidecar_exe not found under sidecar/ after extract" >&2
  fi
fi

# No zip leftovers in the published tree.
rm -f "$out"/*.zip "$out"/**/*.zip 2>/dev/null || true
find "$out" -type f -name '*.zip' -delete 2>/dev/null || true

printf 'pcln-scatter-v2-expanded\n' >"$out/pcln-layout"

echo "Assembled fully-expanded scatter layout in $out:"
find "$out" -type f | sort | head -n 80 || true
echo "(file count: $(find "$out" -type f | wc -l | tr -d ' '))"