# XSR Multi-Sidecar Capability Fabric

## 1. Purpose

XSR Capability Fabric is the Host-owned runtime responsible for discovering, resolving, supervising and routing dynamically available capabilities across Host Services and multiple Sidecars.

Its core rule is:

```text
Discover by Sidecar.
Depend by Capability.
Route through Host.
```

A Sidecar is a deployment and lifecycle unit.

A Capability is a dependency and invocation unit.

These concepts must remain separate.

---

# 2. High-level topology

```text
                         PCL Nexa Host
                             │
                 XSR Capability Fabric
                             │
          ┌──────────────────┼───────────────────┐
          │                  │                   │
          ▼                  ▼                   ▼
     Host Provider      Sidecar Provider    Sidecar Provider
          │                  │                   │
      Minecraft          NexaCloud          Plugin Runtime
      Files                  │                   │
      Network                │            Nested Providers
                             │              ├─ Plugin A
                             │              ├─ Plugin B
                             │              └─ Plugin C
```

All provider interaction passes through Host authority.

---

# 3. Fabric components

The Capability Fabric consists logically of:

```text
Sidecar Discovery
Sidecar Supervisor
Capability Registry
Dependency Resolver
Provider Resolver
Capability Router
Permission Broker
State Ownership Registry
Mirror Manager
Diagnostics
```

These may initially live inside existing XSR Runtime/Composition assemblies.

The logical responsibilities must nevertheless remain explicit.

---

# 4. Sidecar Descriptor

Static Sidecar discovery produces a descriptor similar to:

```csharp
public sealed record SidecarDescriptor(
    SidecarId Id,
    SidecarVersion Version,
    SidecarRuntimeKind Runtime,
    SidecarEntrypoint Entrypoint,
    ProtocolVersionRange Protocol,
    IReadOnlyList<CapabilityOffer> Provides,
    IReadOnlyList<CapabilityRequirement> Requires,
    IReadOnlyList<PermissionRequest> Permissions,
    SidecarTrustClass Trust,
    SidecarActivationPolicy Activation);
```

Static discovery must not execute candidate code.

---

# 5. Discovery roots

Host may scan multiple roots:

```text
bundled/
installed/
enterprise/
user/
```

A candidate is not executable merely because it exists in a root.

Validation includes:

```text
manifest schema
canonical Sidecar ID
version
entrypoint
binary/package digest
signature
trust source
protocol range
permissions
Capability declarations
```

Failed candidates may be represented as rejected/quarantined descriptors for diagnostics.

---

# 6. Capability model

Capability is a versioned contract surface.

Conceptually:

```csharp
public readonly record struct XsrCapabilityId(
    XsrSemanticId Value);

public sealed record XsrCapabilityDescriptor(
    XsrCapabilityId Id,
    XsrCapabilityVersion Version,
    XsrCapabilityCardinality Cardinality);
```

A Capability may own:

```text
Commands
Queries
States
Events
```

Example:

```text
nexa.plugin.key.issue@1
```

may expose:

```text
Query:
    plugin.key.issue

State:
    plugin.entitlement.availability
```

Capability identity must remain stable independently of concrete provider identity.

---

# 7. Providers

Capability Provider may be:

```text
Host Service
Sidecar Session
Nested Provider inside a Sidecar
```

Provider metadata includes:

```text
ProviderIdentity
CapabilityVersion
Priority
TrustLevel
Health
Availability
Permissions
Session binding
```

Example:

```text
nexa.plugin.key.issue
→ NexaCloud Sidecar / Session 12
```

---

# 8. Nested providers

`PCL.Plugin.Sidecar` is itself one Sidecar Provider but hosts independent Plugin providers.

Example:

```text
Plugin Runtime / Session 18

Nested:
  com.foo.worldmap
  com.bar.shader
```

Routing identity therefore supports:

```text
Session 18
+
NestedProvider com.foo.worldmap
```

Plugins retain independent:

