#!/usr/bin/env bash
# Matrix-local CAS publish (Update Protocol v2 §15).
# Generates blockmaps for this RID's packages (v1 and/or v2 per version gate),
# optionally attaches VCDIFF against the previous channel release, batch-uploads
# block/ + delta/ to R2, and stages manifests for the central sign/promote job.
set -euo pipefail
# Windows Git Bash + Python: force UTF-8 so log prints with arrows/CJK cannot crash.
export PYTHONUTF8=1
export PYTHONIOENCODING=utf-8

PACKAGE_DIR="${1:?package-output directory}"
RELEASE_TAG="${2:?release tag e.g. v1.4.8-beta}"
CONFIGURATION="${3:?Release|Beta}"
CHANNEL="${4:?release|beta}"
RUNTIME_ID="${5:?win-x64|...}"
RUNTIME_VARIANT="${6:?SelfContained|NoRuntime}"
BLOCK_DIST="${7:-block-dist}"
MANIFEST_STAGE="${8:-blockmap-stage}"

TARGET_VERSION="${RELEASE_TAG#v}"
mkdir -p "$BLOCK_DIST" "$MANIFEST_STAGE"

if [[ -z "${CLOUDFLARE_API_TOKEN:-}" || -z "${CLOUDFLARE_ACCOUNT_ID:-}" ]]; then
  echo "::warning::CLOUDFLARE_API_TOKEN/ACCOUNT_ID missing; generating maps only (CAS upload skipped)."
fi
# zstd for v2 full blocks (protocol §20); graceful fallback to gzip if install fails.
python -m pip install --quiet 'zstandard>=0.22' || true

PREV_ARGS=()
PREV_DIR="$(mktemp -d)"
cleanup() { rm -rf "$PREV_DIR"; }
trap cleanup EXIT

read_channel_tag() {
  python - "$1" <<'PY'
import json, sys
from pathlib import Path
path = Path(sys.argv[1])
if not path.is_file():
    raise SystemExit(0)
try:
    data = json.loads(path.read_text(encoding="utf-8"))
except Exception:
    raise SystemExit(0)
tag = data.get("tag") or ""
if isinstance(tag, str) and tag.strip():
    print(tag.strip())
PY
}

# Previous channel release supplies source windows for VCDIFF (N-1).
# Only reuse **v2** maps: pairing v1 windows with v2 FastCDC makes the pure-Python
# encoder thrash for hours on SelfContained packages and times out the matrix.
if python scripts/upload_r2_cas.py get "channels/${CHANNEL}.json" --file "$PREV_DIR/channel.json" 2>/dev/null; then
  PREV_TAG="$(read_channel_tag "$PREV_DIR/channel.json" || true)"
  if [[ -n "${PREV_TAG:-}" && "$PREV_TAG" != "$RELEASE_TAG" ]]; then
    echo "Previous channel tag for VCDIFF: $PREV_TAG"
    while IFS= read -r -d '' asset; do
      name="$(basename "$asset")"
      stem="${name%.tar.gz}"
      stem="${stem%.zip}"
      stem="${stem%_Portable.exe}"
      stem="${stem%.exe}"
      key="releases/${PREV_TAG}/${stem}.blockmap.v2.json"
      dest="$PREV_DIR/${stem}.blockmap.v2.json"
      if python scripts/upload_r2_cas.py get "$key" --file "$dest" 2>/dev/null; then
        PREV_ARGS+=(--previous-blockmap "$dest")
        echo "Using previous v2 map: $key"
      else
        echo "No previous v2 map for $stem (full blocks only)."
      fi
    done < <(find "$PACKAGE_DIR" -maxdepth 1 -type f \( -name '*.zip' -o -name '*.tar.gz' -o -name '*_Portable.exe' \) -print0)
  fi
else
  echo "No previous channel pointer; publishing full blocks only."
fi

