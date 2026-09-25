# Launch observations and product preflight

Design sources: `PCL Nexa Machine Capability Registry 1.0.md` (§35–56), the
resource-estimation supplement supplied on 2026-09-19, and XSR-725.

## Observation contract

Services own per-launch resource observation. Random session identity links samples only
within one execution; paths, account identity, instance names, raw JVM arguments and raw
options.txt never enter remote diagnostics. Explicit numeric/enum configuration fields are
allowlisted. Local instance identity remains root-qualified.

Observe from process creation until exit, with bounded memory and fixed cadence. Report
working set, private bytes, CPU and thread count under their actual names; missing JVM heap,
native memory, GPU and platform I/O remain unavailable. Never relabel private bytes as native
memory or system commit. Whole-run aggregates must not silently become last-N-minute aggregates.

Configuration epochs delimit windows when persisted allowlisted game options change. These
are observed persisted settings, not proof the game applied them; comparisons are descriptive,
not causal. No settings change may trigger a full capability scan or block rendering.

The first implementation samples the root JVM process every 500 ms and publishes 30-second
windows plus a final window. It does not measure child processes, FPS, JVM heap/native, or GPU.
P95 uses a bounded whole-run histogram (16 MiB bins, overflow returns measured peak), and
idle CPU samples remain part of its distribution. Settings are read from the effective game
directory, including non-isolated instances. Sequence gaps remain visible; offline queue loss
is not concealed as continuous measurement. Configuration epochs refer to persisted options
observed at window boundaries, not exact in-game application timestamps.

`diagnostic.mods` contains paged metadata IDs/versions, enabled state, source format and bounded
dependency declarations. Fabric string dependencies are parsed; TOML/Quilt/legacy formats
are explicitly partial. Nested JARs are not recursively loaded and lower completeness.
Unknown substitutions such as `${file.jarVersion}` remain unknown. Component versions reuse
the install-edit reader, and include addon selections. No filename heuristic creates verified
compatibility. Metadata is inspected after spawning on a bounded background task; it describes
the directory at inspection, not proof of the exact set the JVM loaded.

Worker stores `diagnostic.run` by `(run, sequence)` and `diagnostic.mods` by `(run, page)` with
idempotent insert, 90-day retention, administrator-only queries and bounded pagination.
The administrator dashboard consumes server-side aggregates, not per-run sample lists.
It groups peak working-set observations by launcher version, OS and loader, and presents
sample-weighted resource means by settings. Mod/version prevalence counts distinct runs.
Settings and mod groups require five runs; bounded top groups are labelled as such.
These associations are not causal effects or verified compatibility evidence. Detailed
records remain inputs to analysis, rather than an unstructured administrator feed.
The client uses 4-event batches (2 in the compact cohort) to leave room for escaped metadata.
Missing historical mod/resource profiles use -1 and zero similarity, not fictitious empty mods.

Remote samples obey diagnostic consent, include sequence IDs for retry deduplication, and
have bounded retention. Stable revocation must clear queued diagnostic data; test-channel
policy remains mandatory and disclosed. Local measurement required for launch planning is
independent of transmission consent.

## Product preflight

Collect one explicitly scoped snapshot asynchronously for the captured root and instance.
Evaluate using the existing pure engine; never collect from UI rendering. Present aggregated
issues once. Only verified hard constraints block; estimates never become hard blockers.
Continuation applies to the pending attempt only, cannot bypass Blocked, and cancellation or
a stale decision cannot start a different instance. Required file/Java repair precedes the final
gate so a repaired condition is not reported from an obsolete snapshot.

The product start route invokes the gate after planning. Its Java provider inspects only the
selected runtime (java.exe sibling of javaw.exe on Windows); a failed probe is unavailable,
not verified absence. The snapshot's heap policy is replaced with the actual planned heap
request, or unknown when custom arguments override it. Existing low-level launch routes
remain lower-level execution APIs; Desktop launches through the product start route.

## Alpha 5 sequence

1. Whole-run observations, configuration epochs, privacy/schema tests and remote inspection.
2. Actual launch gate, single aggregated UI, cancellation/stale-attempt tests.
3. Continue `alpha5-gap-status.md` with explicit implementation versus real-device validation.
   Window composition, Patch compiler rewriting and privileged update transactions remain
   separate incomplete deliverables until their own acceptance checks pass.
