# PCL N XSR migration

`refactor/xsr` is a clean-slate architecture branch. It contains the new .NET 10 XSR project graph, but no migrated launcher behavior, legacy build pipeline, or vendored/submodule implementation.

The existing product remains in the separate `dev` worktree and is consulted as a read-only behavioral reference. Legacy projects are never referenced, copied wholesale, or merged into this branch.

Current tracked implementation baseline:

- [XSR architecture lock](docs/xsr/README.md)
- [clean-slate reference rules](docs/xsr/source-reference.md)
- [product version policy](docs/xsr/versioning.md)
- [project-graph migration unit](docs/xsr/migrations/XSR-002-project-graph.md)
- central version projection in `eng/xsr/Xsr.Version.props`

Build and validate the scaffold with:

```text
dotnet build PCL-N.slnx
dotnet run --project tests/PCL.Xsr.ArchitectureTests/PCL.Xsr.ArchitectureTests.csproj -- --repo-root .
```

The project graph is an enforceable boundary, not an implementation-parity claim. Business capabilities are added later as closed migration units with focused tests.

The XSR product line uses `2.0.0`, `2.0.0.alpha.N`, `2.0.0.beta.N`, and `2.0.0.ci.ffffff`.
