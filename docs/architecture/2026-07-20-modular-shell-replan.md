# PCL N 底层架构重规划

> **Status:** Proposed  
> **Date:** 2026-07-20  
> **Method:** engineering-system-designer（现有系统评估 + 模块化单体）  
> **Scope:** 桌面启动器主线（`PCL.Desktop` / `PCL.Application` / `PCL.Domain` / Hosting），不含 `PCL.Server` 独立部署  
> **补充：** 多 skill 落地策略见 [2026-07-20-multi-skill-architecture-plan.md](./2026-07-20-multi-skill-architecture-plan.md)（MVVM Toolkit / DI / Messenger / 插件边界 / CQRS-lite）  
> **Skill 状态（2026-07-20）：** `architecture`、`mvvm-toolkit`、`mvvm-toolkit-di`、`mvvm-toolkit-messenger` 已安装；实现以 multi-skill 文档 ADR-003 为准。

---

## 1. 问题陈述

当前仓库已经有一层**名义上的分层**（Portable / Domain / Application / Platform / UI.Abstractions / Desktop），但运行时主路径被 **`MainWindow.axaml.cs`（约 9.2k 行）** 吸成了“上帝对象”：导航、标题栏、实验性 chrome、版本选择、启动、下载任务、AI 修复、对话框、插件注入全部挤在同一文件。

结果：

| 症状 | 证据 |
|------|------|
| 改 UI 必碰业务 | 版本库、FAB、标题栏、文件夹状态都写在 MainWindow |
| 实验性双轨失控 | 经典 / 实验布局分支散落在 CreateLaunchMainPage、ApplyInstanceSelectPage、ApplyExperimentalChrome |
| Domain 几乎空壳 | `PCL.Domain` 仅 Java + LaunchProfile 等 ~7 个文件 |
| 单文件过大 | `MinecraftAiRepairAdvisor` ~3.1k；Headless 测试 ~10.7k；安装/启动页 1k–1.4k |
| 回归成本高 | 任意 chrome / select 改动都要在 MainWindow 里定位状态机 |

**目标不是微服务拆分**（团队规模与产品形态都不需要），而是把**模块化单体（Modular Monolith）做实**：壳薄、特性厚、状态可测、边界可替换。

---

## 2. 需求

### 2.1 功能需求（FR）

1. **启动闭环**：账号 → 选版本/文件夹 → 装依赖 → 规划参数 → 启进程/JvmHost → 日志/关游戏  
2. **实例与资源**：实例元数据、Mod/资源/存档/服务器、导入整合包  
3. **下载与任务**：原版/加载器/库/资源下载；任务管理器与进度 FAB  
4. **设置与实验开关**：设置持久化；实验性 UI / JvmHost / AI 修复等可回退  
5. **插件宿主**：导航/设置页/UI 槽位/账号下载启动 API 注入  
6. **跨平台壳**：Win/Linux/macOS；主题、本地化、单实例

### 2.2 非功能需求（NFR）— 桌面本地

| 指标 | 目标 | 说明 |
|------|------|------|
| 冷启动到可操作 | p95 &lt; 3s（开发机） | 不含插件/首次下载模型 |
| 主线程卡顿 | 连续 &gt;100ms 帧空档应有日志 | UI 操作与 IO 分离 |
| 并发下载任务 | 默认 ≤ 线程池上限（现有设置） | 任务状态单一真源 |
| 实例规模 | 单文件夹 ≤ 500 versions 可列表 | 列表虚拟化可后续 |
| 内存（空闲主页） | 合理自包含占用；实验 AI 模型另计 | AI 模型下载为可选路径 |
| 可回退 | 实验 UI 一键关回经典 | 业务状态不得绑死在实验页 |
| 可测试 | Shell / Store / Feature 可单测 | Headless 不再只靠巨型 MainWindow 测 |

### 2.3 明确不做（本阶段）

- 不把 Desktop 拆成多个进程/微服务  
- 不重写 Avalonia 控件库  
- 不一次性删除经典 UI（Strangler Fig 迁移）  
- 不在本阶段重做 `PCL.Server` / 在线服

---

