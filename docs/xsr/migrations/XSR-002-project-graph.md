# XSR-002 project graph bootstrap

## Outcome

The clean-slate branch now has a compilable .NET 10 solution that locks the requested XSR project families without importing any legacy source or project reference. This task establishes build and ownership boundaries only; it does not claim runtime, renderer, PXML, service, or Sidecar behavior parity.

`Nexa.Plugin.Sidecar` is owned by the independent `Nexa.Plugin` XSR repository. It is deliberately absent from this solution so neither repository depends on a workstation-relative project path. The Host-side boundary is `Nexa.Sidecar.Protocol` plus `Nexa.Sidecar.Transport`.

## Project ownership

| Family | Projects | Boundary |
|---|---|---|
| Foundation | `Nexa.Core`, `Nexa.Domain`, `Nexa.Contracts` | portable primitives, rules, and stable contracts |
| XSR | `Nexa.Xsr.Abstractions`, `Runtime`, `State`, `Transport`, `Diagnostics`, `Generators` | coordination and exchange kernel |
| Services | `Nexa.Services` | composition point for capability-owned service slices |
| Renderer | `Nexa.UI.Next`, `Backend.Avalonia`, `DevTools`, `Benchmarks` | semantic renderer and edge adapters |
| PXML | `Nexa.Pxml.Compiler`, `Runtime`, `Generators` | build-time compilation and runtime artifact loading |
| Sidecar Host | `Nexa.Sidecar.Protocol`, `Nexa.Sidecar.Transport` | process-neutral wire contract and local IPC |
| Host | `Nexa.Desktop` | process bootstrap and composition root only |

The projects intentionally contain no speculative public API. Their project descriptions, references, executable/library roles, and generator markers are the initial enforceable contract. Capability APIs arrive with their first closed implementation unit.

## Enforced rules

- project references must remain inside this repository;
- the direct reference set is deny-by-default and acyclic;
- Sidecar Protocol remains independent from Host Core, Contracts, and XSR assemblies;
- XSR core projects do not reference Services, UI.Next, Avalonia, or Desktop;
- Services do not reference UI.Next, Avalonia, or Desktop;
- only `Nexa.UI.Next.Backend.Avalonia` may reference Avalonia packages;
- generator projects remain explicit Roslyn components;
- all projects inherit the canonical XSR product version and strict build settings.

## Verification

```text
dotnet restore NexaCL.slnx
dotnet build NexaCL.slnx --configuration Release
dotnet run --project tests/Nexa.Xsr.ArchitectureTests/Nexa.Xsr.ArchitectureTests.csproj --configuration Release --no-build -- --repo-root .
dotnet publish Nexa.Desktop/Nexa.Desktop.csproj --configuration Release -p:PublishTrimmed=true -p:TrimMode=link
```

The migration-branch workflow runs the same build, architecture, version, and trim gates on Linux. XSR-002 completes the architecture gate but no Wave 1 behavior item.
