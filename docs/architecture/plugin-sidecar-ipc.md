# Plugin sidecar + UI data-chain injection

## Goal

| Process | Role |
|---------|------|
| **PCL.Desktop** (AOT OK) | Shell + **generic** UI renderer; no plugin business pages in host source |
| **PCL.Plugin.Sidecar** (CoreCLR) | Full plugin platform + **UI data-chain** (manifest / page tree / actions) |

Plugin pages are **not** hardcoded into the host. Sidecar pushes page metadata and node trees; host injects settings nav and renders remotely.

## Data chain

```
runtime.init → LoadEnabled
ui.manifest  → groups[] + pages[]     → host SettingsPageGroups/Pages.Add*
ui.getPage   → root node tree         → PageSetupRemoteDataChain
ui.invokeAction → result              → refresh / pick file / openUrl / toast
```

### Node kinds

`card` | `stack` | `list` | `row` | `text` | `muted` | `hint` | `button` | `checkbox`

### Protocol version

**3** (data-chain). Legacy catalog.* RPCs remain for file-drop install.

## Host types

- `PageSetupRemoteDataChain` — only generic renderer
- `PluginSidecarUiInjector` — registers remote pages after sidecar start
- `PluginSidecarPnpFileArtifactHandler` — `.pnp` drop → catalog.installPnp

## Expanding to full original plugin system

Add more pages/actions in `PCL.Plugin.Sidecar/Ui/UiDataChain.cs` (and future providers):

| Original page | Status |
|---------------|--------|
| 已安装 | ✅ data-chain |
| 安全 | ✅ data-chain |
| 开发者 | ✅ data-chain |
| 平台状态 | ✅ data-chain |
| 市场 / 账户 / 云同步 / UI Patch / 兼容性 | extend providers + actions |

No host UI code required for new pages — only sidecar data.

## Packaging

```powershell
.\scripts\build-desktop.ps1 -WithPlugin -SkipPluginFetch
# host bin/.../sidecar/ + inject on launch
```

CI `include_plugin: true` stages `sidecar/` next to host without compiling plugin into Desktop.
