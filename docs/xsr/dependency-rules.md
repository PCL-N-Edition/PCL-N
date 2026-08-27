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

`PCL.UI.Next.Backend.Avalonia` is the only new-architecture project family allowed to expose Avalonia implementation types, and those types must not leak through UI.Next public contracts.

## Enforcement

`PCL.Xsr.ArchitectureTests` scans project references and package references without loading product assemblies. `.github/workflows/xsr-architecture.yml` runs the gate on the migration branch and pull requests targeting it.

The first gate deliberately applies strict rules to new project families while reporting legacy projects as migration inventory. Expanding a forbidden list to acknowledge a new dependency is an architecture decision and requires a matching document change.

Roslyn analyzers will add source-level diagnostics for forbidden namespaces, synchronous waits, reflection dispatch, and unstable plugin APIs. Until those analyzers exist, architecture tests remain mandatory rather than optional.
