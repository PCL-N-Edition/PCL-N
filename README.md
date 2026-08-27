# PCL N XSR migration

`refactor/xsr` is a clean-slate architecture branch. It intentionally contains no launcher source code, application project, solution, legacy build pipeline, or vendored/submodule implementation.

The existing product remains in the separate `dev` worktree and is consulted as a read-only behavioral reference. Legacy projects are never referenced, copied wholesale, or merged into this branch.

Current tracked implementation baseline:

- [XSR architecture lock](docs/xsr/README.md)
- [clean-slate reference rules](docs/xsr/source-reference.md)
- [product version policy](docs/xsr/versioning.md)
- central version projection in `eng/xsr/Xsr.Version.props`

There is currently nothing to build or run. The first source project will be introduced as a closed XSR migration unit together with its architecture gate and tests.

The XSR product line uses `2.0.0`, `2.0.0.alpha.N`, `2.0.0.beta.N`, and `2.0.0.ci.ffffff`.
