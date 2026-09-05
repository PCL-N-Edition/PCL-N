# XSR-705 Wave 7 launch page

## Outcome

The first product vertical slice attaches to the shell: the launch page. Renderer navigation
intents now drive real page routing inside the content host, the page reads its summaries and
action through `launch.*` host state cells, and the start intent dispatches the real
`minecraft.launch` command through the composed runtime routers with the discovered instance
and the account identity.

## Page and routing

- `PCL.Desktop/Ui/LaunchPage.pxml` is an embedded PXML page (account summary, instance
  summary, launch button) compiled at startup through the same parser/compiler/
  loader pipeline as the shell. Its dynamic texts bind to host state cells
  (`launch.profile.summary`, `launch.instance.summary`, `launch.action.label`), declared by
  `LaunchPageStateComposition` in the Minecraft launch family's state block; empty cells carry
  no implicit default — the controller publishes the initial facts when it attaches.
- `LaunchPageController` (Desktop composition root, internal, covered through
  `InternalsVisibleTo`) subscribes to the renderer intent sink. `ui.navigation.launch` shows
  the launch page, every other `ui.navigation.*` destination shows a placeholder page, and
  `ui.launch.start` starts the launch flow. Destination switches use
  `XsrUiNavigator.Replace` — the navigator's back stack stays reserved for hierarchical
  drill-in, never for moving between primary destinations.
- Program resolves the Minecraft root (for now `%APPDATA%/.minecraft`; settings plumbing is a
  later slice), composes the controller after the shell, and attaches it.

## Layout parity

The legacy checkout's `PCL.Desktop/Features/Launching/Views/PageLaunchHomeExperimental.axaml`
is the normative layout reference for the Experimental launch page. The migrated page keeps its
exact idle-page structure instead of flattening the three cards into one horizontal row:

- the content inset is the shell-owned `28,24` padding;
- the page body is `0.92*`, `16 px`, `1.35*`, with the account column constrained to
  `240..360 px` and the right column to at least `280 px`;
- the account card stretches for the full page height, uses `20,18` padding, keeps the badge at
  the far edge of its header, and pins the account summary to the card bottom;
- the right column contains an intrinsic-height version card, a `12 px` gap, and a community card
  which consumes all remaining height;
- the version card uses `16,14` padding, a trailing 28 px instance-list affordance, the
  `12,10` single-line picker row with a trailing 26 px chevron affordance, and the 44 px launch
  button; the community card uses `18,14,32,14` padding.

PXML expresses those facts through backend-neutral stack weight, minimum/maximum size, and
alignment properties. UI.Next resolves the weighted slots deterministically; the Avalonia backend
continues to consume only the committed scene and must not recreate the legacy Grid.

## Launch flow (superseded by XSR-706)

The temporary XSR-705 implementation below is historical evidence only. XSR-706 replaces this
boundary with the product-level `minecraft.start` coordinator; Desktop no longer performs any of
these low-level steps.

Attach publishes the account summary from the `accounts.profiles` roster (first profile, or
"未选择账户"), requests the instance query, and publishes the first discovered instance as the
instance summary. The start intent requires a selected instance: without one the status cell
reports "未找到可启动的实例" and nothing dispatches. With one, the controller loads
`<instance>/<versionId>.json`, resolves the offline identity (roster profile first, otherwise
the vanilla offline v3 UUID derivation over "OfflinePlayer:" + name), dispatches
`MinecraftRouteIds.Launch`, and publishes "正在启动…", "已启动", or the stable error message
into the status cell. The renderer only ever reads these cells through the state bridge.

## Verification and tests

New executable suite `tests/PCL.Desktop.Tests` (registered in the solution, the architecture
gate's project graph, and CI as "Test desktop composition"): the launch page composes with its
bound summaries and action, its wide, default, and minimum-window scenes preserve the locked
column/card geometry without overlap, navigation intents route between the launch and placeholder
pages, and a start intent without instances routes to installation without dispatching. XSR-714
supersedes the temporary page-local status footer with the shared notification surface. The full
launch dispatch chain (planner → executor → process port) remains covered by the
`PCL.Services.Tests` corpus tests; a full end-to-end launch with a real version JSON on disk
is deferred to the launch settings slice, which also owns the Minecraft root setting.

## Deliberate scope

- No instance selection yet: the slice launches the first discovered instance; a selector is
  the next page unit.
- The Minecraft root directory is not yet a setting; the placeholder pages carry no content.
- Online accounts (Microsoft/ThirdParty identity modes) keep their roster summary but launch
  offline in this slice.