```text
Capability ownership
permissions
diagnostics
State ownership
fault attribution
```

even while sharing one CoreCLR process.

---

# 9. Capability requirements

A Consumer may declare:

```text
Required
Optional
Lazy
```

## Required

Missing Capability prevents ACTIVE.

```text
→ Blocked
```

## Optional

Missing Capability does not prevent activation.

```text
→ Active / Degraded
```

## Lazy

Dependency resolution may defer provider activation until the Capability is first used.

---

# 10. Prefer Capability dependencies

Preferred:

```json
{
  "requires": [
    {
      "capability": "nexa.plugin.key.issue",
      "version": ">=1 <2",
      "kind": "required"
    }
  ]
}
```

Implementation-specific Sidecar dependencies are reserved for cases where provider identity is itself part of the contract.

---

# 11. Provider cardinality

Capabilities declare:

```text
Single
Multiple
```

## Single

Fabric selects one active provider.

Example:

```text
nexa.license.authority
```

## Multiple

Multiple providers can coexist.

Examples:

```text
account.provider
storage.provider
```

Consumers may request all providers or select a specific provider according to the Capability contract.

---

# 12. Provider resolution

Provider selection may consider:

```text
Capability version
trust level
Host policy
priority
health
availability
user preference
enterprise policy
```

Consumer must not cache:

```text
PID
Pipe
Socket
Provider SessionId
Provider RuntimeId
```

Consumer binds to:

```text
Capability + Contract
```

Fabric resolves the current Provider.

---

# 13. Dependency graph

After static discovery:

```text
collect providers
→ collect requirements
→ resolve versions
→ select providers
→ build dependency graph
→ detect cycles
→ derive activation order
```

Example:

```text
Host network.http
      ↓
NexaCloud
      ↓ provides nexa.plugin.key.issue
Plugin Runtime
```

Activation:

```text
Host
→ NexaCloud
→ Plugin Runtime
```

Shutdown is reverse order.

---

# 14. Cycles

Required dependency cycles are invalid.

Example:

```text
A requires b
B requires a
```

must produce a deterministic diagnostic containing the complete cycle.

Optional edges may be excluded when resolving a valid activation graph.

Runtime must not discover hard dependency cycles only after processes are already ACTIVE.

---

# 15. Sidecar lifecycle

Fabric lifecycle:

```text
Discovered
    ↓
Verified
    ↓
Resolved
    ├────→ Blocked
    ↓
Starting
    ↓
Registering
    ↓
Ready
    ↓
Active
    ├────→ Degraded
    ↓
Stopping
    ↓
Stopped
```

Failure states:

```text
Failed
Quarantined
```

A crash loop may transition a Sidecar to `Quarantined`.

---

# 16. Activation policy

Sidecars may declare:

```text
AutoStart
OnDemand
Lazy
Manual
```

Dynamic discovery does not imply every Sidecar becomes a permanently resident process.

This is important for resource control.

---

# 17. Capability invocation

Cross-provider call:

```text
Consumer
    │
    │ Capability + Contract
    ▼
Host Capability Router
    │
    ├─ resolve Provider
    ├─ authorize caller
    ├─ validate health
    ├─ map correlation
    ├─ apply timeout
    ├─ bind cancellation
    └─ enforce queue limits
          │
          ▼
Provider
```

Sidecars do not receive direct private endpoints for their dependencies.

---

# 18. Rebinding

Capability binding survives Provider restart.

Example:

```text
nexa.plugin.key.issue
→ Session 12
```

Provider crashes and restarts:

```text
Session 12 unavailable
Session 21 becomes ACTIVE
```

Fabric updates:

```text
nexa.plugin.key.issue
→ Session 21
```

Consumers remain bound to Capability, not old Session identity.

---

# 19. Provider availability

Provider health and Capability availability are explicit.

When provider disappears:

```text
Capability availability
→ unavailable
```

Required dependents may transition:

```text
Active → Blocked
```

or:

```text
Active → Degraded
```

