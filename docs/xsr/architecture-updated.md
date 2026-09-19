# XSR Architecture

## 1. Direction

XSR is the long-term architecture of PCL Nexa:

```text
X = execution and exchange runtime
S = business services
R = semantic renderer
```

PCL Nexa uses a **NativeAOT Host supervised Multi-Sidecar Capability Fabric**.

The Host remains the authority for:

- process bootstrap and composition;
- capability discovery and routing;
- permission enforcement;
- authoritative Host state;
- presentation continuity;
- Sidecar lifecycle and process supervision;
- platform integration;
- local Minecraft execution.

Sidecars are independently versioned process-level capability providers.

Third-party managed plugins never execute inside the NativeAOT Host.

---

## 2. Clean-slate rule

`refactor/xsr` is a clean-slate implementation.

The legacy `dev` branch is a behavior and compatibility reference only.

XSR code may reproduce verified:

- behavior;
- algorithms;
- data formats;
- protocol knowledge;
- platform handling;

but must not import the legacy dependency graph as the basis of the new architecture.

There is no periodic `dev -> refactor/xsr` merge.

Legacy fixes are forward-ported by meaning and verified by explicit tests.

---

# 3. Runtime topology

The target runtime shape is:

```text
                         PCL Nexa Host
                         NativeAOT
                             │
             ┌───────────────┼────────────────┐
             │               │                │
             ▼               ▼                ▼
        XSR Runtime      Services         UI.Next
             │               │                │
             └───────────────┼────────────────┘
                             │
                    XSR Capability Fabric
                             │
           ┌─────────────────┼──────────────────┐
           │                 │                  │
           ▼                 ▼                  ▼
   NexaCloud Sidecar   Plugin Runtime      Other Sidecars
      NativeAOT          Sidecar               ...
                          CoreCLR
                            │
                     ┌──────┼──────┐
                     ▼      ▼      ▼
                  Plugin A Plugin B Plugin C
                     ALC     ALC     ALC
```

The Host and Sidecars form a Hub-and-Spoke topology.

Sidecars may depend on capabilities provided by other Sidecars, but cross-Sidecar communication is brokered through the Host.

Private Sidecar-to-Sidecar transport is not part of the architecture.

---

# 4. Project families

| Family | Responsibility |
|---|---|
| `PCL.Core`, `PCL.Domain`, `PCL.Contracts` | portable primitives, domain rules and stable contracts |
| `PCL.Xsr.*` | registry, routing, state, scopes, events, diagnostics and transport abstractions |
| `PCL.Services.*` | business capabilities |
| `PCL.Services.Composition` | binds Services to Runtime routers |
| `PCL.UI.Next` | canonical semantic renderer |
| `PCL.UI.Next.Backend.*` | native windows, surfaces, drawing, input, IME, clipboard and accessibility bridges |
| `PCL.Pxml.*` | authoring language, compiler, generated IR and runtime loading |
| `PCL.Sidecar.*` | Sidecar protocol, transport and Host-side session infrastructure |
| `PCL.Plugin.Sidecar` | CoreCLR managed-plugin runtime |
| `PCL.Desktop` | process bootstrap and composition root |

A project is introduced only when a real ownership or dependency boundary exists.

Empty placeholder assemblies are not architecture progress.

---

# 5. Ownership rules

## 5.1 Host

The Host owns:

```text
Capability Registry
Dependency Resolution
Permission Broker
Sidecar Supervisor
Session Routing
State ownership arbitration
UI continuity
Platform integration
Minecraft local execution
```

The Host does not own third-party dynamic plugin IL.

---

## 5.2 Services

Services own:

```text
business policy
business effects
authoritative service facts
business state publication
```

A Renderer must never become a second implementation of business policy.

---

## 5.3 State

State represents current observable truth.

State ownership is explicit.

One authoritative semantic State contract cannot silently be claimed by unrelated providers.

Availability is separate from the last value.

A crashed provider may leave a last-known value, but its availability must become stale or unavailable.

---

## 5.4 Renderer

UI.Next owns:

```text
semantic entities
layout
animation
input routing
navigation
overlay
accessibility semantics
dirty tracking
render-scene production
```

Renderer reads local State and emits Intent.

Renderer does not:

```text
resolve concrete Services
call Sidecars directly
perform network I/O
execute business compatibility policy
```

---

## 5.5 Backend

Backends own:

```text
native windows
native surfaces
final draw submission
GPU/device resources
platform input
IME
clipboard
native accessibility bridge
```

Backend implementation types must not enter UI.Next public contracts.

Avalonia is a backend implementation, not the application architecture.

The architecture permits future backend replacement without changing UI.Next semantic contracts.

---

# 6. Communication primitives

XSR has four primary communication primitives:

## Command

Requests an action.

Commands are asynchronous and may be accepted before business completion.

## Query

Requests a one-time result.

Queries are asynchronous, cancellable and absent from render hot paths.

## State

Represents durable current truth.

