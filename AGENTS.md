# Agent instructions (PCL-N)

## Git workflow defaults

- After finishing a **fix** (or other self-contained code change the user asked for), **commit and push** by default.
- Do not wait for a separate “请提交/推送” unless the user says not to push, the change is exploratory/WIP, or the push target/branch is unclear.
- Prefer the current tracking branch (`git push origin HEAD` / push the branch that was being worked on).
- Follow existing commit-message style (conventional commits, focus on why).
- Still ask before destructive git operations (force-push, hard reset, amending published history).

## XSR migration rules

- XSR development belongs on `refactor/xsr` in a dedicated Git worktree outside the active `dev` checkout. Do not build the new architecture inside the `dev` working directory.
- Treat `docs/xsr/` as the architecture lock. Update the relevant document before changing a boundary or compatibility promise.
- Migrate behavior and data contracts, not legacy type or assembly shapes. Do not modify a legacy implementation merely to make an XSR migration diff smaller.
- New services must not reference Avalonia, Desktop, renderer internals, ViewModels, or service locators. They receive commands/queries and publish state/events.
- `PCL.UI.Next` is the canonical renderer. It reads state and emits intent; it must not resolve concrete services or call a Sidecar directly.
- Sidecar hot paths use generated numeric dispatch and an extensible binary protocol. Do not add synchronous IPC, JSON data-plane messages, reflection dispatch, or CLR object exchange across the process boundary.
- Every migration task is a closed unit with a migration note, parity or contract tests, architecture tests, and AOT/trim validation where applicable.
- Updating an API baseline does not make a breaking change acceptable.
- XSR product versions use the exact dotted forms defined in `docs/xsr/versioning.md`: `2.0.0`, `2.0.0.alpha.N`, `2.0.0.beta.N`, and `2.0.0.ci.ffffff`.
