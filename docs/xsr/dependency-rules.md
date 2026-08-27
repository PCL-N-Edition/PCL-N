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

No source project currently exists on the clean-slate branch, so there is no project graph to scan. The first source-project migration unit must introduce architecture fitness tests and a migration-branch CI gate in the same change. Source analyzers follow as soon as there is compilable code to analyze.

The first gate applies these rules strictly to new project families; there is no legacy-project exception list on this branch. Expanding a forbidden list to acknowledge a new dependency is an architecture decision and requires a matching document change.

Roslyn analyzers will add source-level diagnostics for forbidden namespaces, synchronous waits, reflection dispatch, and unstable plugin APIs. Once source exists, architecture tests are mandatory even before the full analyzer set is complete.
