# Agent instructions (PCL-N)

## Git workflow defaults

- After finishing a **fix** (or other self-contained code change the user asked for), **commit and push** by default.
- Do not wait for a separate “请提交/推送” unless the user says not to push, the change is exploratory/WIP, or the push target/branch is unclear.
- Prefer the current tracking branch (`git push origin HEAD` / push the branch that was being worked on).
- Follow existing commit-message style (conventional commits, focus on why).
- Still ask before destructive git operations (force-push, hard reset, amending published history).
