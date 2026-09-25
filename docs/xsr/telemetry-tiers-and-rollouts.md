# Telemetry tiers and controlled rollouts

This supersedes the single consent gate in XSR-517. Necessary telemetry is enabled
in every channel. Diagnostic telemetry is the user experience improvement program:
mandatory and disclosed in CI/Alpha/Beta; optional, initially disabled, in stable.
The existing TelemetryExperienceProgram preference controls diagnostics only.

Necessary events contain only build, OS, architecture and a bounded result category:
application start/failure and update/rollout check outcomes. Diagnostics add game and
background task lifecycle categories. Neither tier sends account names,
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
- The first compiled feature is `telemetry.compact-batches` (5 instead of 10 events with the richer diagnostic schema).
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

## Diagnostic observability expansion

Diagnostics now include bounded structured error records (severity, known subsystem,
exception type, up to eight symbol-only frames), metric samples and feature coverage.
Raw log bodies, arbitrary exception messages, command arguments and filesystem paths
remain local. Necessary telemetry does not gain these fields. Stable users may revoke
all of them; test-channel policy remains mandatory and visible in OOBE/settings.

Fixed metric identifiers cover dispatch/scheduler latency, launcher CPU/working-set/private
bytes/managed heap, completed JVM launch duration and actual working-set peaks, and
catalog-result cardinality. Private bytes must never be labeled JVM native or system commit.
Feature coverage means the proportion of diagnostic consent sessions that used a known
feature, not unique people. Only one coverage observation per feature per consent session;
invocation counts are separate. No persistent client identifier is transmitted.

The Worker retains 90 days of aggregate event, logarithmic histogram and error-group
counters. It stores one validated symbol-only sample per fingerprint, with administrator-only
access. P50/P95 are explicitly histogram bucket bounds, not exact percentiles. Metric and
error requests remain bounded; resource sampling is every 30 seconds, never on the UI path.

GitHub linkage reads the fixed PCL-N-Edition/PCL-N repository. Exact diagnostic fingerprint
markers or an exception type plus two matching frames may link existing issues; multiple
matches remain ambiguous. It never opens issues or sends comments automatically. A periodic
bounded sync and administrator refresh update links/status; failure retains prior information
and displays stale sync status. Telemetry is untrusted input and never creates executable
code, arbitrary URLs, commands, SQL or GitHub content.

HTTP operation timing is emitted through an optional typed logging sink, without parsing log text or forwarding request metadata. Catalog snapshots carry cache-hit and normalization measurements. Sampling is bounded to two observations per metric and sixteen distinct error signatures per 30-second window; feature coverage is recorded once per consent session, while operation counts are limited. The collector drains at most ten batches each interval and never waits on the UI thread.
