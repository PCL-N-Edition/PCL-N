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
ui.invokeAction → result              → refresh / pick file|folder / openUrl / toast / refreshNavigation
```

### Node kinds

`card` | `stack` | `list` | `row` | `text` | `muted` | `hint` | `button` | `checkbox`

### Action result flags

| Field | Host behavior |
|-------|----------------|
| `refreshPage` | Re-fetch `ui.getPage` |
| `refreshNavigation` | Re-run `ui.manifest` inject (e.g. developer show Safety) |
| `root` | Replace page body with inline tree (local market scan) |
| `pickFilePatterns` / `pickFolder` | Host picker → re-invoke with path |
| `openUrl` | System browser |

### Protocol version

**3** (data-chain). Legacy catalog.* RPCs remain for file-drop install.

## Host types

- `PageSetupRemoteDataChain` — only generic renderer
- `PluginSidecarUiInjector` — registers remote pages after sidecar start
- `PluginSidecarPnpFileArtifactHandler` — `.pnp` drop → catalog.installPnp

## Original plugin surfaces (data-chain)

| Original page | Status |
|---------------|--------|
| 平台状态 | ✅ |
| 已安装 | ✅ install/enable/disable/uninstall/rollback |
| 市场 | ✅ local folder scan + web market + .pnp install |
| 安全 | ✅ gated by developer flags |
| 开发者 | ✅ + `refreshNavigation` reinject |
| UI Patch | ✅ apply + conflict resolve |
| 兼容性 | ✅ offline records |
| 账户 | ✅ OnlineAccountService pairing / logout |
| 云同步 | ✅ section toggles via PluginOnlineRuntime |
| 数据与隐私 | ✅ permission grant/revoke |

No host UI code required for new pages — only sidecar data.

## Packaging

```powershell
.\scripts\build-desktop.ps1 -WithPlugin -SkipPluginFetch
# host bin/.../sidecar/ + inject on launch
```

CI `include_plugin: true` stages `sidecar/` next to host without compiling plugin into Desktop.
