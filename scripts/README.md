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

GitHub Release is only for installers and portable downloads; updater maps and
blocks are published to Cloudflare R2.
