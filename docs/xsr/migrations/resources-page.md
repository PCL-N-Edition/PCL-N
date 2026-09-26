# Resources page

The shell Resources destination replaces the migration placeholder. Legacy Community
is a read-only behavior reference: category browsing, text/game/loader filters,
sorting, pagination, and a separate project/version detail view.

## Boundary

`ResourceCatalogContract` defines sealed search and detail queries. Services owns
HTTP, provider facets, metadata validation and response budgets. Desktop renders
immutable results and emits intents; it never performs HTTP or calls a concrete
catalog. Requests are cancellable; superseded results cannot replace current UI.
The search field survives result publication, preserving selection and focus.
Resource icons use a separate cancellable sealed query, so image latency never
delays search results. Only HTTPS cdn.modrinth.com/data/ assets are accepted, with
redirects disabled, four concurrent reads, a 1 MiB actual-byte limit and a bounded
32-entry cache. The existing encoded raster carrier gains an explicit resource-icon
factory for static PNG/WebP (1024px maximum); existing PNG-only factories retain
their contract. Decoding stays in the backend and invalid images keep a placeholder.
WebP header reference: https://developers.google.com/speed/webp/docs/riff_container.
The current-instance filter reuses `MinecraftInstallEditContract.Query` through
the existing install catalog router; Desktop does not parse version JSON or infer
Minecraft/loader versions from filenames. A changed selection invalidates its result.

The initial provider is Modrinth, for mods, modpacks, resource packs, shaders and
data packs. Provider limitations are explicit: no fabricated World category,
CurseForge aggregation, favorites or automatic dependency installation. Project
and version links open the provider website through the existing host HTTPS
action. Installation remains owned by existing install/content Services, rather
than an independent installer inside this page.

Search pages contain at most 20 entries. Detail version pages contain at most 20
rows. Metadata responses are limited to 8 MiB of actual bytes. JSON parsing uses
explicit fields, without reflection serialization, for NativeAOT. Only canonical
provider links assembled from validated identifiers leave the Service.

Reference: https://docs.modrinth.com/api/operations/searchprojects/ and
https://docs.modrinth.com/api/operations/getprojectversions/.

## Verification

Service tests cover provider facets, page offsets, invalid project identifiers,
cross-project version rejection, exact game/loader filtering, cancellation, and
actual response bytes exceeding a deliberately false Content-Length. Data packs
use `all_project_types:datapack`; the old mod-only category filter misses projects.

Desktop acceptance tests cover shell navigation, padded card bounds at multiple
window sizes, persistent search input, version links, current-instance query reuse
(including vanilla without a loader), superseded requests, and cancellation on exit.
The catalog holds only one search page; version rows are paged rather than building
thousands of UI entities at once. Offline results expose a retry action.