## 3. 容量估算（本地桌面适配）

> 本产品是单用户桌面进程，不是公网 API。容量关注点是**本机 IO / 进程 / UI 状态**，不是 QPS。

### 3.1 典型负载假设

| 参数 | 假设 | 推导 |
|------|------|------|
| 同时打开窗口 | 1 | 单实例协调器 |
| Minecraft 文件夹 | 1–5 | 当前/官方/用户 + 自定义 |
| 每文件夹 versions | 20–200 | 列表刷新 |
| 并发下载任务 | 1–8 | 任务管理 |
| 同时运行游戏 | 0–1（常见） | 关游戏/日志 FAB |
| 插件数 | 0–20 | HostModule 注册 |

### 3.2 1x / 5x / 10x

| 维度 | 1x | 5x | 10x | 瓶颈 |
|------|----|----|-----|------|
| versions 扫描 | 50 | 250 | 500 | 磁盘枚举 + JSON 解析 |
| 下载并发文件 | 64 线程 | 同 | 同 | 带宽 / 磁盘 |
| 插件导航项 | 10 | 50 | 100 | 导航注册表与 UI 组合 |
| 启动规划耗时 | 0.5–2s | — | — | classpath / 校验 |

**结论：** 瓶颈在 **MainWindow 状态耦合与主线程工作**，不是分布式吞吐。架构优化优先 **解耦与异步边界**，而不是加队列中间件。

---

## 4. 现状架构评估

### 4.1 依赖方向（保留）

```
PCL.Desktop ──► PCL.Application ──► PCL.Domain
       │                │              ▲
       │                ├──► PCL.Platform.Abstractions
       │                └──► PCL.UI.Abstractions
       ├──► PCL.Platform
       └──► PCL.UI.Abstractions

PCL.Core.Portable ◄── Application / Domain / Platform（原语）
```

架构测试已禁止 Desktop 引用 WPF / 旧 `PCL.Core` / `PCL.Plugin` 程序集直连（插件经 Host 桥接）。**分层骨架正确，问题在 Desktop 实现塌缩。**

### 4.2 热点与责任泄漏

```
MainWindow (9.2k)
 ├─ Window chrome / title subpage / experimental glass FAB
 ├─ Navigation host (left/right swap)
 ├─ Launch orchestration + login pages
 ├─ Instance select/manage/export/install/...
 ├─ Download install triggers
 ├─ Task manager snapshots
 ├─ Game process extras + AI repair entry
 ├─ Settings apply + theme + background
 └─ Dialogs / hints / drag-drop / update
```

Application 层已有大量服务（`Minecraft/*` 70 文件），但 **UI 仍直接编排多服务**，缺少：

- 稳定的 **Feature Facade / Use-case**
- 全局 **UI 状态 Store**（文件夹、选中实例、运行中游戏）
- **Shell** 与 **Feature** 的清晰接口

### 4.3 80/20

**一个最大杠杆点：** 把 `MainWindow` 降为「窗口壳 + 组合根」，把导航、chrome、启动、实例选择迁出。  
预计可消除后续 UI 改动 70%+ 的交叉冲突。

---

## 5. 目标高层架构

### 5.1 原则

1. **模块化单体，不拆服务**（&lt;10 人团队规则）  
2. **最少新增组件**：能用类库/命名空间解决的不新建进程  
3. **可逆决策**：经典/实验是 **Presentation Profile**，不是两套业务  
4. **状态单一真源**：文件夹、实例、任务、运行游戏各一 Store  
5. **Strangler Fig**：新壳旁路旧 MainWindow 逻辑，按路由迁移

### 5.2 逻辑视图