according to lifecycle policy.

Optional dependents remain active with the affected feature unavailable.

---

# 20. State ownership

Capability Fabric arbitrates authoritative State ownership.

A State registration is bound to:

```text
ProviderIdentity
```

Conflicting authoritative registrations are rejected.

Example:

```text
Host owns:
    minecraft.*

NexaCloud owns:
    nexa.cloud.*

Plugin com.foo owns:
    plugin.com.foo.*
```

Namespace alone is not the security mechanism; explicit registration ownership is authoritative.

---

# 21. Permissions

Capability resolution and permission authorization are separate operations.

Having a dependency does not imply full access.

Example:

```text
requires:
    nexa.account
```

does not imply permission to obtain:

```text
raw credentials
refresh tokens
internal entitlement material
```

Dispatch order:

```text
resolve Capability
→ identify caller
→ authorize requested contract
→ route
```

Nested Plugin permissions are evaluated against Plugin identity, not merely Plugin Runtime Sidecar identity.

---

# 22. Trust classes

Suggested Sidecar trust classes:

```text
BuiltIn
FirstPartySigned
EnterpriseTrusted
MarketplaceSigned
UserProvided
```

Trust class may affect:

```text
signature requirements
permission ceiling
allowed Capabilities
autostart
background execution
sandbox/resource policy
update authority
```

Nested Plugins have their own trust identity.

---

# 23. Plugin Runtime

Default third-party managed plugin topology:

```text
PCL.Plugin.Sidecar / CoreCLR
├─ Plugin A / collectible ALC
├─ Plugin B / collectible ALC
└─ Plugin C / collectible ALC
```

This prevents:

```text
N plugins
→ N CoreCLR processes
```

while keeping dynamic IL outside NativeAOT Host.

Plugin Runtime may later be sharded into multiple Sidecar Sessions for isolation.

---

# 24. Plugin loading

Plugin package lifecycle:

```text
discover
→ verify package
→ resolve dependencies
→ resolve entitlement if required
→ create/reuse ALC
→ load
→ register nested provider
→ activate
```

Unload:

```text
deactivate
→ unregister
→ remove State ownership
→ unload ALC
→ verify collectible unload
```

A Plugin that fails to unregister cleanly must not leave dangling Host contracts.

---

# 25. DRM Capability flow

Protected Plugin loading uses Capability routing.

Target flow:

```text
Encrypted Plugin Package
        │
        ▼
Plugin Runtime Sidecar
        │
        │ request Capability:
        │ nexa.plugin.key.issue
        ▼
Host Capability Fabric
        │
        ▼
NexaCloud Sidecar
        │
        ▼
Nexa Cloud Server
        │
        ▼
short-lived protected key grant
        │
        ▼
NexaCloud Sidecar
        │
        ▼
Host opaque relay
        │
        ▼
Plugin Runtime Sidecar
        │
        ├─ unwrap key
        ├─ decrypt in memory
        ├─ verify plaintext
        ├─ ALC.LoadFromStream
        └─ destroy temporary key/plaintext buffers
```

NexaCloud provides the Capability.

Plugin Runtime consumes it.

Neither side hard-codes direct process addressing.

---

# 26. Recipient-bound key grant

Plugin Runtime should generate a temporary recipient keypair per sensitive Runtime Session or activation context.

Request conceptually includes:

```text
PluginId
PluginVersion
PackageDigest
RuntimeSessionId
RecipientPublicKey
```

Server-issued grant should bind:

```text
GrantId
PluginId
PluginVersion
PackageDigest
RuntimeSessionId
IssuedAt
ExpiresAt
WrappedContentKey
ServerSignature
```

Host routes the envelope but does not obtain a directly usable CEK.

---

# 27. DRM plaintext boundary

Normative rule:

> Decrypted Plugin payload and directly usable Plugin content keys MUST NOT cross the Plugin Runtime Sidecar process boundary.

Host must not receive:

