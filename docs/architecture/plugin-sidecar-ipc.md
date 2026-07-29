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

### Startup performance contract

The first window may wait for sidecar process startup, handshake, `runtime.init`, and one
`ui.manifest` request. It **must not** wait for `ui.getPage` bodies.

```text
sidecar ready
  → fetch manifest once
  → register navigation
  → host is ready and first window opens
  → fetch a page body only when that page is opened
  → cache the returned root for the rest of the session
```

Remote pages use the host's generic `MyLoading` animation while the first body request is
running. Do not reintroduce splash-time page preloading: online account and marketplace
pages can each contain network work, and protocol v3 serializes calls on one connection.
Preloading those bodies previously added about eight seconds to the measured first-window
path and monopolized every other sidecar request.

### Node kinds

`card` | `stack` | `list` | `row` | `toolbar` | `text` | `muted` | `hint` | `button` | `checkbox` | `textbox` | `select` | `settingsGroup` | `settingsCell`

Host rendering follows classic setup pages (`MyCard` / `MyTextBox` / `MyButton`), not experimental UI.
`settingsGroup` / `settingsCell` approximate iOS Settings grouped lists (inset rounded sections + trailing switches).

- `textbox`: optional `id`, `placeholder`, initial `text`
- `select`: `options[{value,label}]`, `selected`, optional `actionId` on change
- `button.valueField` / `metaField`: host sends field text as `params.value` / `params.pluginId`

### Progress frames

Long actions (e.g. `market.installRemote`) may emit intermediate responses:

```json
{ "id": "…", "progress": { "stage": "下载", "detail": "…", "progress": 0.4 } }
```

Final frame has `result` or `error`. Host maps progress into the task manager.

### Action result flags

| Field | Host behavior |
|-------|----------------|
| `refreshPage` | Re-fetch `ui.getPage` |
| `refreshNavigation` | Re-run `ui.manifest` inject (e.g. developer show Safety) |
| `root` | Replace page body with inline tree (local market scan) |
| `pickFilePatterns` / `pickFolder` | Host picker → re-invoke with path |
| `openUrl` | System browser |
| `hostBooleanKey` / `hostBooleanValue` | Write host launcher bool (e.g. `SystemDebugMode`) |

### Protocol version

**3** (data-chain). Legacy catalog.* RPCs remain for file-drop install.

## Transport and performance

Protocol v3 already provides:

- one persistent connection for the process lifetime;
- Windows named pipe and Unix domain socket transports;
- a four-byte big-endian payload length followed by UTF-8 JSON;
- source-generated `System.Text.Json` metadata, compatible with Native AOT;
- a single writer/read owner, so frames cannot interleave;
- no `WriteThrough` option on the control channel.

The primary optimization rule is to reduce calls and payload copies before tuning CPU
instructions. Page roots are low-frequency control messages, so replacing the current
framing with `System.IO.Pipelines` alone would not fix observed startup latency.

### Protocol v4 boundary

The following changes require a coordinated host **and** `PCL.Plugin.Sidecar` protocol
upgrade. They must not be introduced unilaterally while protocol v3 packages are supported.

| Need | Protocol v4 direction |
|------|-----------------------|
| Concurrent long actions | dedicated read loop, bounded write queue, request-id multiplexing and cancel frames |
| High-frequency progress/logs | coalesced snapshots or batches every 16–50 ms |
| Stable compact event payloads | fixed binary event headers; keep source-generated JSON for low-frequency control |
| Payloads above 1 MiB | pass a file path/handle when data already exists on disk; otherwise evaluate shared memory |
| Backpressure | bounded channels; requests/errors never dropped, stale progress may be replaced |

Any v4 work must include cross-process compatibility tests, malformed-frame limits,
cancellation/desynchronization tests, and benchmarks proving that the extra complexity
reduces allocation or latency.

## Host types

- `PageSetupRemoteDataChain` — only generic renderer
- `PluginSidecarUiInjector` — registers remote pages after sidecar start
- `PluginSidecarPnpFileArtifactHandler` — `.pnp` drop → catalog.installPnp

## Original plugin surfaces (data-chain)

| Original page | Status |
|---------------|--------|
| 平台状态 | ✅ |
| 已安装 | ✅ install/enable/disable/uninstall/rollback |
| 市场 | ✅ online search/list/get/update + local (dev) + web |
| 安全 | ✅ Safe Mode + isolation ID textbox |
| 开发者 | ✅ order verify textbox + `refreshNavigation` |
| UI Patch | ✅ apply + conflict resolve |
| 兼容性 | ✅ offline records |
| 账户 | ✅ OnlineAccountService pairing / logout |
| 云同步 | ✅ section toggles via PluginOnlineRuntime |
| 数据与隐私 | ✅ permission grant/revoke |

No host UI code required for new pages — only sidecar data.

## Packaging

Release host is a **single-file** binary. The CoreCLR sidecar is packaged as an **opaque zip** and embedded:

```text
PCL.Desktop.Embedded.PluginSidecar.zip
  → extracted to {data}/runtime/sidecar/{hash}/PCL.Plugin.Sidecar(.exe)
```

```powershell
# Local: pack zip then build/publish host with embed
.\scripts\pack-plugin-sidecar-zip.ps1 -Runtime win-x64 -SkipFetch -OutputZip artifacts\sidecar.zip
dotnet publish PCL.Desktop -c Release -r win-x64 -p:PclPluginSidecarZipPath=artifacts\sidecar.zip ...
```

Dev multi-file layout still works: `{app}/sidecar/` or repo `PCL.Plugin.Sidecar/bin/...`.

CI (`reusable-build.yml`): `embed_plugin_sidecar: true` (default) packs the zip and passes `-p:PclPluginSidecarZipPath=...` so the public zip/tar stays **one host exe** while still shipping plugins.

### OOBE path restart

1. User sets data/cache on OOBE DataPaths → host writes `pcln-paths.json` and restarts with `--oobe-resume`.
2. Next process extracts embedded sidecar into the **new** data dir, connects plugin (splash), then OOBE **Welcome → Online → Finish**.