```
┌─────────────────────────────────────────────────────────────────┐
│                         PCL.Desktop                              │
│  ┌─────────────┐  ┌──────────────┐  ┌─────────────────────────┐ │
│  │ AppShell    │  │ Navigation   │  │ Feature Modules         │ │
│  │ Window+     │◄─┤ Host         │◄─┤ Launch / Instances /    │ │
│  │ Chrome+FAB  │  │ (left/right) │  │ Download / Community /  │ │
│  │ TitleBar    │  │              │  │ Settings / Tasks        │ │
│  └──────┬──────┘  └──────┬───────┘  └────────────┬────────────┘ │
│         │                │                       │              │
│  ┌──────▼────────────────▼───────────────────────▼────────────┐ │
│  │ Session State (UI-thread aware stores)                     │ │
│  │ FolderStore · InstanceStore · TaskStore · GameSessionStore │ │
│  │ ExperimentalProfile (chrome + layout strategy)             │ │
│  └───────────────────────────┬────────────────────────────────┘ │
│                              │                                   │
│  ┌───────────────────────────▼────────────────────────────────┐ │
│  │ Hosting Adapter (DesktopHost / Plugin bridges)             │ │
│  └───────────────────────────┬────────────────────────────────┘ │
└──────────────────────────────┼──────────────────────────────────┘
                               │
┌──────────────────────────────▼──────────────────────────────────┐
│ PCL.Application — Use-cases / Services                           │
│  Launching · Minecraft.* · Accounts · Downloads · Settings · …  │
└──────────────────────────────┬──────────────────────────────────┘
                               │
        ┌──────────────────────┼──────────────────────┐
        ▼                      ▼                      ▼
 PCL.Domain            PCL.Platform.*          PCL.Core.Portable
 (entities/value)      (paths/process/java)    (IO/log/utils)
```

### 5.3 Sync vs Async 边界

| 边界 | 模式 | 原因 |
|------|------|------|
| UI 点击 → Use-case | async Task | 避免主线程阻塞 |
| Store 更新 → View | UI 调度器同步应用 | Avalonia 绑定/重建 |
| 下载进度 | 事件 / IProgress | 高频、可丢中间帧 |
| 插件初始化 | 启动期同步 + 失败隔离 | 已有 try/log；应 bulkhead |
| 游戏进程退出 | 事件回 UI | 现有 Exited 回调 |

### 5.4 CAP（本地状态）

桌面应用无网络分区 CAP 语义。对本地持久化：

| 数据 | 一致性选择 | 说明 |
|------|------------|------|
| 设置 / 选中版本 / 文件夹列表 | **CP 偏好**（写后读一致） | 单文件设置 store；写失败提示 |
| 任务进度 | **AP**（允许丢帧） | 最后状态最终一致即可 |
| 插件注册表 | 启动期快照 | 运行时变更走 Changed 事件 |

---

## 6. 组件深潜

### 6.1 AppShell（新建，替换 MainWindow 胖逻辑）

**职责：**

- 窗口生命周期、缩放、拖动、阴影  
- TitleBar（主层 / 子页返回）  
- ExtraDock（FAB：回顶 / 任务 / 关游戏 / 日志）  
- 全局 Hint / Dialog host  
- 应用 `ExperimentalProfile` 到 chrome（玻璃 / 经典）

**不职责：**

- 不直接调用 Minecraft 安装/启动规划细节  
- 不持有 versions 列表业务

**技术选择：** 仍为 Avalonia `Window`；逻辑拆到 `Shell/*` 部分类或独立服务。  
**为何不新建第二个窗口框架：** 成本高、无用户收益。

### 6.2 NavigationHost

**职责：** 根据 `NavigationRouteId` 挂载 left/right（或 full-bleed 单 child）。  
**接口：**

```csharp
public interface INavigationHost
{
    void Navigate(NavigationRouteId route, NavigationRequest request, bool animate);
    IDisposable PushSubPage(SubPageDescriptor page); // 选择版本 / 版本设置
    event EventHandler<NavigationRouteId>? RouteChanged;
}
```

插件继续写 `INavigationRegistry`；Host 只消费描述符。

### 6.3 Feature Module 契约

每个功能域一个模块，**自带页面工厂 + 可选子路由**：

```csharp
public interface IDesktopFeatureModule
{
    string Id { get; }                 // "launch", "instances", ...
    IReadOnlyList<NavigationRouteId> Routes { get; }
    DesktopMainPage CreateMainPage(IFeatureServices services);
    // 可选：子页（select / manage）
    bool TryOpenSubPage(SubPageId id, object? arg, out Control page);
}
```

**首批模块：**

