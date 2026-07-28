# Plugin sidecar IPC (host AOT + CoreCLR plugin process)

## Goal

| Process | Runtime | Role |
|---------|---------|------|
| `PCL.Desktop` | Native AOT capable | Shell + host settings UI + thin IPC client |
| `PCL.Plugin.Sidecar` | CoreCLR | Plugin runtime, `.pnp` catalog, install (headless) |

## Layout

```
PCL.Desktop.exe
sidecar/
  PCL.Plugin.Sidecar.exe
  PCL.Plugin.dll
  …
```

Resolve: `PCL_PLUGIN_SIDECAR_PATH` → `{base}/sidecar/…` → `{base}/…` → dev bin.

## Protocol

Length-prefixed (BE u32) UTF-8 JSON. Protocol version **2**.

| Method | Role |
|--------|------|
| `system.hello` / `system.shutdown` / `health.ping` | Lifecycle |
| `runtime.init` | Data/cache roots + bootstrap + LoadEnabled |
| `runtime.status` | installed/enabled counts + runtime root |
| `catalog.list` | Installed plugins |
| `catalog.installPnp` | Install package path |
| `catalog.setEnabled` | Enable / disable |
| `catalog.uninstall` | Uninstall |

`ui.openSettings` is **not** used (host owns management UX).

Host DTOs: `PluginSidecarJsonContext` (AOT source-gen).

## Host UX

Settings → **插件平台 → 侧车与目录** (`PageSetupPluginSidecar`): status, list, install `.pnp`.  
Drag-and-drop `.pnp` → `PluginSidecarPnpFileArtifactHandler`.

## Packaging

```powershell
.\scripts\build-plugin-sidecar.ps1 -Publish -Runtime win-x64
.\scripts\build-desktop.ps1 -WithPlugin -Publish -Aot -Runtime win-x64
# → artifacts/desktop-win-x64/ + sidecar/
```

CI (`reusable-build.yml` with `include_plugin: true`):

1. Fetch PCL.Plugin tag source (`SkipRewrite`)
2. Publish host (no plugin IL)
3. `build-plugin-sidecar.ps1 -Publish` → `$PUBLISH_DIR/sidecar/`

## Non-goals

- Sidecar-owned Avalonia settings window
- In-process `PclWithPlugin` into Desktop product packages
- Cross-process UI composition into host chrome
