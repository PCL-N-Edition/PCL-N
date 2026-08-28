# XSR-106 runtime lifetime scopes

## Outcome

Wave 1 adds the runtime lifetime scope layer that the kernel plan defined alongside routing, state, events, scheduling, and diagnostics. A scope is a named node in a disposal tree: plugins, windows, Sidecar sessions, and service groups own one scope, and disposing it atomically releases everything registered on it and every scope below it.

This is a distinct abstraction from the XSR-104 event ordering scopes; the names do not substitute for each other. Event scopes order publications inside a bounded ring. Lifetime scopes own resources and teardown.

## Locked contract

- A scope has a session-unique `XsrScopeId`, a name, an optional parent, and a disposed flag. A root scope is created directly; children are created through `CreateChild` and are disposed with (and before) their parent.
- Resources are registered as `IDisposable` instances or cleanup actions and are released in reverse registration order. `Unregister` detaches a resource without disposing it, so ownership can move between scopes.
- Disposal is depth-first (children first, then own resources), idempotent, and safe under concurrency. One failing cleanup never stops the remaining cleanup; a scope disposes the whole subtree exactly once even when individual resources or child scopes were already disposed.
- Registration after disposal throws `ObjectDisposedException`; disposal itself never throws across resource failures.
- Composition owns scope topology: plugin unload, window teardown, Sidecar session retirement, and service shutdown are single-scope operations that cancel owned scheduled work, detach owned event subscriptions, and release owned services. The kernel does not couple scopes to specific resource types.

## Non-goals

This unit does not introduce async disposal, scope-relative service resolution, automatic wiring of routers or stores into scopes, or per-scope authorization. Composition wires resources into scopes explicitly; async disposal follows when a consumer needs it.

## Verification

`PCL.Xsr.Runtime.Tests` covers register/unregister with reverse-order release, depth-first nested disposal with parent usability after child disposal, idempotent and exactly-once cleanup, disposal after registration rejection, and a plugin-style bulk cleanup where one scope dispose cancels owned scheduled work and releases every owned resource and child scope exactly once. The scope types live in `PCL.Xsr.Runtime` and are covered by the existing NativeAOT, trim, and architecture gates.
