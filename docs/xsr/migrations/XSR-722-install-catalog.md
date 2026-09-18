# XSR-722 Remote install catalog and compatibility

Services owns game/loader catalog acquisition and pure compatibility policy. Desktop only sends
refresh/select-catalog commands and projects immutable published snapshots in FramePreparing.
No Avalonia/renderer references or legacy assembly references enter Services. HTTP providers
are injected, bounded, cancellable and use JsonDocument/XML (no reflection serialization).

The legacy metadata endpoints and formats are behavioral references: Mojang manifest v2,
Fabric/Legacy Fabric/Quilt per-game metadata, Forge/NeoForge Maven, Cleanroom GitHub releases,
LiteLoader JSON, OptiFine download listings and LabyMod manifests. No installer is claimed.
Known version gates apply before HTTP; returned per-game catalogs are authoritative for actual
support. Unsupported, empty, loading and failed are distinct. Selection follows the phase
contract below. Changing the game invalidates loader-version selections and in-flight results;
only current per-catalog requests publish. Fabric API/QSL merge per-game, per-loader source catalogs.

The top install action is a capsule with a right arrow. Component selection is a single grouped
track, with one selected thumb and transparent inactive segments, driven by existing renderer
selection/press transitions. No independent animation timer is introduced.

UI.Next adds XsrUiSegmentedTrack: a noninteractive thumb projects the selected child bounds,
clipped to the shared track. Existing transition offsets and the backend spring clock animate
retargets from the presented position. The thumb has no input/accessibility identity.

## Explicit selection-phase revision

Per the user's later direction, this supersedes XSR-721 base-tab reachability: before a game is
chosen only Minecraft is visible. Pure compatibility gates expand candidate loaders immediately.
A background prefetch command
queues provider work on the thread pool with four concurrent catalog requests; parsing never runs
on the UI thread. Immutable aggregate snapshots preserve every sibling catalog result. Loading,
empty and failure presentation stays within each loader page. Browsing joins an in-flight request.
A selected loader removes incompatible choices; Forge/OptiFine and LiteLoader/OptiFine
compatibility follows the migrated rules. Fabric API/QSL expand for their required base.
Game changes invalidate old loader requests. Network errors are reported separately and retryable.
The grouped thumb supports pointer capture, direct drag, nearest-segment preview, release snap,
capture-loss recovery and keyboard/click selection. Visibility changes use the shared spring
clock, not delays or independent timers.

## List direct manipulation

Install catalog scroll viewports opt into vertical drag scrolling. Axis arbitration keeps horizontal
pager swipes available. An 8 px threshold cancels row activation once a vertical drag wins.
Recent pointer velocity projects an exponentially decelerating release (5/s), clipped to the
viewport extent. Capture loss cancels momentum; new press or wheel input stops it immediately.
The existing backend clock advances immutable scene motion facts; no renderer timer or service
reference is added. Reduced motion preserves direct dragging and suppresses inertial release.

## Retained list projection

Catalog snapshots stay immutable and cached. The Desktop projection realizes only visible rows
plus a small overscan window, with exact 50 px strides and spacer extents. Overlapping rows retain
entity identity; selection changes do not refetch metadata. XsrUiStableContent excludes retained
lists from navigation/entry fades. Re-selecting the chosen game clears it and all dependencies;
re-selecting a loader/addon build removes that choice and its dependent selections.

## Addon sources

