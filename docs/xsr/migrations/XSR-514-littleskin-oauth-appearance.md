# XSR-514 LittleSkin OAuth and appearance services

## Outcome

The LittleSkin capability comes online: the OAuth 2 client (device authorization grant,
authorization-code exchange, token refresh, profile list, Minecraft session creation, player
list, closet management, texture upload) and the Microsoft skin/cape appearance services
migrate into `PCL.Services`, all over a caller-owned `HttpClient` with stub-handler tests.

## Locked contract

- Endpoints and identities stay exact: `littleskin.cn/api/yggdrasil` for the Yggdrasil
  surface, `open.littleskin.cn` for the device flow and device-token refresh,
  `littleskin.cn/oauth` for authorization-code and passport refresh. The historical
  `Closet.ReadWrtie` scope spelling is the wire contract despite the documentation
  spelling. The requested device-flow scope set and the authorization-code scope set differ
  exactly as legacy (OpenID + offline_access exist only on the device flow).
- Device flow: request → poll with `authorization_pending` (delay ≥ 1 s),
  `slow_down` (+5 s, capped at 60 s), and the whitelisting-specific `invalid_client`
  message that points users at the third-party login alternative. Authorization-code
  exchange requires a client secret; device-flow tokens refresh on `open.littleskin.cn`
  with a fallback to the passport endpoint + secret for legacy code-flow tokens.
- Yggdrasil session: profile list through the sessionserver (a 200 body carrying
  `code: 403` means a missing `Yggdrasil.PlayerProfiles.Read` scope and is surfaced as a
  re-auth request); Minecraft token creation via `authserver/oauth` with the dash-stripped
  UUID; texture upload via the authlib-injector profile API authenticated with the
  Minecraft session token (not the OAuth token) and a 32-hex normalized UUID.
- Closet and players: player list carries pid/name/skin/cape texture ids; closet pages
  (capped at 50) report tid, a 64-hex texture hash address, and the pivot item name; applying
  a texture PUTs `skin`/`cape`; `EnsureClosetTexture` adds the texture to the closet only
  when absent.
- Microsoft appearance: skin upload (multipart `variant` slim/classic + PNG file) parses the
  canonical ACTIVE texture URL without ever throwing on malformed bodies; cape listing
  collapses duplicates and requires http(s) addresses; cape activation verifies ownership
  before the request; preview preference is ACTIVE owned cape → sessionserver address →
  nothing.

## Deliberate scope

The LittleSkin profile kind was already bridged by XSR-513; wiring these services into the
account pages is Wave 7 product UI. Legacy logging lines are dropped here — the composition
root attaches the Wave 5 logging capability where it wants observability.

## Verification

`tests/PCL.Services.Tests` (106 executable tests, 6 new) covers: the device flow from
request through a pending poll to tokens; refresh on the open endpoint; `invalid_client`
rejection with the dedicated message; the client-secret requirement of the code exchange;
profiles, Minecraft session (UUID normalization), players, closet pagination with hash
validation and pivot names, texture application, and ensure-in-closet covering both the
already-present and missing paths; Minecraft-token-authenticated texture upload with UUID
normalization, the short-UUID refusal, and multipart PUT targeting; Microsoft skin upload
with ACTIVE-texture parsing and malformed-body tolerance; and Microsoft cape
listing/deduplication, ownership-gated activation, and preview preference. All transport is
stub-handler fixtures; runs under CoreCLR and NativeAOT in CI.
