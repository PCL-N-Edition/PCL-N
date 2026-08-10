# Host-only body + Native AOT packaging

## Product rules

1. **Releases never ship a `NoPlugin` SKU.** Artifacts are host-only: `SelfContained` / `NoRuntime`, unless a pipeline explicitly enables source-overlay inject.
2. **Default host body contains no plugin product code.**  
   No embed of `PCL.Plugin` IL, no `LoadFromStream` plugin loader.
3. **Plugin product is an out-of-process CoreCLR sidecar** (see [plugin-sidecar-ipc.md](./plugin-sidecar-ipc.md)): host stays AOT-capable; `PCL.Plugin.Sidecar` owns ALC / Harmony / market / DirectInject & IndirectInject.
4. **Sidecar Release binaries are obfuscated (Obfuscar) and ship without PDBs or a host symbol table.** DirectInject targets host methods by assembly/type/method names (or optional local `PCLN_HOST_SYMBOLS_PATH` for debug only).
5. Legacy in-process source-overlay (`PclWithPlugin=true`) is for debug / special builds, not store AOT packages.

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
- In-process source-overlay builds force CoreCLR (Harmony / collectible ALC for `.pnp`); do not combine them with `-Aot`.

Release pipelines publish a self-contained Native AOT host for every supported RID. The
plugin product remains a separate CoreCLR sidecar embedded as an opaque archive and
extracted under the configured data directory.

### Optimization and CPU policy

The production host favors responsiveness without raising its minimum CPU requirement:

```xml
<PublishAot>true</PublishAot>
<Optimize>true</Optimize>
<OptimizationPreference>Speed</OptimizationPreference>
<IlcPgoOptimize>false</IlcPgoOptimize>
```

`Speed` keeps the Native AOT speed-oriented compiler path. Framework MIBC PGO is disabled
to avoid its large contribution to executable size. Public RID artifacts must not set
`IlcInstructionSet=native` or an AVX2-only baseline because the CI runner CPU is not the
minimum CPU supported by that RID.

CPU-heavy routines may instead use runtime multi-version dispatch:

```text
x64 with AVX2 → AVX2 implementation
ARM64         → AdvSimd implementation
older x64     → SSE2 implementation
other CPUs    → scalar implementation
```

Every accelerated routine must retain and test the scalar fallback. SIMD is appropriate
for measured byte, hash, compression, image, or patch loops; it cannot improve network,
disk, process startup, or IPC waiting.

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
