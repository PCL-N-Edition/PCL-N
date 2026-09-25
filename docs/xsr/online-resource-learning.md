# Online resource learning — working-set model v1

This is an estimate, not compatibility evidence or a verified memory requirement. It must not
create hard launch blockers. JVM heap, JVM native, private bytes and system commit are distinct
metrics; the first online learner predicts process working-set peak in MiB only.

The Worker trains at most once per UTC day, outside request handling. It selects at most 128
completed sessions from 30 days, joins at most sequence 0..2048 for each, and requires contiguous
windows, normal exit, at least 60 seconds and 30 observations, and no settings epoch changes.
One session contributes one peak, not one weight per sample or duration. Unknown/invalid features
are rejected. Cohorts are OS + loader with at least 50 sessions; these are not unique-user counts.

Four non-negative ridge-regression coefficients use features `[1, heapLimitMiB/4096,
classpathCount/256, renderDistance²/256]`. Training runs 160 fixed iterations with a step bounded
by feature energy. A training-residual P95 shifts the intercept. Every fifth session in deterministic
run-ID order is held out. Publish a cohort only if holdout coverage is at least 80% and 95%-quantile
loss is no worse than a constant training P95 baseline. This threshold is an admission check,
not a claim of universal 95% coverage. Parameters remain bounded to 0..32768.

`GET /v2/launcher/resource-model` serves an R2 document with schema=1, metric, generatedAt,
expiresAt (seven days), and cohort coefficients, training feature ranges, sample counts and
validation statistics. No run IDs, paths, account identities or raw windows are exposed. Requests
do not query D1 or train. An empty cohort list means insufficient accepted evidence, not zero usage.

Client admission must enforce schema/metric, size, timestamps, finite bounded parameters, cohort
and feature ranges. Offline, expired, invalid or unmatched models fall back to the existing local
estimator. Do not extrapolate or alter heap/native/commit estimates using working-set predictions.
Remote influence must be bounded and its provenance shown as an estimate. Telemetry is self-reported;
these gates do not prevent coordinated poisoning, and coefficients are not trusted executable code.

The client admission component uses a 64 KiB actual-read limit and a three-second refresh timeout.
It rejects duplicate properties/cohorts, unsupported metrics, malformed feature vectors, weak
validation statistics, invalid sample counts, and expired or overlong validity windows. Prediction
also checks expiry, input bounds, training ranges, and the final 64..65536 MiB output range. It does
not extrapolate. Refreshes serialize and cannot replace a newer document with an older generation.
A failed refresh retains a previously admitted session-cache document only until its original expiry;
there is no disk cache or extension of validity on network failure. Cancellation propagates.

Current slice: server trainer, scheduled aggregation, aggregate download route, client admission and
explicit download/session-cache component, and deterministic regression/SQL/privacy tests are implemented.
Production background refresh wiring and estimate projection, mod-specific learned
profiles, administrator model-quality presentation and live deployment remain pending. Alpha 5 is
not complete from this slice alone.
