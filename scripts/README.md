# Local scripts

## Host-only Desktop

```powershell
.\scripts\build-desktop.ps1
.\scripts\build-desktop.ps1 -Publish -Runtime win-x64
.\scripts\build-desktop.ps1 -Publish -Aot -Runtime win-x64
```

## Plugin source-overlay inject

Clone or fetch private `PCL.Plugin` at a release tag, apply host rewrites, compile plugin sources into Desktop:

```powershell
# Latest tag
.\scripts\apply-plugin-overlay.ps1

# Pin
.\scripts\apply-plugin-overlay.ps1 -Tag v0.16.0

# Build / run with plugin
.\scripts\build-desktop.ps1 -WithPlugin
.\scripts\run-plugin-ui.ps1 -SkipFetch
.\scripts\test-plugin-ui.ps1 -SkipFetch
```

`-SkipFetch` / `-SkipPluginFetch` reuse an existing `PCL.Plugin/` tree (for local development).

Design notes: [docs/architecture/plugin-source-overlay.md](../docs/architecture/plugin-source-overlay.md).
