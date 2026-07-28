# Plugin source-overlay inject

## Product rules

1. **Default host body is plugin-free.** Public packages do not compile or embed the private plugin product.
2. **Inject = source overlay, not IL embed.** Pull the latest (or pinned) `PCL.Plugin` **tag source**, apply host rewrites, then compile Desktop with `-p:PclWithPlugin=true`.
3. **No `LoadFromStream` / embedded `PCL.Plugin.dll`.** The plugin types compile into the Desktop host assembly (or ship only when that flag is set).

## Pipeline

```
scripts/apply-plugin-overlay.ps1 [-Tag vX.Y.Z]
  ├─ resolve latest tag (gh) unless -Tag / -SkipFetch
  ├─ clone or checkout PCL.Plugin/ at that ref
  └─ copy host-overlay/rewrite/** → host repo root

dotnet build|publish PCL.Desktop -p:PclWithPlugin=true
  └─ Import PCL.Plugin/host-overlay/msbuild/PclPlugin.overlay.targets
       ├─ Compile plugin **/*.cs + AvaloniaXaml **/*.axaml
       ├─ PackageReference Harmony / BouncyCastle / …
       ├─ ProjectReference PCL-N-Plugin-SDK contracts
       └─ PclIncludesPlugin=true (CoreCLR; AOT off)
```

Host hooks that stay in the body (empty without overlay):

| File | Role |
|------|------|
| `PCL.Desktop/Hosting/DesktopHost.cs` | Calls `RegisterOptionalModules` / `InitializeOptionalRuntime` partials |
| `PCL.Desktop/Hosting/DesktopHost.Optional.cs` | Host-only no-op partials |
| Overlay rewrite of `DesktopHost.Optional.cs` | Registers `PclPluginHostModule` + `PluginPlatformBootstrap` |

## Local commands

```powershell
# Fetch latest plugin tag, rewrite host hooks, build with plugin
.\scripts\build-desktop.ps1 -WithPlugin

# Pin a tag
.\scripts\apply-plugin-overlay.ps1 -Tag v0.16.0
.\scripts\build-desktop.ps1 -WithPlugin -SkipPluginFetch

# Run UI
.\scripts\run-plugin-ui.ps1 -SkipFetch   # reuse existing PCL.Plugin/
```

Requires a checkout of [PCL-N-Plugin-SDK](https://github.com/PCL-N-Edition/PCL-N-Plugin-SDK) either as `PCL-N-Plugin-SDK/` under the host repo or as a sibling of the host repo folder.

## UI

Plugin settings pages live under `PCL.Plugin/Ui/Settings/`:

- Prefer **AXAML** shells (same pattern as host `PageSetup*.axaml`).
- List enter motion uses host `ControlVisualHelpers.AnimateListEntrance` / `MotionTokens` so plugin pages match launcher timing.

## CI

`reusable-build.yml` optional inputs:

- `include_plugin: true` — checkout SDK + run overlay + `-p:PclWithPlugin=true`
- `plugin_tag` — pin; empty resolves latest via `gh`
- secret `PLUGIN_REPO_TOKEN` — private plugin/SDK clone when needed

Host-only publish paths leave `include_plugin` false.
