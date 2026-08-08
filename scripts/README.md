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

`generate_update_blockmap.py` extracts a canonical scatter archive, applies
`pcln-fastcdc-v1`, writes deterministic gzip blocks, and emits the signed-map-ready
manifest used by launcher 1.4.3 and newer:

```text
block/ab/abcdef...                 # full SHA-256 is the object name
manifests/PCL_N_....blockmap.json
```

The public mTLS endpoint is `/v1/updates/block/ab/abcdef...`. GitHub Release is
only for installers and portable downloads; updater archives, maps and blocks are
published to Cloudflare R2.
