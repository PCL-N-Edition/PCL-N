# Architecture

| Document | Status |
|----------|--------|
| [2026-07-20 Modular Shell Replan](./2026-07-20-modular-shell-replan.md) | Proposed（system-designer 基线） |
| [2026-07-20 Multi-Skill Architecture Plan](./2026-07-20-multi-skill-architecture-plan.md) | Proposed（MVVM/DI/Messenger/插件/CQRS-lite 补充） |

## Skill coverage

| Skill | Document section |
|-------|------------------|
| engineering-system-designer | modular-shell-replan（全文） |
| architecture / backend-architect | multi-skill §2、§6、ADR |
| mvvm-toolkit / di / messenger | multi-skill §3、§4 |
| avalonia-zafiro-development | multi-skill §1 裁决、§3.3 务实映射 |
| dotnet-desktop-plugin-architect | multi-skill §5 |

Legacy layering (Portable / Domain / Application / Platform / Desktop) remains the dependency backbone. Active debt is **Desktop shell collapse** (`MainWindow` god-object), addressed by the modular shell + MVVM composition plan above.
