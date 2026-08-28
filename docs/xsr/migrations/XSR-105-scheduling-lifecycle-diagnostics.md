# XSR-105 scheduling, lifecycle, and end-to-end diagnostics

## Outcome

Wave 1 closes the X kernel operability gap: cancellable one-shot scheduling on a `TimeProvider`, guarded lifecycle state machines, and one bounded session trace that correlates dispatch, state, events, scheduling, and lifecycle observations under one session ID.

## Locked contract

- Scheduling: `XsrScheduler` schedules cancellable one-shot work through `TimeProvider.CreateTimer`; it spawns no threads and performs no synchronous waits. Work handles follow a state machine (pending, running, completed, cancelled, disposed) whose transitions are serialized under the scheduler gate, and the scheduler owns every timer — a zero due time cannot leak or double-own one. Every work item is observed exactly once — completed, cancelled, or faulted with a classification and fault type — so detached work never produces an unobserved exception. Cancelling before the due time still yields a cancelled observation. Disposing a handle is safe at any point and detaches it from the scheduler; scheduler disposal then skips it. Scheduler dispose cancels pending work and stops acceptance; pending depth is observable.
- Lifecycle: `XsrLifecycle` is a per-component state machine over `NotStarted → Starting → Running → Stopping → Stopped` with `Failed` reachable from every non-terminal phase. Illegal transitions are rejected, `Starting → Stopping` is a legal clean abort, terminal phases cannot restart (restart means a new instance), transitions are serialized under concurrency so exactly one competing caller wins, and every accepted transition is observed.
- Stable errors: services reject operations in the wrong phase with `xsr.lifecycle` (`XsrErrorKind.Lifecycle`) instead of leaking their internal state machines.
- Diagnostics: `XsrSessionTrace` is one bounded, thread-safe ring per session. Entries are neutral records — kind, semantic ID, correlation ID, timestamp, detail, success — never payloads or handler exceptions. Overflow drops the oldest entries and counts them (`DroppedCount`) instead of growing unbounded; entries addressable by correlation ID are returned oldest to newest.
- Runtime adapters (`XsrTraceDispatchObserver`, `XsrTraceStateObserver`, `XsrTraceEventObserver`, `XsrTraceSchedulerObserver`, `XsrTraceLifecycleObserver`) feed every subsystem observation into one trace. Correlation IDs are preserved wherever the subsystem contract carries them — command and query completions, event publications, and scheduled work; state changes are correlated through their semantic identity until state publication carries correlation across transport.
- The trace observes; it never changes behavior. Observer failures are isolated at every subsystem boundary, consistent with routing, state, and event observers.

## Non-goals

This unit does not introduce recurring schedules, retry or backoff policies, service dependency ordering (the composition root owns startup order), cross-process trace propagation, log sinks, or metric export. Sidecar reconnect diagnostics and durable trace transport are Wave 4 units.

## Verification

`PCL.Xsr.Runtime.Tests` covers scheduled completion with correlation, pre-due cancellation observation, fault classification with a private detail leaked nowhere, scheduler dispose, the full legal lifecycle chain, illegal-transition rejection, terminal failure, serialized competing transitions, stable lifecycle error codes, bounded trace behavior with drop counting, correlation-addressable lookup, and one end-to-end flow where a dispatched command, its follow-up event, and a state publication all land in one session trace with the command's correlation preserved. `PCL.Xsr.Diagnostics` is AOT-compatible and exercised by the NativeAOT, trim, and architecture gates.
