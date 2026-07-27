# Host-only body + Native AOT packaging

## Product rules

1. **Releases never ship a `NoPlugin` SKU.** Artifacts are host-only: `SelfContained` / `NoRuntime`.
2. **Host body contains no plugin-related code.**  
   No embed of `PCL.Plugin` IL, no `LoadFromStream` plugin loader, no CI download of private plugin releases into Desktop.

Privileged platform / marketplace / `.pnp` products live **outside** this repository’s process (separate product or future IPC sidecar).

## Current host initialization

```
DesktopHost.Initialize
  └─ DesktopNavigationRegistry.RegisterGeneratedHostModules  // built-in shell only
  └─ PclHostBuilder.Build
```

There is no in-process plugin discovery.

## Native AOT

Because the host no longer embeds plugin IL:

- Default Desktop flags can stay AOT-friendly (`PublishAot` / trim analyzers).
- CI smoke: `portable-core.yml` → `desktop-native-aot` publishes host-only AOT on win/linux/mac.
- Local: `.\scripts\build-desktop.ps1 -Publish -Aot ...`
- Release pipelines still use CoreCLR single-file until the AOT matrix is promoted for all RIDs (VLC/native deps remain multi-file next to the AOT host).

## Migration notes

| Legacy | Now |
|--------|-----|
| `*_WithPlugin` artifact suffix | Dropped; use `SelfContained` / `NoRuntime` |
| `*_NoPlugin` | Never published |
| Embed `PclPlugin*` MSBuild | Removed from `PCL.Desktop.csproj` |
| `EmbeddedRuntimeExtensionLoader` | Deleted |
| Update client `NoPlugin` → `WithPlugin` migration | Keep one release cycle for field upgrades, then remove |

## Out of host body (explicit)

- `PCL.Plugin` private product and its dependencies (Harmony, marketplace, N Cloud platform, `.pnp` runtime)
- In-process plugin UI injection (`pcl.plugin.*` tags) — to be removed or rehosted as IPC data later
