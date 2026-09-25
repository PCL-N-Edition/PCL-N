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
