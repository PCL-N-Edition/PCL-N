# Architecture

PCL N 桌面主线架构文档。依赖骨架仍是：

`PCL.Desktop → PCL.Application → PCL.Domain / Platform.Abstractions / UI.Abstractions`

主要技术债：**Desktop 壳塌缩**（`MainWindow.axaml.cs` ~9.2k 行上帝对象）。处理路线 = **模块化单体 Shell + Session Stores + 渐进 MVVM**。

## Documents

| Document | Status | Role |
|----------|--------|------|
| [2026-07-20 Modular Shell Replan](./2026-07-20-modular-shell-replan.md) | Proposed | system-designer 基线：容量、故障、Strangler 阶段 |
| [2026-07-20 Multi-Skill Architecture Plan](./2026-07-20-multi-skill-architecture-plan.md) | Proposed（skill 已就绪） | MVVM Toolkit / DI / Messenger / 插件边界 / CQRS-lite / ADR |

## Skill coverage（本机已安装）

| Skill | Install location | Used in |
|-------|------------------|---------|
| `engineering-system-designer` | `~\.agents\skills\` | modular-shell-replan |
| `engineering-backend-architect` | `~\.agents\skills\` | multi-skill §6 |
| `architecture` | `~\.agents\skills\architecture`、`~\.codex\skills\architecture` | multi-skill §2、ADR |
| `mvvm-toolkit` | `~\.agents` / `~\.codex` / `~\.grok\skills\mvvm-toolkit` | multi-skill §3.1 |
| `mvvm-toolkit-di` | 同上 `-di` | multi-skill §3.1 组合根 |
| `mvvm-toolkit-messenger` | 同上 `-messenger` | multi-skill §3.2 |
| `avalonia-zafiro-development` | `~\.agents\skills\` | multi-skill §1、§3.3（原则 only） |
| `dotnet-desktop-plugin-architect` | 未单独安装；对齐 `PCL.Plugin` 现网 | multi-skill §5 |

### 安装记录（2026-07-20）

```powershell
# MVVM trio（npx 卡住时改用 shallow clone + Copy-Item）
git clone --depth 1 https://github.com/github/awesome-copilot.git $env:TEMP\awesome-copilot
Copy-Item $env:TEMP\awesome-copilot\skills\mvvm-toolkit* $env:USERPROFILE\.agents\skills\ -Recurse -Force
# 同步到 .codex / .grok 同理

# architecture（managedcode/dotnet-skills）
git clone --depth 1 https://github.com/managedcode/dotnet-skills.git $env:TEMP\dotnet-skills
Copy-Item `
  "$env:TEMP\dotnet-skills\catalog\Platform\Architecture\skills\architecture" `
  "$env:USERPROFILE\.codex\skills\architecture" -Recurse -Force
Copy-Item "$env:USERPROFILE\.codex\skills\architecture" `
  "$env:USERPROFILE\.agents\skills\architecture" -Recurse -Force
```

## Implementation phases (summary)

| Phase | Goal |
|-------|------|
| 0 | ✅ `CommunityToolkit.Mvvm` + composition root + messages + feature module interface |
| 1 | ✅ Shell MVVM（TitleBar / ExtraDock / Experimental profile） |
| 2 | ✅ Session Stores 单例（Folder / Instance / Task / Game） |
| 3 | ✅ Launch + Instances Feature 模块（Select Surface；Launch 主页仍部分在 MainWindow） |
| 4 | Downloads / Tasks / Settings / Community |
| 5 | 瘦 MainWindow、拆 Headless 测试、可选 DynamicData |

详见 multi-skill 文档 §7。
