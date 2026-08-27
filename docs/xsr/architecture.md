# XSR architecture

## Direction

XSR is the long-term architecture of PCL N:

```text
X = execution and exchange runtime
S = business services
R = semantic renderer
```

The implementation is a modular monolith with one deliberate out-of-process boundary for plugin execution. This preserves straightforward deployment and debugging while giving business capabilities explicit ownership. A module may be extracted later only when independent deployment, scaling, or ownership justifies the operational cost.

The legacy system is a source of behavior, algorithms, data formats, protocol knowledge, and verified platform handling. It is not the foundation of the new dependency graph.

## Runtime shape

```text
                         PCL.Desktop
                    composition root only
                               |
                               v
                        PCL.Xsr.Runtime
             +-----------------+-----------------+
             |                 |                 |
             v                 v                 v
       PCL.Services.*     PCL.Xsr.State      PCL.UI.Next
             |                 |                 |
             +-----------------+-----------------+
                               |
                               v
                    Domain / Contracts / Core

        Host process                    Plugin process
  PCL.Xsr.Runtime + UI.Next  <------>  Sidecar Fabric v2
```

X coordinates service registration, command/query routing, state, events, scopes, scheduling, capabilities, Sidecar sessions, and diagnostics. It does not own business rules or presentation truth.

## Target project families

| Family | Responsibility |
|---|---|
| `PCL.Core`, `PCL.Domain`, `PCL.Contracts` | portable primitives, domain rules, and stable cross-module contracts |
| `PCL.Xsr.*` | runtime abstractions, routing, state, transport, diagnostics, and generated code |
| `PCL.Services.*` | business capabilities grouped by change ownership |
| `PCL.UI.Next` | canonical semantic renderer |
| `PCL.UI.Next.Backend.*` | platform rendering, windows, native input, IME, clipboard, and accessibility bridges |
| `PCL.Pxml.*` | authoring language, compiler, IR, generators, and runtime loading |
| `PCL.Sidecar.*`, `PCL.Plugin.Sidecar` | protocol, transport, and dynamic plugin execution |
| `PCL.Desktop` | process bootstrap and composition root |

The target list is not a requirement to create an empty project for every name. A project is introduced only when it has a clear owner and a dependency boundary worth enforcing.

## Communication model

XSR has four distinct primitives:

- Command: request an action. It is asynchronous and may be accepted before business completion.
- Query: request a one-time result. It is asynchronous, cancellable, and absent from render paths.
- State: represent a durable current fact. It is the renderer's primary input.
- Event: represent a transient fact that already happened. It never substitutes for current state.

Development identifiers may be readable strings. Source generation resolves them to compact, stable runtime IDs for hot paths. Reflection and string dispatch are not runtime routing mechanisms.

## Ownership and composition

- Services own business behavior and publish state/events.
- The state store owns observable system facts and their revisions.
- Renderers project state and emit intent without becoming a second source of truth.
- Platform projects own OS-specific implementations behind portable contracts.
- Desktop owns startup order and dependency composition, not business behavior.
- Sidecar owns plugin code execution and private plugin runtime state; Host owns the local mirror, UI module registry, permissions, and presentation continuity.

## Operability

The runtime must make `SessionId`, plugin identity, correlation ID, command/query ID, duration, queue depth, state coalescing, restarts, and activation time observable. Cancellation, backpressure, shutdown, crash recovery, and reconnect are correctness requirements, not later performance work.
