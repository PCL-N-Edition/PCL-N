# Local scripts

## Host-only Desktop

```powershell
.\scripts\build-desktop.ps1
.\scripts\build-desktop.ps1 -Publish -Runtime win-x64
.\scripts\build-desktop.ps1 -Publish -Aot -Runtime win-x64
```

## Plugin sidecar (out-of-process, host AOT-safe)

```powershell
# Build CoreCLR sidecar only
.\scripts\build-plugin-sidecar.ps1 -SkipFetch

# Host + sidecar (host may use -Aot)
.\scripts\build-desktop.ps1 -WithPlugin -SkipPluginFetch
.\scripts\build-desktop.ps1 -WithPlugin -Publish -Aot -Runtime win-x64

# Optional: fetch plugin tag source first (no host rewrite required for sidecar)
.\scripts\apply-plugin-overlay.ps1 -Channel Latest -SkipRewrite
```

Design: [docs/architecture/plugin-sidecar-ipc.md](../docs/architecture/plugin-sidecar-ipc.md).

## Cloudflare content-addressed launcher updates

`generate_update_blockmap.py` extracts a canonical scatter archive (or a single
portable file), applies FastCDC, writes deterministic gzip blocks, and emits
**dual** signed-map-ready manifests (default `--profile both`):

```text
block/ab/abcdef...                      # raw-chunk SHA-256 is the object name
manifests/PCL_N_....blockmap.json       # pcln-fastcdc-v1 (256K/1M/2M)
manifests/PCL_N_....blockmap.v2.json    # pcln-fastcdc-v2 (128K/512K/1M)
```

| Profile | Algorithm | Min / Avg / Max | Asset suffix |
|---------|-----------|-----------------|--------------|
| v1 | `pcln-fastcdc-v1` | 256 KiB / 1 MiB / 2 MiB | `.blockmap.json` |
| v2 | `pcln-fastcdc-v2` | 128 KiB / 512 KiB / 1 MiB | `.blockmap.v2.json` |

Use `--profile v1|v2|both` to control emission. CAS blocks are shared by content
hash across profiles. The public mTLS endpoint remains `/v1/updates/block/ab/…`.
New clients prefer `.blockmap.v2.json` and fall back to `.blockmap.json`.

### VCDIFF (protocol v2)

Pass up to two previous maps for source windows:

```bash
python scripts/generate_update_blockmap.py --file … --profile both \
  --previous-blockmap previous/PCL_N_….blockmap.v2.json \
  --previous-blockmap previous2/PCL_N_….blockmap.v2.json
```

For each new target chunk the publisher:

1. Picks ≤3 source windows from the same relative path (±1 old chunks, ≤4 MiB)
2. Encodes RFC 3284 VCDIFF (`scripts/pcln_vcdiff.py`)
3. Admits when `delta ≤ 0.7 × full.gz` **and** saves ≥ 16 KiB
4. Stores at most **2** deltas under `delta/v2/<hh>/<target>/<sourceWindowSha>.vcdiff`
5. Emits nested `full` + `deltas[]` on v2 chunk entries

Without `--previous-blockmap`, v2 maps still emit nested `full` only when deltas run;
current dual-publish keeps flat chunk fields for compatibility.

### R2 CAS upload (protocol v2 §15–19)

`upload_r2_cas.py` replaces per-object `wrangler r2 object put` loops:

```bash
# Preferred: R2 S3 API tokens
export CLOUDFLARE_ACCOUNT_ID=…
export R2_ACCESS_KEY_ID=…
export R2_SECRET_ACCESS_KEY=…
export R2_BUCKET=pcln-releases
pip install 'boto3>=1.34'
python scripts/upload_r2_cas.py upload-tree block-dist --prefix block --prefix delta --concurrency 24
```

Behavior:

| Feature | S3 mode | Wrangler fallback |
|---------|---------|-------------------|
| ListObjects inventory skip | yes | no (always put) |
| `If-None-Match: *` | yes (412 = success) | n/a |
| Concurrency | adaptive 8–48 | sequential via pool |
| Secrets | `R2_ACCESS_KEY_ID` + secret | `CLOUDFLARE_API_TOKEN` |

Channel promotion still publishes maps/signatures first, then catalog, then `channels/*.json` last.

GitHub Release is only for installers and portable downloads; updater maps and
blocks are published to Cloudflare R2.
