# Plugin source-overlay inject

## Product rules

1. **Default host body is plugin-free.** Public packages do not compile or embed the private plugin product.
2. **Plugin release product = git tag source**, not DLLs. A publishable tag must contain `host-overlay/` (manifest + MSBuild targets + rewrite).
3. **Inject = checkout tag source → apply rewrites → compile.**  
   `scripts/apply-plugin-overlay.ps1` then Desktop `-p:PclWithPlugin=true`.
4. **No `LoadFromStream` / embedded `PCL.Plugin.dll`.**

## Plugin release flow

```
main 合入
  → bump PCL.Plugin.csproj Version
  → push main
  → annotated tag vX.Y.Z + push tag
  → GitHub Release（changelog / releases/latest 发现用）
  → release.yml：校验 host-overlay + build/test + 宿主 source-overlay 冒烟编译
```

DLL 上传**不是**注入前置条件。GitHub Release 主要用于：

- 给 `apply-plugin-overlay -Channel Latest`（默认）解析 `releases/latest` 的 **tag 名**
- 给人看的更新说明

## Pipeline

```
scripts/apply-plugin-overlay.ps1 [-Tag vX.Y.Z] [-Channel Latest|Stable]
  ├─ resolve source tag (default Latest = GitHub releases/latest, else newest v* git tag)
  ├─ clone/checkout PCL.Plugin/ at that **source** ref
  ├─ require host-overlay/manifest.json + msbuild targets
  ├─ copy host-overlay/rewrite/** → host worktree (dirtines tracked files)
  └─ write .pcl-plugin-overlay.state.json

dotnet build|publish PCL.Desktop -p:PclWithPlugin=true
  └─ Import PCL.Plugin/host-overlay/msbuild/PclPlugin.overlay.targets
       ├─ Compile plugin **/*.cs + AvaloniaXaml **/*.axaml
       ├─ PackageReference Harmony / BouncyCastle / …
       ├─ ProjectReference PCL-N-Plugin-SDK
       └─ PclIncludesPlugin=true (CoreCLR; AOT off)

# optional: clean host rewrite dirt
scripts/apply-plugin-overlay.ps1 -RestoreHostRewrites -SkipFetch
```

Host hooks that stay in the body (empty without overlay):

| File | Role |
|------|------|
| `PCL.Desktop/Hosting/DesktopHost.cs` | Calls `RegisterOptionalModules` / `InitializeOptionalRuntime` partials |
| `PCL.Desktop/Hosting/DesktopHost.Optional.cs` | Host-only no-op partials |
| Overlay rewrite of `DesktopHost.Optional.cs` | Registers `PclPluginHostModule` + `PluginPlatformBootstrap` |

**Do not commit** rewritten host files after overlay; restore or leave dirty only for local WithPlugin builds.

## Local commands

```powershell
# Latest formal release tag (GitHub Release → tag source)
.\scripts\apply-plugin-overlay.ps1 -Channel Latest

# Newest v* git tag (may be newer than a formal Release)
.\scripts\apply-plugin-overlay.ps1 -Channel Latest

# Pin
.\scripts\apply-plugin-overlay.ps1 -Tag v0.17.0

.\scripts\build-desktop.ps1 -WithPlugin -SkipPluginFetch
.\scripts\apply-plugin-overlay.ps1 -RestoreHostRewrites -SkipFetch
```

Requires [PCL-N-Plugin-SDK](https://github.com/PCL-N-Edition/PCL-N-Plugin-SDK) under the host repo or as a sibling folder.

## CI (host)

`reusable-build.yml` optional:

- `include_plugin: true` — run overlay + `-p:PclWithPlugin=true`
- `plugin_tag` — pin; empty uses **Latest** channel (`releases/latest`)
- secret `PCL_PLUGIN_TOKEN` when needed (private PCL.Plugin read PAT)
