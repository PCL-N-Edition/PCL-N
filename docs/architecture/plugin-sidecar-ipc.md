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

The first window does **not** wait for the sidecar process, handshake, `runtime.init`,
`ui.manifest`, or any `ui.getPage` body. Optional-runtime warmup begins in parallel with
shell initialization and injects plugin navigation when ready.

```text
host shell ready
  → first window opens and splash closes
  → sidecar handshake/runtime.init continues in the background
  → fetch manifest once
  → register navigation
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

**4** (multiplexed transport + data-chain). Legacy `catalog.*` RPCs remain for
file-drop install. `system.hello` is deliberately exchanged with the v3 frame:

- a v4 host advertises `minimumProtocolVersion=3` and `maximumProtocolVersion=4`;
- a v4 sidecar selects the highest common version;
- new host + old sidecar and old host + new sidecar both remain on v3;
- only peers that select v4 switch the established connection to v4 framing.

## Transport and performance

The compatibility handshake still provides the protocol v3 transport:

- one persistent connection for the process lifetime;
- Windows named pipe and Unix domain socket transports;
- a four-byte big-endian payload length followed by UTF-8 JSON;
- source-generated `System.Text.Json` metadata, compatible with Native AOT;
- a single writer/read owner, so frames cannot interleave;
- no `WriteThrough` option on the control channel.

The primary optimization rule is to reduce calls and payload copies before tuning CPU
instructions. Page roots are low-frequency control messages, so replacing the current
framing with `System.IO.Pipelines` alone would not fix observed startup latency.

### Protocol v4 transport

After negotiation, both processes wrap the existing stream in `PipeReader` / `PipeWriter`
and use one dedicated read loop plus one dedicated write loop. The fixed big-endian header
is 20 bytes:

```text
payloadLength:u32 | protocolVersion:u16 | messageType:u16 |
flags:u32 | requestId:u64
```

Low-frequency request and response payloads remain source-generated JSON. Request IDs are
not repeated inside JSON. Progress is a compact binary frame and is coalesced to at most
one snapshot per 33 ms per active action.

| Concern | v4 behavior |
|---------|-------------|
| Concurrent long actions | responses are routed through a pending-request map by `requestId` |
| Cancellation | caller cancellation sends a `Cancel` frame; cancelling one call does not poison the connection |
| Write integrity | every frame passes through a single bounded writer channel |
| Backpressure | host and sidecar accept at most 128 pending calls; writer queues hold 256 frames |
| Overload | sidecar returns a structured `429` response instead of growing memory without a bound |
| Progress overload | stale progress may be dropped; requests, final results and errors wait for capacity |
| Inline payload limit | 1 MiB, rejected from the header before payload allocation |
| Existing large data | `.pnp` packages and other disk-backed objects are passed by path |
| Windows pipe access | the server uses `CurrentUserOnly` with 64 KiB input/output buffers |
| Unix access | the domain socket is restricted to user read/write permissions |

Shared memory is intentionally not created yet: no current RPC needs to copy a payload over
1 MiB, because package operations already pass a path. Add a shared-memory data plane only
when a measured non-file payload crosses that boundary.

Regression coverage:

- host transport tests force a later response to complete before an earlier simulated slow
  call, proving that the v3 head-of-line lock is gone;
- host cancellation is followed by `health.ping`, proving the stream remains synchronized;
- sidecar tests launch a separate process and cover v3 fallback, v4 multiplexing, cancel
  frames and malformed oversized frame rejection;
- the frame writer reuses an `ArrayBufferWriter<byte>` per write loop and JSON metadata is
  generated at build time, avoiding the former per-frame header array and reflection path.

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
