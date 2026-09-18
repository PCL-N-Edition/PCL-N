# XSR-721 Install entry and Java catalog

## Locked contract

Wave 7 exposes `安装` as a first-class Desktop navigation destination. Its root is only two
edge-to-edge choice cards: `Java` and `Bedrock`. The cards have no hero title, subtitle, icon, or
secondary call to action. Their upper areas are reserved for future gradient artwork, while the
only visible text is the centered name at each card's lower edge. The choices remain whole-card,
keyboard-accessible buttons with specific semantic labels; their internal PXML `Key` values are
stable implementation handles and are never used as spoken labels.

Choosing Java pushes a Java-install subpage through the existing UI.Next navigator. The page has
no hero title or subtitle: it starts with one version input and an adjacent capsule-shaped
`开始安装` action. Directly below is a compact, horizontally scrollable selector strip and one
embedded `HorizontalPager`; these are lower content within the Java page, not further global
navigation destinations. Its ordered slices are `Minecraft`, `Forge`, `Cleanroom`, `NeoForge`,
`Fabric`, `Legacy Fabric`, `Fabric API`, `Quilt`, `QSL`, `LabyMod`, `OptiFine`, and `LiteLoader`.
The pager supports direct horizontal drag and Left/Right keyboard paging through the canonical
renderer. Wheel input must not advance pages horizontally. The visible page selectors remain
ordinary buttons so the same choices are available without a drag gesture.

Each base loader slice contains its own explicit selection action. `Fabric API` is an additive
slice that is visible only while `Fabric` is selected; `QSL` is likewise visible only while
`Quilt` is selected. Changing the selected base loader removes incompatible dependent slices and
their presentation selection. The Desktop controller owns this transient visibility and selection
projection; the PXML document deliberately carries no Service state binding for it.

Choosing Bedrock pushes a dedicated, transparent pending-migration subpage.  It must state that
the Bedrock installer has not moved to XSR yet and must not start a download, present an invented
progress state, or claim that the old implementation is active.  The normal title-bar return path
is sufficient wayfinding back to the installation choices.

This unit is deliberately an installation *entry and catalog* slice, not a new installer
boundary.  `Nexa.Desktop` owns the PXML documents, page navigation, transient text input,
presentation selection, and the truthful feedback shown when an unavailable action is requested.
It does not add a Service installer route, call an Avalonia control directly, or make a Service
depend on Desktop, UI.Next, PXML, or renderer types.  Existing Minecraft library/runtime services
remain the future source of actual version, loader, acquisition, and progress contracts.

## Visual and motion contract

The install root uses solid, generously rounded card surfaces with reserved artwork regions, not
Liquid Glass. Press feedback begins immediately; the Java and Bedrock destinations use the
existing reversible navigator path instead of an unrelated fade. The horizontal catalog follows
the renderer's direct-manipulation contract: its content tracks a drag continuously and resolves
through the existing interruptible pager motion. Default settling is restrained and non-bouncy;
reduced-motion preferences retain clear selection feedback without spatial travel.

The `开始安装` affordance is a compact capsule beside the input rather than a full-width footer,
keeping the chosen version and its action visibly coupled.  Selecting a Minecraft version or
loader only changes transient presentation until a real installer contract exists.

## Acceptance

- The main navigation opens the title-free, two-card install entry instead of a generic placeholder.
- `Java 版` opens the Java catalog; `Bedrock 版` opens the explicit pending-migration page.
- The Java page exposes the labeled version input, right-aligned capsule action, compact
  selector strip, and embedded twelve-slice horizontal pager. It never uses wheel scrolling to
  change pager pages.
- The slices are ordered Minecraft, Forge, Cleanroom, NeoForge, Fabric, Legacy Fabric, Fabric
  API, Quilt, QSL, LabyMod, OptiFine, and LiteLoader; Fabric API and QSL hide unless their
  selected parent loaders are compatible.
- All click targets have meaningful Chinese semantic labels.  Internal keys such as
  `InstallJavaChoice` and `JavaInstallStart` are not user-facing accessibility names.
- Pager selection and subpage navigation stay inside UI.Next intents/navigation; no Avalonia
  control or Service route is used as a shortcut.
- The unavailable Java start action gives truthful in-window feedback and Bedrock never simulates
  an installation.
- PXML compilation, Desktop navigation tests, UI.Next pager/input tests, architecture checks,
  format, trimmed Desktop validation, and the NativeAOT Desktop shell smoke pass.

## Download page visual revision

The download-page redesign supersedes the earlier horizontal selector-strip placement.
Java component selectors now form a 152 px vertical, scrollable rail beside the existing
HorizontalPager. The version input and install action stay together in a bottom action row.
The content viewport receives the remaining width and height, with inset spacing for readable
catalog rows. Whole-card edition choices use pale neutral/tinted surfaces and dark titles;
accent color identifies Java versus Bedrock without filling the complete window with it.
Existing keys, commands, pager ordering, conditional addon visibility and installation truth
remain unchanged. All motion is still renderer-owned; no additional animation clock or service
boundary is introduced. Validate Desktop/PXML contracts and architecture, plus a trimmed build.

## User-directed compact layout

Supersedes the visual revision above: input and install action return to the top.
Component tabs are horizontal text labels with a two-pixel active underline, followed by a
one-pixel divider. The Minecraft catalog is a plain list with a leading selected checkmark.
Version values remain the existing catalog values, not illustrative mockup version numbers.

## Home-aligned install selection

The horizontal selectors now reuse the home profile-action capsule palette, corner radii and
renderer press/hover behavior instead of underlines. The top version input and install action
remain unchanged. The legacy install view was inspected read-only for grouped version lists and
explicit selection; no legacy source or control was copied. Minecraft choices use fixed-width
check icons and a grouped surface; selector labels and version labels retain semantic names.
Edition cards reduce outer padding from 28 to 12 px, inner padding from 28 to 16 px, and their
gap from 20 to 12 px. This supersedes previous spacing and underline requirements only.

## Base-loader reachability regression

Selecting a loader is selection, never catalog filtering. All ten base slices and their selector
buttons remain available for every loader. Only Fabric API and QSL depend on the selected loader,
so the pager has 10 pages normally and 11 for Fabric or Quilt. Acceptance covers every base
selection, direct Fabric-to-Forge without a vanilla hop, and clearing the incompatible addon.
