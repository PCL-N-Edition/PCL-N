# Local scripts

## Host-only Desktop

```powershell
.\scripts\build-desktop.ps1
.\scripts\build-desktop.ps1 -Publish -Runtime win-x64
.\scripts\build-desktop.ps1 -Publish -Aot -Runtime win-x64
```

## Plugin source-overlay inject

**Release product is a PCL.Plugin git tag (source + `host-overlay/`), not DLL assets.**

```powershell
# Formal release tag (releases/latest → checkout that tag's source)
.\scripts\apply-plugin-overlay.ps1 -Channel Stable

# Newest v* git tag
.\scripts\apply-plugin-overlay.ps1 -Channel Latest

# Pin source tag
.\scripts\apply-plugin-overlay.ps1 -Tag v0.17.0

# Build host with plugin sources compiled in
.\scripts\build-desktop.ps1 -WithPlugin -SkipPluginFetch

# Undo host rewrite dirt (DesktopHost.Optional.cs etc.)
.\scripts\apply-plugin-overlay.ps1 -RestoreHostRewrites -SkipFetch

.\scripts\run-plugin-ui.ps1 -SkipFetch
```

Design: [docs/architecture/plugin-source-overlay.md](../docs/architecture/plugin-source-overlay.md).
