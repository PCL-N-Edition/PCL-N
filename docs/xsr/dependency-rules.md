# XSR dependency rules

Dependencies point toward portable policy and stable contracts. Platform frameworks and composition remain at the edge.

## Allowed direction

```text
Desktop
  -> XSR Runtime
  -> Services composition
  -> UI.Next
  -> Platform implementations

Services.* -> XSR abstractions/state + Domain + Contracts + Core + Platform.Abstractions
UI.Next    -> renderer contracts + Domain/Core value types when explicitly approved
Backends   -> UI.Next + platform framework
Domain     -> Core only
```

References not shown are denied by default for new XSR projects.

## Forbidden dependencies

| Source | Must not depend on |
|---|---|
| `PCL.Domain` | Desktop, Application, renderer, Avalonia, concrete platform implementations |
| `PCL.Xsr.*` core projects | Desktop, legacy Application, Avalonia, concrete services, renderer implementation/backend |
| `PCL.Services.*` | Desktop, legacy Application, ViewModels, Avalonia, UI.Next, renderer backends |
| `PCL.UI.Next` | Desktop, legacy Application, concrete services, Sidecar implementation, Avalonia |
| `PCL.N.Plugin.*` public SDK | Host internals, Desktop, Application, concrete Platform, UI.Next internals, Avalonia, Sidecar implementation |
| Sidecar protocol/transport | plugin business assemblies and Host UI types |

`PCL.Sidecar.Protocol` has no dependency on product Core, Contracts, or XSR assemblies. `PCL.Sidecar.Transport` depends only on Protocol. Both sides of the process boundary consume these independently versioned surfaces without exchanging Host CLR objects.

`PCL.UI.Next.Backend.Avalonia` is the only new-architecture project family allowed to expose Avalonia implementation types, and those types must not leak through UI.Next public contracts.

## Enforcement

The initial project graph is defined in [migrations/XSR-002-project-graph.md](migrations/XSR-002-project-graph.md). `PCL.Xsr.ArchitectureTests` scans every project, rejects unregistered or external project references, enforces the locked direct-reference graph, verifies generator and executable roles, and prevents Avalonia packages outside the backend.

The migration-branch CI builds the full solution, runs the architecture executable, validates the selected XSR product version during build, and publishes a trimmed Desktop composition. Source analyzers follow as compilable APIs are introduced.

The gate applies these rules strictly to new project families; there is no legacy-project exception list on this branch. Expanding an allowed set to acknowledge a new dependency is an architecture decision and requires a matching document change.

Roslyn analyzers will add source-level diagnostics for forbidden namespaces, synchronous waits, reflection dispatch, and unstable plugin APIs. Once source exists, architecture tests are mandatory even before the full analyzer set is complete.
