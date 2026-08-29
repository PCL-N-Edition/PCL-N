# XSR-513 online account flows

## Outcome

The account capability comes online: the Microsoft device-code login chain and the Yggdrasil
third-party authenticate/validate/refresh service migrate into `PCL.Services` as
fixture-testable services over a caller-owned `HttpClient`, and a small bridge maps login
outcomes onto the persisted roster (XSR-506) while keeping its published views
credential-free. The LittleSkin OAuth client (device flow, auth code, closet, texture
upload) stays with the skin/cape unit — it is the LittleSkin capability's own surface.

## Locked contract

- Microsoft chain: device code request → poll (`authorization_pending` loops,
  `slow_down` adds five seconds to the interval, `authorization_declined` and
  `expired_token` are distinct terminal errors) → Xbox Live → XSTS (family-safety, region,
  and no-Xbox-profile `XErr` values map to specific messages) → `login_with_xbox` →
  profile with active-skin selection and transient-failure retries honoring `Retry-After`
  (max four attempts) → entitlements ownership check. The refresh token survives every
  hop and `RefreshAsync` reruns Xbox→Minecraft without a device code. The HttpClient and
  the poll delay are injected; the client id resolves from the environment exactly like
  legacy.
- Yggdrasil: `authenticate` (agent + clientToken + requestUser, stable client token
  generated when absent), `validate` (success = valid, anything else = invalid, transport
  errors count as invalid so the caller tries refresh), and `refresh`. Error bodies surface
  `errorMessage`/`error` verbatim; 401/403 without a body map to the legacy credentials
  message; responses without a usable profile are hard failures. Server URLs normalize to
  the `/api/yggdrasil` root (scheme added, `/authserver` suffix stripped). JWT access
  tokens are checked against their `exp` claim with a two-minute skew; opaque tokens count
  as unexpired.
- Roster bridge: `AccountLoginProfiles.FromMicrosoft` / `FromYggdrasil` map login results to
  `LaunchProfile` records (Microsoft, LittleSkin-by-host, or ThirdParty kinds), and
  `Upsert` replaces the roster entry matching kind + UUID or appends a new one. Credentials
  persist through the XSR-506 file port; the published `accounts.profiles` views stay
  credential-free.

## Deliberate scope

LittleSkin OAuth (device flow, authorization-code exchange, profile list, Minecraft
session, closet, texture upload) and the skin/cape services are the next unit — they are
the LittleSkin capability's own API surface rather than generic auth. Progress percentages
in the Microsoft chain keep the legacy milestones so UI polish later matches user
expectations.

## Verification

`tests/PCL.Services.Tests` (100 executable tests, 7 new) covers: the full Microsoft device
chain (device code, pending → slow-down → authorized polling with observable delays,
Xbox/XSTS/Minecraft/profile/entitlements, active skin, progress milestones); declined and
expired device codes as distinct errors; refresh without a device code; Yggdrasil
authenticate, validate (204 valid), and refresh against a normalized server, with the
server error message surfaced on rejection; server normalization (`scheme`, `/authserver`
strip, API root) and JWT expiry semantics; and the roster bridge — Microsoft, LittleSkin,
and third-party profiles landing with correct kinds, repeated logins replacing by kind +
UUID, credentials persisted, and views staying credential-free. All transport is
stub-handler fixtures; runs under CoreCLR and NativeAOT in CI.
