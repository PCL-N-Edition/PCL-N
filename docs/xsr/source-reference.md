# Legacy source reference rules

## Separate worktrees

`refactor/xsr` is a clean-slate implementation branch. The legacy product stays in a separate worktree on `dev` and is read only when a migration task needs behavioral evidence.

Use `git worktree list` to locate the current `dev` checkout. A local example is:

```text
D:\PCL-F\PCL-F       dev reference
D:\PCL-F\PCL-F-XSR   refactor/xsr implementation
```

Paths are workstation-specific and must not be embedded in product code, project files, tests, or CI.

## Allowed reference material

- externally observable behavior and UX;
- verified algorithms and protocol handling;
- user-data and cache formats;
- Minecraft metadata and launch semantics;
- platform compatibility behavior;
- test corpora and canonical expected outputs;
- security fixes and required compatibility behavior.

## Forbidden reuse

- merging `dev` into `refactor/xsr`;
- project or assembly references to the legacy worktree;
- copying a legacy project, layer, ViewModel, service locator, or UI runtime wholesale;
- adding the legacy implementation as a submodule, package, generated source, or build input;
- using filesystem links to make legacy source compile in XSR;
- changing legacy code merely to simplify the XSR implementation.

## Per-task evidence flow

```text
identify behavior in dev
  -> capture inputs, outputs, invariants, and failure semantics
  -> write a migration note or parity corpus in refactor/xsr
  -> implement against XSR boundaries from scratch
  -> run parity, architecture, AOT/trim, and integration gates
```

When behavior is ambiguous, the task records the chosen contract before implementation. Git history remains available for archaeology, but commits are not forward-merged; only validated semantics move forward.