| Module | 迁出内容 |
|--------|----------|
| `LaunchFeature` | 经典/实验主页、登录页、启动请求 |
| `InstancesFeature` | 选择版本（经典/整页）、管理、资源… |
| `DownloadsFeature` | 安装原版/加载器 |
| `CommunityFeature` | 资源社区 |
| `SettingsFeature` | 设置左/右与 binder |
| `TasksFeature` | 任务管理页 + TaskStore 投影 |

### 6.4 Session Stores（UI 状态真源）

| Store | 状态 | 持久化 |
|-------|------|--------|
| `MinecraftFolderStore` | 列表、选中 root、preset 规则 | settings keys |
| `InstanceSelectionStore` | 选中实例目录 | settings |
| `TaskSessionStore` | 任务快照、进度 | 内存 |
| `GameSessionStore` | 运行中 Process + context | 内存 |
| `ExperimentalUiProfile` | HomepageUi / chrome / full-page select | settings 只读投影 |

**规则：** View 只订阅 Store；写操作走 Store API 或 Use-case，禁止 Feature 互改私有字段。

### 6.5 Application Use-cases（收紧 Desktop 调用面）

现有服务保留，增加**门面**减少 MainWindow 式编排：

| Use-case | 封装 |
|----------|------|
| `SelectMinecraftFolder` | 规范化路径、持久化、触发实例刷新 |
| `StartMinecraft` | 现 `MinecraftLaunchCoordinator` 入口 |
| `InstallVanilla` | 现安装服务 + 任务注册 |
| `RepairLaunchFault` | AI / 常规修复策略选择 |

Desktop 只依赖门面 + 进度回调。

### 6.6 Experimental 作为 Profile，不是分叉产品

```
ExperimentalUiProfile
  ├── Chrome: Classic | Glass
  ├── LaunchHome: ClassicSplit | FullPageExperimental
  └── InstanceSelect: ClassicLeftRight | FullPageSidebar
```

业务（文件夹、实例、启动）**只实现一次**；Profile 只换 View 组合。  
避免再出现「实验页一套状态、经典页另一套状态」。

### 6.7 Domain 填充（渐进）

从 Application DTO 上提：

- `MinecraftRoot`（当前/官方/用户/自定义）  
- `GameInstance`  
- `LaunchRequest` / `LaunchResult`  
- `DownloadTask`

Domain 保持无 Avalonia / 无 UI 依赖。

---

## 7. 数据与内部 API

### 7.1 主要访问模式

| 数据 | Top 查询 | 延迟目标 |
|------|----------|----------|
| 文件夹列表 | 打开选择页 | &lt; 16ms 内存 |
| 实例列表 | 切换 root / 刷新 | &lt; 300ms（50 实例本地盘） |
| 选中实例 | 主页 / 启动 | 内存 O(1) |
| 任务进度 | FAB / 任务页 | 事件 &lt; 50ms 投递 |
| 设置项 | 设置页绑定 | 同步读缓存 |

### 7.2 Shell ↔ Feature 事件（示例）

```csharp
// Feature → Shell
public sealed record RequestTitleSubPage(string Title, Action? OnBack);
public sealed record RequestHint(string Message, bool Critical);
public sealed record RequestNavigate(NavigationRouteId Route);

// Store → Views
public sealed record FolderSelectionChanged(string? Root);
public sealed record GameRunningChanged(bool IsRunning);
```

可用轻量 `IMessenger` / 自定义 event bus；**禁止** Feature 直接 `FindControl` Shell 按钮。

### 7.3 错误码（Use-case 层统一）

| Code | 含义 | UI |
|------|------|-----|
| `Folder.NotFound` | 路径不存在 | 侧栏标缺失，允许刷新/移除 |
| `Instance.InvalidJson` | 版本 JSON 坏 | 打开文件夹而非选中 |
| `Launch.Cancelled` | 用户取消 | Hint |
| `Launch.Fault` | 结构化故障 | 修复流 |
| `Settings.PersistFailed` | 写盘失败 | Hint critical |

---

## 8. 失败模式

