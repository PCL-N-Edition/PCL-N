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
.\scripts\apply-plugin-overlay.ps1 -Channel Stable -SkipRewrite
```

Design: [docs/architecture/plugin-sidecar-ipc.md](../docs/architecture/plugin-sidecar-ipc.md).
