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

Length-prefixed (BE u32) UTF-8 JSON. Methods: `system.hello`, `system.shutdown`, `health.ping`, `runtime.init`, `catalog.list`, `catalog.installPnp`.  
`ui.openSettings` is **not** used (no independent plugin window).

Host DTOs: `PluginSidecarJsonContext` (AOT source-gen).

## Host UX

Settings → **插件平台 → 侧车与目录** (`PageSetupPluginSidecar`): status, list, install `.pnp`.  
Drag-and-drop `.pnp` → `PluginSidecarPnpFileArtifactHandler`.

## Packaging

```powershell
.\scripts\build-plugin-sidecar.ps1 -Publish -Runtime win-x64
.\scripts\build-desktop.ps1 -WithPlugin -Publish -Aot -Runtime win-x64
```

## Non-goals

- Sidecar-owned Avalonia settings window
- In-process `PclWithPlugin` into Desktop product packages
- Cross-process UI composition into host chrome
