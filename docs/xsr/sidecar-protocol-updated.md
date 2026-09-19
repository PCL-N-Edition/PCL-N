# XSR Sidecar Protocol

## 1. Purpose

XSR Sidecar Protocol is the process communication protocol between the PCL Nexa Host and independently running Sidecars.

A Sidecar is a process-level Capability Provider.

The protocol is designed for:

```text
N concurrent Sidecar Sessions
```

and is not limited to Plugin Runtime.

The core model remains:

```text
register once
→ execute by runtime ID
→ observe through State
→ render locally
```

---

# 2. Transport topology

Physical transport is Host-centric:

```text
Sidecar A ─┐
Sidecar B ─┼── Host
Sidecar C ─┤
Sidecar D ─┘
```

Sidecars do not exchange private endpoint information.

A request from one Sidecar to a Capability provided by another Sidecar is sent to Host, resolved by the Capability Fabric, and routed to the active Provider Session.

---

# 3. Transport

Initial supported transports are:

```text
Windows:
Named Pipe

Unix:
Unix Domain Socket
```

Transport remains replaceable behind the Sidecar Protocol.

Shared memory or ring-buffer transport may be introduced only when profiling demonstrates a need.

Protocol semantics must not depend on transport implementation.

---

# 4. Session lifecycle

Each process activation creates one new Sidecar Session.

Session identity does not survive process restart.

Base lifecycle:

```text
PROCESS_START
→ HELLO
→ WELCOME
→ VERIFY
→ REGISTER_BEGIN
→ REGISTER_*
→ REGISTER_END
→ SNAPSHOT_BEGIN
→ SNAPSHOT_ITEM*
→ SNAPSHOT_END
→ READY
→ ACTIVE
→ DEACTIVATE
→ UNREGISTER
→ SHUTDOWN
```

A failed or restarted Sidecar creates a new Session.

Runtime IDs negotiated in one Session must never be assumed valid in another.

---

# 5. HELLO

The Sidecar sends `HELLO` containing at minimum:

```text
SidecarId
SidecarVersion
ProtocolMin
ProtocolMax
ProcessIdentity
RuntimeKind
ManifestDigest
Nonce
```

Host validates:

```text
discovered identity
manifest identity
protocol range
activation generation
expected executable
trust policy
```

Identity mismatch aborts the Session.

---

# 6. WELCOME

Host responds with:

```text
NegotiatedProtocolVersion
SessionId
HostVersion
SessionNonce
CapabilityFabricVersion
RegistrationPolicy
Limits
```

No runtime contract IDs are valid before registration completes.

---

# 7. Registration

Registration is transactional.

```text
REGISTER_BEGIN
REGISTER_CAPABILITY*
REGISTER_COMMAND*
REGISTER_QUERY*
REGISTER_STATE*
REGISTER_EVENT*
REGISTER_UI_MODULE*
REGISTER_RESOURCE*
REGISTER_PERMISSION_SURFACE*
REGISTER_END
```

The Host validates the complete registration before activation.

Validation includes:

```text
duplicate semantic IDs
unknown codecs
forbidden State ownership
undeclared Capability
manifest/runtime mismatch
permission violations
invalid UI/resource declarations
unsupported schema versions
```

Partial registration is never committed.

---

# 8. Capability registration

A Sidecar may register one or more provided Capabilities.

Conceptually:

```text
REGISTER_CAPABILITY
{
    CapabilityId
    Version
    Cardinality
    ProviderMetadata
}
```

A Sidecar registration must not silently provide Capabilities absent from static discovery policy unless Host policy explicitly permits runtime extension.

Capability ownership becomes active only after the registration transaction commits.

---

# 9. Contract registration

Commands, Queries, States and Events are registered under Provider ownership.

Conceptually:

```text
Capability
└─ Contract
```

A runtime contract registration includes:

```text
SemanticId
ContractKind
CodecShape
CapabilityOwner
PermissionSurface
```

Host assigns Session-local compact Runtime IDs after validation.

---

# 10. Runtime IDs

Runtime IDs are local to one Sidecar Session.

Logical route:

```text
SessionId
+
ContractKind
+
RuntimeId
```

Example:

```text
Session 12 / Command 7
```

and:

```text
Session 21 / Command 7
```

refer to unrelated contracts.

Semantic IDs are used during registration, diagnostics and tracing.

Hot-path dispatch uses compact numeric IDs.

---

# 11. Nested Providers

A Sidecar may host nested providers.

Primary example:

```text
PCL.Plugin.Sidecar
├─ Plugin A
├─ Plugin B
└─ Plugin C
```

Nested provider identity is explicitly registered.

Conceptually:

```text
ProviderAddress
{
    SidecarSessionId
    NestedProviderId?
}
```

A nested Plugin must receive its own:

```text
Capability ownership
permissions
State ownership
diagnostics attribution
contract namespace
```

The Plugin Runtime Sidecar must not flatten every Plugin into one indistinguishable provider.

---

# 12. Frame model

Each production data-plane frame carries at minimum:

```text
ProtocolVersion
MessageType
Flags
CorrelationId
PayloadLength
Payload
```

Production data-plane payload uses a binary extensible codec.

The codec must support:

```text
optional fields
unknown-field skipping
generated encode/decode
bounded lengths
forward/backward compatibility
```

A fixed non-extensible CLR struct layout is forbidden as the wire ABI.

JSON is permitted for:

```text
manifest
diagnostics
debug dump
development tooling
```

JSON is forbidden from the production data-plane hot path.

---

# 13. Codec registry

State and argument values use a frozen typed codec registry.

Initial examples:

