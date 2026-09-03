# XSR-706 Launch orchestration and product UI state

## Outcome

Wave 7 launches Minecraft through one product-level command. The Desktop launch page identifies
an instance and an account; a Services composition coordinator owns every operation required to
turn those identifiers into the low-level, executable `MinecraftLaunchRequest`. Product UI facts
belong to Desktop while remaining cells in the shared host state store. Asynchronous discovery
publishes state only and never mutates the render-thread-owned UI.Next tree.

This unit supersedes XSR-705's temporary statement that `LaunchPageController` reads a version
JSON, derives an offline identity, or dispatches `minecraft.launch` directly.

## Locked launch boundary

The public product path is:

```text
PXML/UI.Next intent
        |
        v
MinecraftStartCommand(instanceId, accountIndex)
        |
        v
MinecraftLaunchCoordinator
  |- resolve the discovered instance
  |- resolve and validate the complete inheritance chain
  |- detect operating system, version, and architecture
  |- resolve the full account profile without publishing credentials
  |- resolve the effective manifest Java contract and loader constraints
  |- select a compatible installed Java, or acquire the required runtime
  |- apply persisted global and per-instance launch settings
  `- construct the low-level MinecraftLaunchRequest
        |
        v
MinecraftLaunchExecutor -> native preparation -> process start
```

`MinecraftLaunchRequest` and `MinecraftRouteIds.Launch` remain the Wave 6 core contract for
focused planner/executor tests and internal composition. Desktop must not construct that request,
read manifest files, infer inheritance, choose a Java executable, or supply platform defaults.
The production composition root registers `minecraft.start` only after all coordinator
dependencies are available. Missing or unsupported account/runtime/input data returns a stable
XSR error; it never falls back to `java`, Java 17, `Unknown` OS, or an incomplete manifest list.

The manifest's effective `javaVersion.majorVersion` remains authoritative. Historical fallback
is used only when the effective inheritance chain supplies no Java contract, after which loader
constraints are intersected. Selection uses the resulting range; acquisition is attempted only
for a concrete required major when no compatible installed runtime exists.

## Product state ownership and threading

The following cells are a Desktop launch-page projection, not Minecraft service truth:

- `launch.profile.name`
- `launch.profile.summary`
- `launch.instance.summary`
- `launch.instance.detail`
- `launch.selected.instance`
- `launch.action.label`
- `launch.status`

Desktop declares them through an explicit host-state declaration callback passed to the
Foundation composition root before the shared store is built. `PCL.Services` contains no owner,
type, or state block named after a Desktop page.

Instance discovery is generation-based and cancellable. Starting a refresh cancels the previous
generation; only the newest generation may publish its result. The worker may complete on any
thread and may only publish host state. The state bridge schedules the resulting PXML binding
updates on the render thread. No continuation calls `XsrUiTree.GetComponent`, changes a component,
marks the tree dirty, or otherwise reads render-owned presentation state.

## PXML identity and accessibility

PXML separates machine identity from human semantics:

```xml
<Button
    Key="LaunchButton"
    Content="{state launch.action.label}"
    Label="{state launch.action.label}"
    Command="ui.launch.primary" />
```

- `Key` is an optional, document-unique internal entity key used by composition and tests. It is
  never projected into the render scene or accessibility tree.
- `Label` is the accessibility name. A state binding is resolved during scene projection so a
  dynamic action announces the same current text that it displays.
- `Content` is visible presentation text.

The loader preserves `Key` as the UI.Next entity name, rejects duplicate keys at compile time,
and continues to project only semantic `Label`/visible text to the Avalonia accessibility edge.
Internal names such as `LaunchButton`, `VersionName`, and `CardAccount` must never become
automation names.

## Button behavior

The primary action is `ui.launch.primary`. With a selected instance it dispatches
`minecraft.start`; without one it routes to the install/download destination and does not emit a
false launch failure. `ui.launch.instances` is an explicit route to the same version-management
destination until that vertical slice supplies a selector. The `launch.action.label` cell is
`启动游戏` when an instance is selected and `下载游戏` otherwise, and drives both visible and
accessible text.

## Acceptance

- Desktop source contains no construction of `MinecraftLaunchRequest` and no version JSON I/O.
- Production start composition supplies a concrete OS, architecture, inheritance chain, account,
  compatible Java executable/major, and launch settings to the low-level request.
- A child manifest receives all parent manifests in deterministic nearest-parent-to-root order;
  unsafe references and cycles fail before launch.
- Two overlapping scans cannot let an older completion replace the latest selected instance.
- Initial discovery can complete on a worker thread without touching UI.Next tree components.
- No-instance primary activation and the instance-list action both route to version management.
- PXML keys are unique internal handles; scene and Avalonia accessibility names are human labels,
  including the dynamic primary action.
- Desktop, Services, PXML, and UI.Next tests pass under their CoreCLR and applicable NativeAOT
  gates; architecture, formatting, Desktop NativeAOT `--validate-shell`, and trimmed Desktop
  `--validate-shell` all pass.
