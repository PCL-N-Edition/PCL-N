# Native AOT desktop packaging (Option D)

## Goal

Ship **all** multi-platform release variants as **Native AOT** so the launcher
**starts without single-file self-extraction**.

## Why not a one-line CI flip

| Blocker | Detail |
|--------|--------|
| Embedded IL plugins | `EmbeddedRuntimeExtensionLoader` uses `AssemblyLoadContext.LoadFromStream` + `GetTypes` / `Activator` |
| CSProj policy | `PclPluginAssembly` forces `PublishAot=false` / `PublishTrimmed=false` |
| CI policy | `reusable-build.yml` forces single-file JIT + native self-extract |
| Third-party `.pnp` | Collectible ALC + Harmony + runtime AXAML are incompatible with in-process AOT |
| Private boundary | First-party `PCL.Plugin` is a private DLL release, not a solution project (and `Plugin → Desktop` would cycle if linked naively) |

Measured: **NoPlugin** `PCL.Desktop` **does** Native-AOT publish on `win-x64` after isolating the analyzer project from host AOT flags. Output is a native host **plus** sibling native deps (Skia / VLC / …) — no bundle extract step.

## Target architecture

### First-party platform (`PCL.Plugin`)

**Static native link** into the AOT host:

1. Break Desktop ↔ Plugin project cycle (split Plugin.Core vs Plugin.UI, host only on abstractions).
2. Replace reflection discovery with **static registration**:
   - `builder.AddModule(new PclPluginHostModule())`
   - `new PluginPlatformBootstrap()` (or source-generated list)
3. CI consumes **plugin sources or an AOT-ready package**, not `LoadFromStream` of release DLLs.
4. Drop embedded resource DLL model for first-party code.

### Third-party `.pnp` plugins

In-process IL load is **out** under AOT. Product line:

| Phase | Model | Notes |
|-------|--------|------|
| D.1 | **Disabled on AOT SKU** | Marketplace / `.pnp` unavailable or install-only for CoreCLR SKU |
| D.2 | **Out-of-process host** | AOT shell + CoreCLR/native sidecar over IPC (settings/UI as data) |
| D.3 (optional) | Native ABI plugins | Long-term ecosystem rewrite |

Harmony stays **out** of the AOT process.

## Migration phases

| Phase | Deliverable | Exit criteria |
|-------|-------------|----------------|
| **0** | NoPlugin Desktop AOT publish works on primary RIDs | CI smoke publish + run `--validate-environment` |
| **1** | Static first-party plugin registration + cycle break | AOT **WithPlugin** binary boots with platform modules |
| **2** | Third-party policy (D.1 then D.2) | Documented SKU matrix; no in-process IL load |
| **3** | CI all RIDs / SelfContained AOT | Release artifacts no longer use `PublishSingleFile` extract path |
| **4** | Remove dual path | Delete embed MSBuild + LoadFromStream loader |

## Publish shape (AOT)

- `PublishAot=true`, `SelfContained=true`
- **Not** `PublishSingleFile` (native deps remain beside the host; still **direct** start)
- `DebugType=None` / strip symbols in release jobs
- macOS continues to wrap the host in `PCL N.app`

## Non-goals (initial)

- Framework-dependent (`NoRuntime`) Native AOT as primary SKU
- Keeping Harmony / runtime AXAML in-process on AOT
- Loading arbitrary community IL plugins inside the AOT process
