# Telemetry tiers and controlled rollouts

This supersedes the single consent gate in XSR-517. Necessary telemetry is enabled
in every channel. Diagnostic telemetry is the user experience improvement program:
mandatory and disclosed in CI/Alpha/Beta; optional, initially disabled, in stable.
The existing TelemetryExperienceProgram preference controls diagnostics only.

Necessary events contain only build, OS, architecture and a bounded result category:
application start/failure and update/rollout check outcomes. Diagnostics add game and
background task lifecycle/performance categories. Neither tier sends account names,
tokens, paths, instance names, log text, process IDs, hardware IDs or arbitrary exception
messages. Event names and tiers are fixed in an allowlist; an event cannot be relabeled
necessary by a caller. Worker v2 validates tier, event and field set before aggregate
storage. Legacy v1 ingestion remains separate and classified as legacy, not necessary.

Queues are bounded separately. Revoking diagnostics removes pending diagnostic events
and cancels in-flight batches containing them; necessary facts remain eligible. Upload
failure never blocks launcher work. Data already accepted by the server cannot be recalled.
Counters represent events, not users; retries can duplicate counts. No raw payload storage.

Rollouts use a local random seed, never a hardware/account identifier. The seed is not
uploaded. A stable hash of seed and rule ID determines a bucket; increasing the percentage
retains existing treatment members. Rules constrain channel/platform/version, expire,
and can be disabled. Only compiled-in feature keys and released package versions are
eligible. Remote policy never supplies code, command arguments, paths or credentials.
Unknown/expired feature rules use local defaults. Invalid update policy must not promote
an otherwise gated release. Public GitHub downloads remain available independently.

The administrator API uses the existing authenticated administrator boundary. Rule writes
are versioned and bounded; a replacement changes eligibility without rewriting local user
preferences. Gray assignment is not authorization and cannot weaken security, signature
verification, diagnostic consent, accessibility or installation protection.

## Implemented protocol and lifecycle

- `/v2/launcher/telemetry` enforces event-to-tier assignment; v1 remains opt-in legacy.
- Task Center and game process state changes supply diagnostic lifecycle events. Error logs
  supply only a necessary failure category, never their message. Update and rollout checks
  record bounded success/failure results. CI/Alpha/Beta policy is enforced inside the queue
  service, including direct consent commands; the stable preference can revoke diagnostics.
- `/v2/launcher/rollouts` supplies at most 32 rules in 32 KiB. An existing administrator can
  GET/PUT `/v1/admin/launcher/rollouts`; writes compare the policy revision atomically.
- Each rule names a stable cohort ID, one released version or compiled feature, channels,
  RIDs, a UTC expiry, an enabled flag and 0..10000 basis points. A target has one rule.
- The first compiled feature is `telemetry.compact-batches` (20 instead of 50 events).
  It changes batching only. Public state is `launcher.rollout.snapshot`; the transport
  consumes the service decision. Refresh occurs off the UI thread every five minutes;
  failed refresh and expiry restore the default batch size. Exposure is diagnostic.
- Update discovery uses `rollout=1`; clients require the rollout field and evaluate locally.
  Older clients receive only fully released versions. A disabled, expired, mismatched or
  zero-percent update rule holds the release back. Removing its rule deliberately releases
  it to everyone. GitHub downloads remain independent. Discovery does not bypass signatures
  or resolve the separately open SEC-07 privileged updater boundary.
- The administrator page edits rules and distinguishes necessary, diagnostic and historical
  aggregate data. Saving a stale revision returns 409 and never overwrites newer rules.
- No active experiment is enabled by deployment; the initial rule set is empty.