Fabric API and QSL merge Modrinth version files with CurseForge paginated files (50/page,
10,000-file limit), game and loader filtered. CurseForge uses the legacy official API with
Nexa_CURSEFORGE_API_KEY / CURSEFORGE_API_KEY, falling back to MCIM without forwarding that key.
Project identities: [Fabric API](https://www.curseforge.com/minecraft/mc-mods/fabric-api) 306612,
[QSL](https://www.curseforge.com/minecraft/mc-mods/qsl) 634179. SHA-1, or filename when a hash is
missing, merges identical files while preserving both download URLs, source, size and integrity
facts. One failed source retains the other's usable results with a warning; two failures or an
empty survivor are errors, not a claim of incompatibility. Actual installer execution is separate.

Before game selection, the top input filters the Minecraft catalog without replacing the input
entity or moving its caret. Selecting a game switches it to the editable installation name;
clearing the game restores search mode. Search projection is recomputed only when text/catalog
changes, while visible row realization remains bounded independently of total catalog size.

## Validation (2026-09-07)

- Services: 225 tests; UI.Next: 84; Desktop: 59; Avalonia backend: 7 groups.
- Architecture: all 29 projects pass dependency and boundary checks.
- Windows x64 NativeAOT: Desktop suite passes all 59 tests; Services executable passes the four
  install-catalog provider/merge/prefetch/cancellation tests. Both publishes complete without warnings.
- A 10,000-version Desktop fixture keeps fewer than 30 rows/spacers alive at a 600 px viewport,
  preserves extent at a 250,000 px scroll offset, retains overlapping entity identity and input focus,
  and proves prefetch provider invocation uses a background thread while the UI remains interactive.
- Opt-in live provider smoke: Mojang and all nine base-loader endpoints return catalogs. Fabric API
  and QSL each return merged Modrinth/CurseForge download sources without partial-source warnings.
  Reproduce with `dotnet run --project tests/Nexa.Services.Tests -- --live-install-catalog`.

## Return-to-selection follow-up

Returning to any catalog locates its selected row in the complete, currently applicable catalog
before realizing the visible window. This applies to Minecraft, base loaders and addons, including
results arriving after navigation. It stops previous inertia and centers the selection without
replaying entry motion. Manual scrolling within the same page is not continuously overridden.

## OptiFine / OptiFabric compatibility follow-up

OptiFine's official download row is the source for its Forge compatibility field. `N/A` explicitly
rejects Forge; a missing/unparseable field is unknown. Dotted Forge versions match exactly; historic
`#build` entries match that build suffix. This is an allowlist of officially documented combinations,
not a claim that every unlisted combination can never run. See https://optifine.net/downloads.

OptiFabric (the official mod, CurseForge project 322385) is a Fabric-dependent slice, never a base
loader or a similarly named Modrinth modpack. Its per-game files are queried through the existing
CurseForge provider. Bounded JAR metadata reads verify the `optifabric` identity, Minecraft and Fabric
Loader dependency predicates from `fabric.mod.json`; nothing is executed or extracted to disk.
Selecting it exposes OptiFine alongside Fabric; removing Fabric or OptiFabric clears the dependent
OptiFine combination. Fabric API remains independent. Version lists reject incompatible selections
in both the UI projection and intent handler. OptiFabric requires a separate same-game OptiFine;
metadata eligibility does not certify every runtime patch or third-party mod combination.
Sources: https://www.curseforge.com/minecraft/mc-mods/optifabric and
https://github.com/Chocohead/OptiFabric/blob/llama/src/main/resources/fabric.mod.json.

### Cleanroom and OptiFine

Minecraft 1.12.2 keeps Cleanroom + OptiFine reachable. This is partial compatibility,
not a verified matrix: show an inline notice, including reported crashes in
0.6.9-alpha / 0.6.10. Do not silently install OptiRefine or claim an unverified fix.
Sources: https://cleanroommc.com/wiki/end-user-guide/preparing-your-modpack,
https://github.com/CleanroomMC/Cleanroom/issues/600,
https://github.com/Ecdcaeb/OptiRefine.

### Follow-up validation (2026-09-07)

The declaration now has 13 ordered slices: OptiFabric follows Fabric API and is conditional on
Fabric. CoreCLR passes 226 Services and 61 Desktop tests; architecture checks pass all 29 projects.
Windows x64 NativeAOT publishes without warnings and runs all 61 Desktop tests plus the five
install-catalog Services tests. The live 1.20.1 query resolves both official OptiFabric files and
validates their embedded dependency declarations without partial warnings. The return-to-selection
fixture locates Minecraft row 5,000 and loader row 300 after scrolling away and switching pages.

## Service-owned eligibility boundary

InstallCatalogStateContract owns the sealed catalog state key and declaration independently of the
service implementation. A sealed typed `minecraft.install.eligibility` XSR query computes loader
visibility, per-build conflict/notice, commit rejection and selection transitions from catalog
metadata. Desktop supplies selection intent and projects immutable results; it must not reference
InstallCompatibility, duplicate its predicates, or fetch network metadata. Eligibility queries are
bounded in-memory reads, with no I/O or asynchronous wait. Architecture regression forbids direct
Desktop references to compatibility implementation symbols and the former service-owned state key.