```text
raw CEK
plaintext Plugin assembly
decrypted Plugin package
```

NexaCloud is the authorization/key-issuance client.

Plugin Runtime is the plaintext execution boundary.

Server remains final entitlement authority.

---

# 28. DRM threat model

Client DRM is not treated as mathematically unbreakable.

It is intended to:

```text
prevent trivial package extraction
prevent direct package copying
prevent ordinary managed decompiler access to shipped plaintext
bind execution to entitlement
support revocation
raise reverse-engineering cost
```

It cannot guarantee resistance against a sufficiently privileged attacker performing:

```text
memory dump
process instrumentation
JIT inspection
runtime patching
```

Commercial server functionality must continue to enforce authorization server-side.

---

# 29. Plugin Runtime isolation

ALC provides managed dependency isolation.

It does not provide a security sandbox.

The Sidecar process boundary prevents Plugin Runtime failures from terminating Host.

Optional future sharding:

```text
Plugin Runtime #1
→ normal plugins

Plugin Runtime #2
→ native-heavy plugins

Plugin Runtime #3
→ restricted/experimental plugins
```

requires no change to Capability semantics.

---

# 30. Crash semantics

Example:

```text
NexaCloud crashes
→ cloud/key capabilities unavailable
→ Plugin Runtime remains alive
→ Host remains alive

Plugin Runtime crashes
→ Plugin capabilities unavailable
→ NexaCloud remains alive
→ Host remains alive
```

After recovery:

```text
new Session
→ re-register
→ atomic mirror restore
→ Capability rebind
```

---

# 31. Hot discovery

Fabric may support runtime rescanning:

```text
new Sidecar package
→ discover
→ verify
→ resolve
→ register descriptor
→ activate according to policy
```

Removal:

```text
disable
→ stop new requests
→ transition dependents
→ deactivate
→ unregister
→ stop process
```

Hot discovery is optional for the first implementation, but architecture must not assume a fixed compile-time Sidecar set.

---

# 32. Diagnostics

Fabric diagnostics should expose:

```text
Sidecar ID
Sidecar version
Session ID
Provider identity
Nested provider identity
provided Capabilities
required Capabilities
selected providers
dependency graph
activation state
health
restart count
queue depth
permission rejection
Capability rebind
```

Dependency and Provider decisions must be explainable.

---

# 33. Minimum acceptance gates

Before Multi-Sidecar Fabric is considered stable, CI/integration evidence must prove:

```text
multiple Sidecars can be discovered
malformed manifests are rejected
protocol incompatibility is rejected
required dependencies resolve
optional dependencies degrade correctly
dependency cycles are detected
multiple concurrent Sessions do not collide
identical local Runtime IDs remain isolated by Session
provider restart performs Capability rebinding
consumer does not cache provider Session identity
Sidecar crash does not terminate Host
Sidecar crash does not invalidate unrelated providers
State ownership conflicts are rejected
permissions are checked before dispatch
nested Plugin provider identity is preserved
Plugin unload unregisters all contracts
Plugin Runtime restart rebuilds mirrors transactionally
NativeAOT Host never loads Plugin IL
encrypted Plugin payload need not persist plaintext to disk
Host never receives directly usable Plugin content keys
NativeAOT/trim/architecture gates remain green
```

---

# 34. Summary

The Multi-Sidecar Capability Fabric is defined by:

```text
Sidecar = process/lifecycle unit
Capability = dependency/invocation unit
Provider = owner of a Capability implementation
Host = routing and authorization authority
State = observable truth
Plugin Runtime = CoreCLR dynamic-code boundary
NexaCloud = entitlement/key Capability provider
Server = final commercial authority
```

Operationally:

```text
Discover Sidecars.
Resolve Capabilities.
Build dependency graph.
Activate only what is needed.
Route every cross-provider call through Host.
Keep provider identity replaceable.
Keep State ownership explicit.
Keep Plugin IL out of Host.
Keep DRM plaintext inside Plugin Runtime.
```