# XSR-102 command and query routing

## Outcome

Wave 1 adds typed asynchronous command and query routers on top of the deterministic registry from XSR-101. Runtime dispatch uses compact IDs and closed generic route adapters; it performs no reflection or semantic-string lookup on the execution path.

## Locked contract

- Command acceptance and command completion are separate. A valid route is accepted immediately and exposes a completion task; callers may omit awaiting business completion without creating an unobserved exception.
- Every command completion, including detached completion, is sent to the configured dispatch observer. Fire-and-forget never means that rejection, cancellation, or handler failure is discarded.
- Queries return one result, are always asynchronous, accept caller cancellation, and may define an explicit timeout at the routing boundary.
- Handlers return `XsrResult` or `XsrResult<T>`. Runtime failures use stable semantic error codes; handler exceptions and CLR type names never become the public error contract.
- Cancellation requested by the caller becomes `xsr.cancelled`. Expiry of the query timeout becomes `xsr.timed_out`. Unknown IDs, contract mismatches, and handler faults have distinct stable codes.
- A correlation ID is created when the caller does not provide one and is preserved in dispatch observations.
- Route registration is mutable only before sealing. Sealing assigns IDs deterministically through `XsrRegistry<TDescriptor>` and returns immutable, concurrently readable routers.

## Non-goals

This unit does not introduce state, event delivery, scopes, scheduling, service lifecycle, retries, authorization, or transport. Those policies remain separate Wave 1 units. Business completion after a successfully accepted command is represented by later state and events rather than by keeping a UI caller blocked.

## Verification

`PCL.Xsr.Runtime.Tests` covers deterministic route IDs, asynchronous command completion, detached failure observation, stable runtime errors, caller cancellation, explicit query timeout, contract mismatch, handler exception isolation, and concurrent dispatch. The existing NativeAOT, trim, and architecture gates exercise the new routers without Avalonia, Minecraft, plugin, reflection-dispatch, or synchronous-wait dependencies.