| 组件 | 失败 | 影响面 | 检测 | 自动恢复 |
|------|------|--------|------|----------|
| AppShell | 未处理异常 | 整窗 | 全局 handler | 记录日志；尽量不静默退出 |
| FolderStore 持久化 | IO 失败 | 列表可能回退 | catch + log | 内存态保留；Hint |
| Instance 扫描 | 权限/坏盘 | 单 root 空列表 | 异常→空+Hint | 用户换文件夹 |
| 下载任务 | 网络中断 | 单任务 Failed | 进度/状态 | 可重试；不拖垮其它任务 |
| 游戏进程 | 崩溃 | 会话结束 | Exited / fault report | 关 FAB；可选修复 |
| JvmHost 实验 | 原生失败 | 该次启动 | fault code | 建议关实验 Host |
| 插件模块 | 初始化异常 | 该插件 | try/catch | 跳过模块；不阻断主壳（目标态） |
| AI 修复 | 模型/API 失败 | 修复面板 | 状态机 | 回退常规建议 |
| 导航 | 路由缺失 | 页空白 | null provider | 占位 Loading/错误页 |

**Bulkhead：** 插件 / AI / 下载 / 启动 线程与异常互不影响主壳消息循环。

---

## 9. 监控与可观测性

在现有 `PortableLog` / `DesktopFileLog` 上规范化：

| 指标名 | 类型 | 告警建议 |
|--------|------|----------|
| `shell.startup.ms` | histogram | p95 &gt; 5000 开发关注 |
| `nav.route` | counter | — |
| `instance.scan.ms` | histogram | p95 &gt; 2000 |
| `task.active` | gauge | — |
| `launch.outcome` | counter{result} | fault 率异常 |
| `ui.mainthread.stall.ms` | histogram | &gt; 100 采样 |
| `plugin.init.fail` | counter | &gt;0 启动期注意 |

Runbook：优先看 `DesktopFileLog` 与任务页；AI/JvmHost 走已有诊断文案。

---

## 10. 迁移计划（Strangler Fig）

### Phase 0 — 冻结与契约（0.5–1 周）

- [x] 本文档评审  
- [ ] 定义 `IDesktopFeatureModule` / Stores / Shell 接口草图（代码空实现可编译）  
- [ ] 架构测试：禁止新增 `MainWindow.axaml.cs` 大段业务（可选 analyzer / 行数门槛）

### Phase 1 — Shell 抽离（1–2 周）

1. 新建 `PCL.Desktop/Shell/`：`AppShellWindow`、`TitleBarController`、`ExtraDockController`  
2. 把 chrome / FAB / title subpage / experimental glass 迁出 MainWindow  
3. MainWindow 暂时委托 Shell；行为零回归  
4. 测试：BackToTop、关游戏 FAB、标题子页

### Phase 2 — Stores（1 周）

1. `MinecraftFolderStore` 从 MainWindow 字段迁出  
2. `InstanceSelectionStore` / `GameSessionStore` / `TaskSessionStore`  
3. 选择版本页只绑 Store  
4. 测试：文件夹切换持久化、缺失路径、删除 preset

### Phase 3 — Instances + Launch 模块（2–3 周）

1. `InstancesFeature`：经典/整页 select 作为 Profile 下的两种 View  
2. `LaunchFeature`：实验主页与经典左右页  
3. 启动/登录流不再直接塞 MainWindow  
4. 测试：现有 Headless 选择/启动用例迁移到 Feature 级

### Phase 4 — Downloads / Tasks / Settings / Community（并行，2–4 周）

按路由 strangler；每迁一路由删一段 MainWindow。

### Phase 5 — 收尾

- MainWindow ≤ ~500 行（只剩组合）或改名为 `AppShell`  
- Domain 实体补齐常用模型  
- Headless 测试按 Feature 拆文件（避免 10k 单文件）  
- 更新 README 架构表

### 每阶段完成定义（DoD）

- 编译 + 相关 Headless 测试绿  
- 实验开/关两条路径手动冒烟  
- 不扩大 Experimental 业务分叉  
- 提交可独立回滚

---

## 11. 目录目标态（建议）