# Scatter archive blockmaps (v1+v2 per --profile auto / version gate)
while IFS= read -r -d '' archive; do
  name="$(basename "$archive")"
  if [[ ! "$name" =~ ^PCL_N_(Release|Beta)_((win|linux|osx)-(x64|arm64))_(SelfContained|NoRuntime)\.(zip|tar\.gz)$ ]]; then
    continue
  fi
  if [[ "${BASH_REMATCH[2]}" != "$RUNTIME_ID" || "${BASH_REMATCH[5]}" != "$RUNTIME_VARIANT" ]]; then
    continue
  fi
  python scripts/generate_update_blockmap.py \
    --archive "$archive" \
    --output "$BLOCK_DIST" \
    --target-tag "$RELEASE_TAG" \
    --target-version "$TARGET_VERSION" \
    --runtime-id "${BASH_REMATCH[2]}" \
    --runtime-variant "${BASH_REMATCH[5]}" \
    --configuration "${BASH_REMATCH[1]}" \
    --profile auto \
    "${PREV_ARGS[@]+"${PREV_ARGS[@]}"}"
done < <(find "$PACKAGE_DIR" -maxdepth 1 -type f \( -name '*.zip' -o -name '*.tar.gz' \) -print0)

# Windows portable single-file maps
while IFS= read -r -d '' portable; do
  name="$(basename "$portable")"
  if [[ ! "$name" =~ ^PCL_N_(Release|Beta)_((win)-(x64|arm64))_(SelfContained|NoRuntime)_Portable\.exe$ ]]; then
    continue
  fi
  if [[ "${BASH_REMATCH[2]}" != "$RUNTIME_ID" || "${BASH_REMATCH[5]}" != "$RUNTIME_VARIANT" ]]; then
    continue
  fi
  python scripts/generate_update_blockmap.py \
    --file "$portable" \
    --target-asset-name "$name" \
    --entry-name PCL-N-Edition.exe \
    --output "$BLOCK_DIST" \
    --target-tag "$RELEASE_TAG" \
    --target-version "$TARGET_VERSION" \
    --runtime-id "${BASH_REMATCH[2]}" \
    --runtime-variant "${BASH_REMATCH[5]}" \
    --configuration "${BASH_REMATCH[1]}" \
    --profile auto \
    "${PREV_ARGS[@]+"${PREV_ARGS[@]}"}"
done < <(find "$PACKAGE_DIR" -maxdepth 1 -type f -name '*_Portable.exe' -print0)

map_count="$(find "$BLOCK_DIST/manifests" -maxdepth 1 -type f \( -name '*.blockmap.json' -o -name '*.blockmap.v2.json' \) 2>/dev/null | wc -l | tr -d ' ')"
if [[ "${map_count:-0}" -eq 0 ]]; then
  echo "::error::No block maps produced for $RUNTIME_ID/$RUNTIME_VARIANT"
  exit 1
fi

# Batch CAS publish. Keep concurrency low: 12 matrix jobs share one CF API token.
# Auth reuses CLOUDFLARE_API_TOKEN + CLOUDFLARE_ACCOUNT_ID (no separate R2 S3 keys).
if [[ -n "${CLOUDFLARE_API_TOKEN:-}" && -n "${CLOUDFLARE_ACCOUNT_ID:-}" ]]; then
  if ! python scripts/upload_r2_cas.py upload-tree "$BLOCK_DIST" \
      --prefix block --prefix delta \
      --concurrency 4; then
    echo "::warning::Matrix CAS upload failed/throttled for $RUNTIME_ID/$RUNTIME_VARIANT; maps still staged for central retry."
  fi
else
  echo "::warning::Skipping CAS upload (no Cloudflare credentials on matrix job)."
fi

# Stage manifests for the central sign/promote job (small artifact).
find "$BLOCK_DIST/manifests" -maxdepth 1 -type f \( -name '*.blockmap.json' -o -name '*.blockmap.v2.json' \) \
  -exec cp -a {} "$MANIFEST_STAGE/" \;
echo "Staged $(find "$MANIFEST_STAGE" -type f | wc -l | tr -d ' ') blockmap manifest(s)."
