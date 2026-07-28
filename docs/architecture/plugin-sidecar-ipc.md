# Plugin sidecar IPC (host AOT + CoreCLR plugin process)

## Goal

| Process | Runtime | Role |
|---------|---------|------|
| `PCL.Desktop` | Native AOT capable | Shell only; thin IPC client |
| `PCL.Plugin.Sidecar` | CoreCLR | Full plugin platform (ALC, Harmony, market, settings) |

## Layout

```
PCL.Desktop.exe
sidecar/
  PCL.Plugin.Sidecar.exe   # or next to host on Unix
  PCL.Plugin.dll
  …
```

Resolve order: `PCL_PLUGIN_SIDECAR_PATH` → `{base}/sidecar/…` → `{base}/…` → dev bin path.

## Protocol

Length-prefixed (big-endian u32) UTF-8 JSON request/response frames.

Methods (phase 1): `system.hello`, `system.shutdown`, `health.ping`, `runtime.init`, `ui.openSettings`, `catalog.list`, `catalog.installPnp`.

Host DTOs use `System.Text.Json` source generation (`PluginSidecarJsonContext`) for AOT.

## Lifecycle

1. Host `DesktopHost.InitializeOptionalRuntime` warm-starts `PluginSidecarSupervisor`.
2. Missing binary → plugin features off (shell continues).
3. Host exit disposes supervisor (shutdown RPC + kill).

## UI (phase 1)

Settings UI stays **in the sidecar process** (window deferred; runtime init is live). Host may call `ui.openSettings` as a stub until Avalonia shell is attached to the sidecar.

## Non-goals

- In-process `PclWithPlugin` compile-into-Desktop as product path
- Cross-process Avalonia visual tree patches into host chrome