```
PCL.Desktop/
  Shell/
    AppShell.axaml(.cs)
    TitleBarController.cs
    ExtraDockController.cs
    ExperimentalUiProfile.cs
  Navigation/
    NavigationHost.cs
    SubPageStack.cs
  Session/
    MinecraftFolderStore.cs
    InstanceSelectionStore.cs
    TaskSessionStore.cs
    GameSessionStore.cs
  Features/
    Launching/     # 已有 Views + 新增 LaunchFeatureModule.cs
    Instances/
    Downloads/
    Community/
    Settings/
    Tasks/
  Hosting/         # 插件桥保持
  Controls/        # 控件库
```

Application 侧可选：

```
PCL.Application/
  UseCases/
    Launch/
    Instances/
    Downloads/
```

（UseCases 可为现有服务上的薄包装，避免大搬家。）

---

## 12. 风险与权衡

| 决策 | 选择 | 为何不用更简单/更复杂方案 |
|------|------|---------------------------|
| 模块化单体 | ✅ | 微服务运维成本与桌面产品不匹配 |
| 保留经典 UI | ✅ Profile | 大爆炸删经典风险过高 |
| Store 在 Desktop | ✅ | 状态强绑 UI 线程；Domain 只持业务实体 |
| 消息总线 | 轻量自研/现有事件 | 不上 MediatR 全家桶除非痛点证明 |
| 立刻拆 MainWindow 文件 | 分阶段 | 一次 PR 过大无法审 |

---

## 13. 成本（工程）

| 阶段 | 粗估 |
|------|------|
| Phase 1 Shell | 3–6 人日 |
| Phase 2 Stores | 2–4 人日 |
| Phase 3 Launch+Instances | 8–15 人日 |
| Phase 4 其余特性 | 10–20 人日 |
| Phase 5 收尾+测 | 3–5 人日 |

**不增加**云基础设施成本（纯本地架构债清理）。

---

## 14. 自检（system-designer checklist）

- [x] 每个组件有失败模式  
- [x] 本地持久化一致性选择已说明  
- [x] 1x/5x/10x 容量与瓶颈（桌面适配）  
- [x] 技术选择有“为何不更简单”  
- [x] Shell/Feature/Store 边界契约  
- [x] 监控指标草稿  
- [x] FR 映射到组件（启动/实例/下载/设置/插件/壳）  
- [x] 迁移增量而非 big-bang  

---

## 15. 建议的下一步（需产品确认）

1. **采纳本文为 ADR 基线**，在 `dev` 上开 `arch/modular-shell` 分支  
2. 先做 **Phase 1 Shell 抽离**（收益最大、风险可控）  
3. 实验性 UI 新功能只允许通过 `ExperimentalUiProfile` + Feature View 扩展，禁止再往 MainWindow 堆逻辑  

---

## Appendix A — 现状热点文件（2026-07-20 测量）

| Lines | Path |
|------:|------|
| 9203 | `PCL.Desktop/Views/MainWindow.axaml.cs` |
| 3099 | `PCL.Desktop/Features/Launching/MinecraftAiRepairAdvisor.cs` |
| 1462 | `PageLaunchHomeExperimental.axaml.cs` |
| 1456 | `PageDownloadInstall.axaml.cs` |
| 949 | `MinecraftLaunchCoordinator.cs` |
| 10731 | `PCL.Desktop.Test/AvaloniaHeadlessTests.cs`（测试债） |

## Appendix B — ADR（与 multi-skill 文档对齐）

| ID | 标题 | 状态 |
|----|------|------|
| ADR-001 | 采用模块化单体，不拆微服务 | Accepted |
| ADR-002 | Experimental 仅为 Presentation Profile | Accepted |
| ADR-003 | CommunityToolkit.Mvvm + DI + WeakReferenceMessenger | Accepted（实现待 Phase 0） |
| ADR-004 | 不强制 Zafiro 整栈 | Accepted |
| ADR-005 | 插件边界冻结，Shell 适配 | Accepted |
| ADR-006 | CQRS-lite 无 MediatR | Accepted |
| ADR-007 | MainWindow Strangler + Session Stores | Accepted |

细则与组合根/消息约定见 multi-skill 文档 §3、§8。
