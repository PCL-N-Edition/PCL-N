# Host-only body + Native AOT packaging

## Product rules

1. **Releases never ship a `NoPlugin` SKU.** Artifacts are host-only: `SelfContained` / `NoRuntime`, unless a pipeline explicitly enables source-overlay inject.
2. **Default host body contains no plugin product code.**  
   No embed of `PCL.Plugin` IL, no `LoadFromStream` plugin loader.
3. **Plugin product is an out-of-process CoreCLR sidecar** (see [plugin-sidecar-ipc.md](./plugin-sidecar-ipc.md)): host stays AOT-capable; `PCL.Plugin.Sidecar` owns ALC / Harmony / market.
4. Legacy in-process source-overlay (`PclWithPlugin=true`) is deprecated for product packages.

Privileged platform / marketplace / `.pnp` products run in the sidecar process only.

## Current host initialization

```
DesktopHost.Initialize
  └─ DesktopNavigationRegistry.RegisterGeneratedHostModules  // built-in shell only
  └─ RegisterOptionalModules (partial; no-op unless overlay rewrite applied)
  └─ PclHostBuilder.Build
  └─ InitializeOptionalRuntime (partial; no-op unless overlay rewrite applied)
```

## Native AOT

Host-only builds can stay AOT-friendly (`PublishAot` / trim analyzers).

- CI smoke: `portable-core.yml` → `desktop-native-aot` publishes host-only AOT on win/linux/mac.
- Local: `.\scripts\build-desktop.ps1 -Publish -Aot ...`
- **WithPlugin builds force CoreCLR** (Harmony / collectible ALC for `.pnp`); do not combine with `-Aot`.

Release pipelines still use CoreCLR single-file until the AOT matrix is promoted for all RIDs (VLC/native deps remain multi-file next to the AOT host).

## Migration notes

| Legacy | Now |
|--------|-----|
| `*_WithPlugin` artifact suffix | Dropped; use `SelfContained` / `NoRuntime` |
| `*_NoPlugin` | Never published |
| Embed `PclPlugin*` MSBuild | Removed |
| `EmbeddedRuntimeExtensionLoader` | Deleted |
| DLL inject props (`PclPluginAssembly=…`) | Replaced by source overlay + `PclWithPlugin=true` |
| Update client `NoPlugin` → `WithPlugin` migration | Keep one release cycle for field upgrades, then remove |

## Out of default host body

- `PCL.Plugin` private product (present only after overlay + `PclWithPlugin`)
- Third-party `.pnp` runtime, Harmony, marketplace (same)
