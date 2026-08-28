# XSR-101 identifiers and registry

## Outcome

Wave 1 starts with the identity and registration primitive used by later command, query, state, and event registries. The unit introduces no service, renderer, Avalonia, Minecraft, plugin-runtime, or legacy dependency.

## Locked contract

- `XsrSemanticId` is an opaque, case-sensitive development identifier. It is non-empty and rejects whitespace and control characters, but it is not otherwise normalized or parsed into product-specific segments.
- `XsrRuntimeId` is a process-local `uint`. Value `0` is unassigned; valid IDs begin at `1`.
- `XsrRegistry<TDescriptor>` accepts registrations only during startup. Duplicate semantic IDs fail immediately.
- `Seal()` sorts the complete semantic-ID set with `StringComparer.Ordinal`, assigns contiguous runtime IDs, and returns the same immutable snapshot on repeated calls.
- Runtime-ID lookup is a bounds check plus array access. Semantic lookup remains available for registration, diagnostics, and generated-code initialization, not frame or render hot paths.
- Registration and sealing are synchronized. A sealed snapshot supports concurrent readers without further mutation.
- The registry freezes mappings, not arbitrary descriptor object state; descriptor types are expected to be immutable.

Determinism is scoped to an identical registration set. Sidecar session IDs may still be renegotiated as defined by the protocol architecture.

## Non-goals

This unit does not define command/query payloads, handlers, state revisions, event delivery, scopes, scheduling, or generated registration. Those remain separate Wave 1 units so their cancellation, error, ordering, and performance contracts can be reviewed independently.

## Verification

`PCL.Xsr.Runtime.Tests` covers invalid/default identifiers, duplicate registration, concurrent unique registration, registration-order independence, contiguous numeric allocation, sealing, two-way lookup, missing IDs, concurrent numeric reads, and zero-allocation numeric lookup. `PCL.Xsr.Abstractions` and `PCL.Xsr.Runtime` are marked AOT compatible. CI runs the tests both through CoreCLR and as a published NativeAOT executable, then runs the repository architecture gate and trimmed Desktop publish.
