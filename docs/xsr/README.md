# XSR architecture lock

This directory is the normative architecture baseline for the XSR migration. The three original migration guides remain design inputs; the documents here record the decisions that are enforced in this repository.

The user-requested constraints take precedence:

- migration work happens on `refactor/xsr` in a dedicated worktree outside the `dev` checkout;
- the XSR product line starts at `2.0.0` and uses the dotted version forms in [versioning.md](versioning.md);
- the branch contains no legacy source or project graph; code is consulted from the separate `dev` worktree;
- Wave 0 locks architecture and migration policy only; it does not migrate product behavior.

## Documents

- [architecture.md](architecture.md) — system direction and project boundaries
- [dependency-rules.md](dependency-rules.md) — allowed dependency graph and CI enforcement
- [state-model.md](state-model.md) — state ownership, snapshots, deltas, and derived state
- [service-model.md](service-model.md) — service responsibilities and communication primitives
- [renderer-model.md](renderer-model.md) — UI.Next and backend boundaries
- [sidecar-protocol.md](sidecar-protocol.md) — Sidecar Fabric control/data planes
- [versioning.md](versioning.md) — XSR product-version grammar and compatibility surfaces
- [migration-map.md](migration-map.md) — waves, closed work units, and cutover gates
- [source-reference.md](source-reference.md) — clean-slate rules for consulting legacy code

## Decision process

Any change to a locked boundary requires:

1. a concrete motivating use case;
2. an update to the affected document;
3. an architecture-test or analyzer update;
4. compatibility and migration impact notes;
5. review before implementation depends on the new boundary.

Public API baselines describe the accepted surface; changing a baseline never turns a breaking change into a compatible one.