```text
UTF8 String
Bool
Int32
Int64
Float64
Bytes
Generated DTO Blob
```

Unknown or unsupported codecs are rejected during registration.

Sidecars must not negotiate arbitrary executable serializers.

---

# 14. Command dispatch

Command request carries:

```text
Provider binding
Runtime Command ID
CorrelationId
Payload
Cancellation capability
Timeout metadata
```

A Command separates:

```text
route acceptance
```

from:

```text
business completion
```

All accepted command completions must be observed even if the original caller does not synchronously await them.

---

# 15. Query dispatch

Query request is:

```text
correlated
asynchronous
cancellable
timeout-aware
```

Query failures must cross the Sidecar boundary using stable XSR error semantics.

Implementation exceptions must not leak as wire-level ABI.

---

# 16. Cross-Sidecar Capability dispatch

A Sidecar Consumer never addresses another Sidecar by PID, Pipe or Session ID.

It sends a Capability request to Host.

Conceptual route:

```text
Consumer Sidecar
→ CapabilityId
→ ContractId
→ Host Capability Fabric
→ active Provider
→ Provider Session
→ Provider RuntimeId
```

Host performs:

```text
caller validation
permission authorization
Capability resolution
Provider health check
correlation translation
timeout binding
cancellation binding
backpressure
```

before forwarding.

---

# 17. State mirror

Sidecar State is mirrored into Host.

Registration closes with a transactional snapshot:

```text
SNAPSHOT_BEGIN
SNAPSHOT_ITEM*
SNAPSHOT_END
```

Host validates:

```text
coverage
duplicates
codec shape
provider ownership
revision semantics
```

and commits atomically.

Reconnect replaces the old mirror only after the new snapshot is coherent.

A partial snapshot must never become visible.

---

# 18. State deltas

After activation, Sidecar sends revisioned State deltas.

Host applies a delta only against a matching expected revision.

Replaceable State may coalesce to newest value where the State contract permits.

Events must not be silently coalesced.

---

# 19. Availability

Provider availability is independent from last-known State value.

If a Sidecar disconnects:

```text
State value
→ may remain last-known

State availability
→ stale / unavailable
```

Renderer and Services must not treat stale values as active provider truth.

---

# 20. UI modules

Sidecar registration may include Plugin/UI modules.

The Host validates and caches:

```text
Plugin UI IR
resources
binding tables
command bindings
```

Opening an already registered Plugin page performs:

```text
0 Sidecar IPC
```

Rendering reads local Host State mirrors.

Only user/business actions cross the Sidecar boundary.

---

# 21. Resources

Resource declarations may include:

```text
content
content type
SHA-256 digest
logical resource ID
```

Host caches resources content-addressed.

Resource identity is immutable for a given digest.

---

# 22. Cancellation

Cancellation uses correlation identity.

Host-side cancellation of an outstanding Sidecar operation must reach the provider.

Cancellation after Provider restart must not accidentally target a new Session operation with a reused Runtime ID.

Session identity is part of the routing generation.

---

# 23. Backpressure

Every queue is bounded.

Protocol implementations must expose:

```text
queue depth
drops/rejections
coalescing
flow-control state
```

Commands and Events may not be silently discarded.

Replaceable State updates may coalesce if explicitly allowed.

---

# 24. Health

Sidecar Session health includes:

```text
process alive
transport alive
registration state
queue health
heartbeat/health state
crash count
activation duration
last failure
```

Health is visible to Sidecar Supervisor and diagnostics.

Health reporting does not itself create a business Capability.

---

# 25. Reconnect

Reconnect always creates a new Session:

```text
new process/session
→ HELLO
→ registration
→ transactional State snapshot
→ READY
→ ACTIVE
```

Host marks the previous Provider Session unavailable.

Stable Consumer Capability bindings are resolved to the new Session only after activation succeeds.

Consumers must not cache old Session IDs.

---

# 26. Crash semantics

A Sidecar crash must not terminate Host.

A Sidecar crash must not invalidate unrelated Sidecars.

Provider-specific Capability availability becomes unavailable.

Dependent Sidecars are transitioned by the Capability Fabric according to dependency policy:

```text
Required
Optional
Lazy
```

The Sidecar Protocol reports process/session facts; dependency policy itself belongs to `capability-fabric.md`.

---

# 27. Shutdown

Ordered Session shutdown:

```text
stop new dispatch
→ cancel/drain active operations
→ DEACTIVATE
→ UNREGISTER
→ invalidate mirrors
→ SHUTDOWN
→ close transport
→ terminate process if necessary
```

Shutdown must never block the UI thread.

---

# 28. Security

Protocol identity is not sufficient authorization.

Every cross-provider request must still pass Host Capability permission policy.

A Sidecar cannot grant itself more permissions by registering additional contracts.

Sensitive encrypted payloads may use opaque end-to-end envelopes whose plaintext is intentionally unreadable to Host.

The generic Sidecar Protocol does not expose raw Plugin DRM keys as normal XSR values.

---

# 29. Compatibility axes

The following versions are independent:

```text
Sidecar Protocol
Capability contract
Plugin SDK
Plugin API
Plugin Package
Plugin UI IR
PXML language
Host product version
DRM grant format
```

None may be inferred from another.

---

# 30. Stable requirements

The Sidecar Protocol must preserve:

```text
multi-session isolation
transactional registration
transactional State snapshot
stable error semantics
correlation
timeout
cancellation
bounded queues
backpressure
unknown-field skipping
generated codecs
zero-IPC registered UI rendering
crash isolation
restart-safe routing
session-local numeric IDs
```

These are correctness requirements.