State is the Renderer’s primary business-data input.

## Event

Represents a transient fact that already happened.

Events never substitute for current State.

---

# 7. Capability as the dependency unit

Sidecar deployment identity and business dependency identity are separate.

Consumers should depend on:

```text
Capability
```

rather than:

```text
specific Sidecar implementation
```

Preferred:

```text
requires nexa.plugin.key.issue
```

instead of:

```text
requires com.pclnexa.cloud
```

A direct Sidecar dependency is allowed only when an implementation-specific coupling is intentional and documented.

The complete Capability model is defined by `capability-fabric.md`.

---

# 8. Multi-Sidecar model

The previous assumption:

```text
one deliberate out-of-process boundary
for plugin execution
```

is superseded.

The architecture now supports:

```text
N concurrent Sidecar Sessions
```

Each Sidecar:

- is independently discovered;
- has its own identity and version;
- negotiates protocol compatibility;
- declares provided and required capabilities;
- registers runtime contracts;
- owns private runtime state;
- can fail and restart independently.

One Sidecar crash must not terminate:

```text
the Host
unrelated Sidecars
unrelated Host capabilities
```

---

# 9. Plugin execution boundary

Managed third-party plugins execute in one or more:

```text
PCL.Plugin.Sidecar
```

processes using CoreCLR.

Default topology:

```text
one CoreCLR Plugin Runtime
→ many plugins
→ one collectible ALC per plugin
```

Optional future isolation may start additional Plugin Runtime Sidecar instances.

ALC provides:

```text
dependency isolation
version isolation
unload
reload
```

ALC is not a security sandbox.

Crash isolation is provided by the Sidecar process boundary.

---

# 10. Plugin provider identity

Plugins hosted in `PCL.Plugin.Sidecar` remain independent Capability Providers.

Their logical identity is:

```text
SidecarSessionId
+
NestedProviderId
```

For example:

```text
Session 18
NestedProvider com.example.worldmap
```

Capability ownership, permissions, diagnostics, State ownership and fault attribution must be associated with the nested plugin provider rather than only with the Plugin Runtime process.

---

# 11. DRM trust boundary

DRM-protected plugin plaintext must remain inside the Plugin Runtime Sidecar.

The Host must not receive:

```text
plaintext plugin assembly
directly usable content-encryption key
decrypted plugin package
```

NexaCloud acts as an entitlement/key-issuance Capability Provider.

Plugin Runtime acts as:

```text
package verifier
decryptor
CoreCLR execution boundary
ALC manager
```

The Host only brokers the Capability request.

Detailed DRM flow belongs to the Capability Fabric and Plugin Runtime specifications.

---

# 12. Sidecar independence

Sidecar implementation language is not part of XSR semantics.

A Sidecar may be implemented using:

```text
NativeAOT
CoreCLR
Rust
C++
other runtimes
```

provided it:

- can be launched by the Host;
- supports an approved transport;
- implements the Sidecar Protocol;
- passes trust and permission policy;
- obeys Capability contracts.

---

# 13. Dynamic discovery

The Host may dynamically discover Sidecar packages.

Discovery does not imply automatic execution.

The lifecycle is conceptually:

```text
discover
→ verify
→ resolve dependencies
→ activate when policy requires
```

Activation policies may include:

```text
AutoStart
OnDemand
Lazy
Manual
```

Detailed discovery, dependency and provider semantics are defined in `capability-fabric.md`.

---

# 14. Dependency direction

The stable dependency rule is:

```text
UI
→ XSR Intent/State contracts
→ Services / Capability Fabric
→ implementation
```

Never:

```text
UI
→ concrete Service
UI
→ concrete Sidecar
UI
→ business policy helper
```

Sidecar Consumers similarly depend on Capability contracts rather than concrete Provider internals.

---

# 15. Operability

The architecture must make observable:

```text
SessionId
Provider identity
Nested provider identity
Capability
CorrelationId
Command/Query ID
duration
queue depth
backpressure
state coalescing
activation time
provider restart
dependency availability
crash-loop state
```

Cancellation, shutdown, dependency loss, crash recovery and reconnect are correctness requirements.

They are not optional performance features.

---

# 16. Security boundaries

The architecture distinguishes:

```text
process isolation
runtime isolation
capability authorization
DRM
server-side authority
```

These are not interchangeable.

NativeAOT may increase reverse-engineering cost but is not a trust boundary.

ALC may isolate managed dependencies but is not a security sandbox.

Client DRM may increase extraction cost but is not the final authority for cloud/commercial capabilities.

Final commercial authorization remains server-side.

---

# 17. Architecture summary

The target architecture can be reduced to:

```text
Keep Host NativeAOT.
Keep business ownership explicit.
Depend by Capability.
Discover by Sidecar.
Route through Host.
Observe through State.
Render locally.
Run dynamic managed plugins in CoreCLR Sidecars.
Keep plugin plaintext inside Plugin Runtime.
Keep final commercial authority on the server.
```