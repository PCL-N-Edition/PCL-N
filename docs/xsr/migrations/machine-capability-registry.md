# Machine Capability Registry 1.1 — foundation

Source: user-provided `PCL Nexa Machine Capability Registry 1.0.md` (body version 1.1, 2026-09-19).

## Boundary

Machine facts, platform actions, environment facts, derived capabilities, estimates, policies, preflight issues and remediation are distinct. `Nexa.Services.Capabilities` owns typed definitions, immutable observations, the dependency graph and asynchronous provider collection. `Nexa.Services.Composition` seals the query/refresh routes; Desktop consumes only their DTOs and the separately declared revision state. Machine capabilities are observations, not executable Sidecar offers and not authorization grants. The existing Capability Fabric remains the authority for provider invocation.

## First delivered slice

- Stable namespace allowlist from Registry 1.1; typed `Capability<T>` and metadata (availability, source, provider, confidence, timestamp, requirements, permission, stability).
- Sealed registry rejects duplicate IDs, missing dependencies and cycles. Read-only snapshots have ordinal order and typed lookup. Registration never performs hardware probes.
- A broker coalesces concurrent refreshes, probes off the UI thread, isolates failed providers, enforces declared ownership and types, and publishes a revision only after a complete snapshot. Caller cancellation does not cancel shared work. Cached facts are refreshed explicitly or after a short TTL.
- A built-in provider observes OS/architecture, runtime, process-available CPU/ISA and physical/commit memory on supported systems. Physical and commit budgets are separate. Unknown hardware metrics remain unavailable, never synthesized from GC heap limits or GPU marketing VRAM.
- Settings gains a read-only Platform Features page with refresh, value, status, source and provider evidence. No cloud settings are reintroduced. No permissions are escalated and no platform mutation occurs during inspection.
- Preflight issue construction enforces: only verified hard constraints may block. Information does not raise overall severity. Estimator/compatibility/policy/launch wiring, per-instance environment providers, calibration, remediation execution and hardware-specific GPU/thermal providers remain separate follow-up slices; their behavior is not claimed as implemented.

## Validation

Contract tests cover registry sealing/type/ownership/dependency invariants, immutable snapshots, cached/coalesced queries, failure isolation, cancellation, separate memory budgets and preflight certainty. Desktop tests cover Platform Features navigation and sealed-query rendering. Architecture tests forbid direct broker/provider references in Desktop; NativeAOT shell and trim smoke remain required.

Windows memory evidence uses the documented [PERFORMANCE_INFORMATION](https://learn.microsoft.com/en-us/windows/win32/api/psapi/ns-psapi-performance_information) page counters and PageSize; the commit limit is not treated as a hard launch blocker. The built-in Linux provider reads named `/proc/meminfo` counters. macOS memory and device-specific providers explicitly remain NotImplemented; shared runtime facts work across all three platforms.
