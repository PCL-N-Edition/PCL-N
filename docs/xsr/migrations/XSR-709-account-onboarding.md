# XSR-709 Account onboarding and login composition

## Scope and ownership (locked before implementation)

The user requested usable account creation, import, Microsoft, LittleSkin and third-party
login in the redesigned experimental account area. XSR-506/513/514 already provide the
profile port and authentication protocols. Reuse those services; this unit closes product
composition, not a second authentication implementation.

- Product forms remain PXML -> UI.Next IR -> runtime -> scene -> Avalonia drawing. There is
  no native account dialog or Avalonia TextBox-owned product form. A reusable PXML text input
  provides ephemeral draft editing, native text/IME input, keyboard navigation and accessible
  value semantics. It owns no account truth. Password input projects only masking characters
  into the scene, is not copyable/readable through automation, and is cleared on submit/exit.
- Services own cancellable login sessions and credential-free public progress. One shared
  Host State holds status, user-facing device code, validated verification URL, safe errors,
  and LittleSkin character choices. Device codes used to poll, passwords and access/refresh
  tokens remain private to the service operation and persisted profile port.
- Typed account commands start/cancel sessions, create offline profiles, select a LittleSkin
  character, and import profiles. Composition registers them on the production router.
  Worker completions publish state only; no worker reads or changes the UI.Next tree.
  Cancellation/replacement uses an operation generation so a superseded login cannot persist
  an account or overwrite a newer result. Persistence precedes success/selection publication.
- Microsoft and LittleSkin use the existing device authorization flow. Browser navigation
  happens only from an explicit user action, through a narrow host port restricted to the
  provider's HTTPS verification domain. The UI explains missing client configuration instead
  of pretending a network login can start. Real accounts are authorized by the user, never
  by automated tests.
- Third-party login sends credentials only after a user supplies the server and submits the
  form. Require HTTPS (loopback development HTTP may be explicitly supported); never echo
  raw server response bodies or credentials into public errors. OAuth provider tokens are
  not interchangeable with Minecraft/Yggdrasil tokens.
- Legacy discovery inspects only documented launcher data locations/overrides, not arbitrary
  credentials elsewhere on the computer. It offers an explicit import; it never silently
  reuses or modifies the old store. Import preserves the source bytes, validates the complete
  schema before writing, merges/deduplicates identities and persists atomically before state
  publication. File chooser access, if needed, remains a native host effect, not a service UI.

## Acceptance plan

Follow-up acceptance: importing while the roster is hidden must not discard its layout
invalidation. Opening the picker after import must show usable, nonzero-height profile rows,
including after navigation/style/rail changes. Account identity, roster and onboarding share
one card header; remove nested duplicate titles and back controls. Existing populated rosters
need no explanatory footer. Keep one discoverable add/import path and an explicit way back.

Use fixture HTTP/auth ports for success, denial, timeout, missing configuration, cancellation,
superseded completion and durable-save failure. Prove credentials never enter scene/state.
Exercise creation/import/login through the production composition and renderer intents.
Verify text entry, password masking, keyboard/IME/automation, no-profile onboarding, roster
refresh/focus, both styles/minimum geometry, and that old source files are untouched. Run
CoreCLR suites, architecture/format gates and NativeAOT/trimmed Desktop shell smoke.

## Verified implementation

- Add/import/provider forms use one shared card header. Offline, Microsoft, LittleSkin and
  third-party commands reuse the existing auth services, save durably, then select the profile.
  Import is explicit and deduplicated, with old source bytes untouched.
- Reproduced the hidden-roster regression: importing built rows while their ancestors were
  hidden; clearing dirt retained stale empty-list measurements. UI.Next now discards unmeasured
  dirty layout caches before acknowledging dirt. Regression covers reopening and recycled rows.
- Release solution: zero warnings/errors. Desktop 27, Services 180, UI.Next 69, PXML 36 tests;
  Avalonia six top-level scenarios include native text/IME, password-safe ValueProvider,
  accessibility focus/invoke, caption/pager/capsule/motion and lifetime checks. Architecture
  validates 29 projects; renderer benchmark passes including zero-allocation clean frames.
- Windows NativeAOT and linked-trim Desktop each render both shell styles with `--validate-shell`
  (54 semantic nodes). An initial local ILC crash was resolved with workstation GC and size
  optimization (`IlcUseServerGc=false`, `OptimizationPreference=Size`); no product build settings
  were changed for that workaround.
- Real OAuth requires configured provider app IDs and user authorization. Tests use fixture
  HTTP/auth ports; they do not claim a live account login, Narrator speech or manual OS IME test.
