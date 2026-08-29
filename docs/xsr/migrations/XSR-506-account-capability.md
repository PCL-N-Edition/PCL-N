# XSR-506 account capability (launch profiles)

## Outcome

The account capability lands its offline data contract: the legacy launch profile store —
the persisted account list — migrated byte-shape compatible, with the profile list published
as ordered state so surfaces read the account roster locally. Credential-bearing fields stay
in the persistence layer and service results; they never enter published state.

## Locked contract

- Data contract: `LaunchProfile` carries the legacy field set and defaults verbatim —
  `Username` (required), `Info`, string-named `Kind` (`Microsoft`, `ThirdParty`, `Offline`,
  `LittleSkin`, `NCloud`), `Uuid`, `Logo`, `SvgIcon` (default `lucide/user`), `SkinAddress`,
  `AuthServer`, and the credential fields (`AccessToken`, `RefreshToken`,
  `ProviderAccessToken`, `ProviderTokenExpiresAtUnix`, `ClientToken`).
- File format: the legacy launch profile JSON — `schemaVersion` (only 1 is supported) plus
  `profiles`, serialized camelCase and indented with string enum values via a
  source-generated JSON context (AOT-safe, no reflection). Loading a legacy file and saving
  it back produces the same shape.
- Port semantics, migrated: a missing file is an empty set. An unreadable or
  unsupported-schema file is quarantined next to itself (`profiles.invalid`) and then
  surfaces as `IOException` — the service reports the stable `accounts.persist_failed` load
  error with an empty roster marked unavailable. Only the current schema can be saved.
  Writes are atomic: a write-through temporary file replaced with bounded retries (6
  attempts, linear backoff), serialized per path in-process.
- Published state: `accounts.profiles` is an ordered collection of `LaunchProfileView` keyed
  by list index (owner `PCL.Services.Accounts`). The view carries everything descriptive —
  username, info, kind, uuid, logo, svg icon, skin address, auth server — and none of the
  credentials. This split is the unit's security boundary: observing state or capturing a
  snapshot cannot leak tokens.
- Durable-first edits: `AddProfile` / `ReplaceProfile` / `RemoveProfile` validate, save the
  whole list through the port, and only then publish; Success means persisted, and a failed
  save changes nothing observable. Removal shifts later profiles down and published views
  re-index. Startup load failures keep an empty roster unavailable until the first
  successful write heals the store.
- Stable errors: `accounts.invalid_profile` (Rejected — null profile, missing username,
  undefined kind), `accounts.profile_not_found` (NotFound — index out of range),
  `accounts.persist_failed` (Unavailable).

## Deliberate scope

Online flows (Microsoft/LittleSkin OAuth, third-party Yggdrasil validate/refresh, skin and
cape services) are later units — this one deliberately covers only the persisted roster, its
file compatibility, and its state publication. `AccountProviderId` is migrated now as the
provider identity type those flows will consume.

## Verification

`tests/PCL.Services.Tests` (54 executable tests, 5 new) covers: the legacy JSON round trip
(camelCase keys, string enum kind, token and timestamp fields, default svg icon) with
record equality after reload; quarantine of an unsupported-schema file with the
service-level load error, unavailable empty roster, and healing on first write; persistence
across service restarts including replace and re-indexing removal; stable rejection of
invalid profiles and indexes; and failed saves changing nothing observable. The project
runs under CoreCLR and NativeAOT in CI.
