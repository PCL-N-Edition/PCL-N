# PCL N 架构补充计划（多 Skill 综合）

> **Status:** Proposed  
> **Date:** 2026-07-20  
> **Supplements:** [2026-07-20-modular-shell-replan.md](./2026-07-20-modular-shell-replan.md)  
> **Skill matrix:** 见 §0

---

## 0. Skill 矩阵与适用边界

| Skill | 本仓库适用结论 | 纳入计划的要点 |
|-------|----------------|----------------|
| `engineering-system-designer` | ✅ 主线（已出基线文档） | 需求/容量/故障/监控/Strangler |
| `architecture`（通用 .NET） | ✅ 主线 | 模块化单体、DDD 轻量、CQRS 轻量、依赖边界 |
| `avalonia-zafiro-development` | ⚠️ **原则可用，整栈不可直接落地** | Pure MVVM、绑定优先、集合管线思想；**不强制引入 Zafiro/ReactiveUI 全家桶**（现状 0 引用） |
| `mvvm-toolkit` | ✅ 推荐采用 | `ObservableObject` / `RelayCommand` / 属性通知 |
| `mvvm-toolkit-di` | ✅ 推荐采用 | `Microsoft.Extensions.DependencyInjection` 组合根 |
| `mvvm-toolkit-messenger` | ✅ 推荐采用 | `WeakReferenceMessenger` 跨 Feature 通信 |
| `engineering-backend-architect` | ⚠️ 桌面适配 | 模块化单体优先；下载/插件 bulkhead；设置持久化 expand-migrate-contract |
| `dotnet-desktop-plugin-architect` | ✅ 对齐现有 `PCL.Plugin` | 能力门控、ALC 隔离、UI 槽位、安全模式；壳不吞噬插件 |

**本机 skill 可用性（2026-07-20）：**

| Skill | 本地状态 |
|-------|----------|
| engineering-system-designer | 已安装 |
| engineering-backend-architect | 已安装 |
| avalonia-zafiro-development | 已安装 |
| architecture / mvvm-toolkit* / dotnet-desktop-plugin-architect | **未安装**；计划按上述公开惯例补全，建议后续 `npx skills add` 补齐 |

---

## 1. 总原则（冲突裁决）

多 skill 有时会互相打架，本仓库**裁决顺序**：

1. **不破坏现有分层与插件 ABI**（`PclHostApi`、`.pnp`、架构测试禁令）  
2. **模块化单体 + Strangler**（system-designer / backend-architect）  
3. **ViewModel 无 Avalonia 类型**（zafiro 原则）  
4. **渐进 MVVM**（toolkit）：先 Shell/新页，再老页  
5. **Zafiro / DynamicData / CSharpFunctionalExtensions**：仅当某 Feature **新写或大改**且团队接受依赖时引入；**不作为 Phase 1 阻塞项**

> 现状：`MainWindow` 为 code-behind 上帝对象，**零** `CommunityToolkit.Mvvm` / `ReactiveUI` / `DynamicData`。任何“一步到位 FRP”都会变成第二场重写。计划采用 **双速道**：壳与新 Feature 用 MVVM+DI；旧页 Strangler 迁出。

---

## 2. 目标架构（.NET 模块化单体 + 桌面壳）

### 2.1 逻辑分层（`architecture` + 现有项目）

```
┌──────────────────────────────────────────────────────────────┐
│ Presentation (PCL.Desktop)                                    │
│  Shell VM  │  Feature VMs  │  Views (AXAML)  │  ValueConverters│
│  Messenger · ExperimentalUiProfile · NavigationHost          │
└─────────────────────────────┬────────────────────────────────┘
                              │ 仅依赖 Application + UI.Abstractions
┌─────────────────────────────▼────────────────────────────────┐
│ Application (PCL.Application)                                 │
│  UseCases (Command/Query)  │  Services  │  Hosting registries │
│  可选: IRequestHandler 风格薄封装（CQRS-lite，不引入 MediatR） │
└───────────────┬─────────────────────────────┬────────────────┘
                │                             │
     ┌──────────▼──────────┐       ┌──────────▼──────────┐
     │ Domain (PCL.Domain) │       │ Platform.Abstractions│
     │ Entities / VOs      │       │ Paths / Process / … │
     └─────────────────────┘       └──────────┬──────────┘
                                              │
                                   ┌──────────▼──────────┐
                                   │ Platform impl       │
                                   │ Core.Portable       │
                                   └─────────────────────┘
```

**依赖规则（硬）：**

| 从 → 到 | 允许 |
|---------|------|
| Desktop → Application | ✅ |
| Desktop → Domain | ⚠️ 仅展示模型；优先 Application DTO |
| Desktop → Platform 实现 | ✅ 已有；新代码优先抽象 |
| Application → Avalonia | ❌ |
| Domain → 任何 UI / IO 框架 | ❌ |
| Plugin payload → Desktop 内部类型 | ❌（只经 Host 契约） |

### 2.2 CQRS-lite（不强制 MediatR）

按 `architecture` / backend 思路，**命令与查询分离命名**，实现可仍是普通服务：

| 类型 | 例子 | 线程 |
|------|------|------|
| **Query** | `ListInstances(root)`、`GetSelectedFolder` | 可后台 |
| **Command** | `SelectFolder`、`StartMinecraft`、`RemoveFolder` | async + 进度 |
| **Projection** | Task 列表 → FAB 进度 | UI 调度 |

禁止在 View code-behind 里拼装多服务；统一经 **UseCase / Facade**。

### 2.3 DDD 轻量（Domain 填充清单）

当前 Domain 过薄。按访问频率上提（**不**上完整聚合根仪式）：

| 概念 | 类型建议 | 来源 |
|------|----------|------|
| `MinecraftRoot` | record + kind enum (Current/Official/User/Custom) | FolderStore |
| `GameInstanceId` | 强类型 path/id | InstanceSelection |
| `LaunchSession` | 运行中会话值对象 | GameSessionStore |
| `DownloadTaskId` | 任务 id | TaskStore |
| `ExperimentalFeatureFlags` | flags VO | Settings |

Application 服务操作这些模型；Desktop VM 只绑 **只读投影**。

---

## 3. 表示层：MVVM Toolkit + DI + Messenger

### 3.1 包与组合根（`mvvm-toolkit` + `mvvm-toolkit-di`）

**新增依赖（Desktop）：**

- `CommunityToolkit.Mvvm`
- `Microsoft.Extensions.DependencyInjection`（若尚未统一；与 `IPclHost.Services` 对齐）

**组合根位置：** `Program.cs` / `App.axaml.cs` / 新建 `DesktopCompositionRoot.cs`

```
BuildServiceProvider()
  ├── Shell
  │     AppShellViewModel
  │     TitleBarViewModel
  │     ExtraDockViewModel
  ├── Session (singleton)
  │     MinecraftFolderStore
  │     InstanceSelectionStore
  │     TaskSessionStore
  │     GameSessionStore
  │     ExperimentalUiProfile
  ├── Features (transient page VMs / singleton facades)
  │     LaunchFeatureModule
  │     InstancesFeatureModule
  │     …
  ├── Application services (已有，注册为 singleton/scoped)
  └── IMessenger = WeakReferenceMessenger.Default
```

**View 规则：**

- `DataContext` 由导航宿主注入 VM，禁止 View 构造业务服务  
- View code-behind 仅：动画、焦点、极少数 Avalonia 控件互操作  
- **VM 禁止** `using Avalonia.*`（zafiro 硬规则；架构测试可逐步加）

### 3.2 Messenger 通道（`mvvm-toolkit-messenger`）

用 **弱引用消息** 替代 Feature 互相 `FindControl` / 事件场：

| Message | 发送方 | 接收方 |
|---------|--------|--------|
| `NavigateRequestMessage` | 任意 Feature | NavigationHost |
| `TitleSubPageMessage` | Feature 子页 | TitleBar VM |
| `HintMessage` | 任意 | Shell |
| `FolderSelectionChangedMessage` | FolderStore | Launch/Instances VM |
| `GameRunningChangedMessage` | GameSessionStore | ExtraDock VM |
| `TaskProgressChangedMessage` | TaskStore | ExtraDock / Tasks VM |
| `ExperimentalProfileChangedMessage` | Settings | Shell + Features |

**约定：**

- Store 变更 → 发消息 **或** `INotifyPropertyChanged`（两者择一为主：Store 用 INPC，跨模块用 Messenger）  
- 禁止循环消息；命令类消息只单向（Feature → Shell）  
- 单元测试可用 `WeakReferenceMessenger` 实例注入

### 3.3 Avalonia / Zafiro 原则的**务实映射**

| Zafiro / FRP 建议 | PCL N 落地 |
|-------------------|------------|
| Pure ViewModel | ✅ Phase 1 起强制新代码 |
| DynamicData `SourceCache` | 🟡 Phase 3+ 列表页可选；前期 `ObservableCollection` + diff 刷新 |
| `RefreshableCollection` | 🟡 实例列表/社区列表可仿此模式自研薄包装 |
| `Result` / CSharpFunctionalExtensions | 🟡 UseCase 返回 `Result` 或现有 fault 类型；不全局替换 throw |
| 无 `_` 私有字段 / 无 Async 后缀 | ❌ **不采纳**（与现有 PCL 代码风格冲突；保持仓库惯例） |
| 绑定优于 code-behind | ✅ 新页强制；老页迁移时改 |

---

## 4. Shell 与 Feature 模块契约（落地接口草稿）

```csharp
// PCL.Desktop/Shell
public interface IAppShell
{
    void ApplyExperimentalProfile(ExperimentalUiProfile profile);
    void ShowHint(string message, bool critical = false);
}

public sealed partial class AppShellViewModel : ObservableObject
{
    // Title layer, subpage back, window commands
}

public sealed partial class ExtraDockViewModel : ObservableObject
{
    // BackToTop / Task / Shutdown / Log visibility
    // Subscribes GameRunningChanged + TaskProgress + ScrollOffset
}

// PCL.Desktop/Features
public interface IDesktopFeatureModule
{
    string Id { get; }
    IReadOnlyList<NavigationRouteId> Routes { get; }
    void Register(IServiceCollection services);
    DesktopMainPage CreateMainPage(IServiceProvider sp);
    bool TryCreateSubPage(string subPageId, object? arg, IServiceProvider sp, out Control? page);
}
```

**Experimental 仅 Profile：**

```csharp
public sealed record ExperimentalUiProfile(
    bool HomepageUi,
    ChromeStyle Chrome,          // Classic | Glass
    LaunchHomeLayout LaunchHome, // Split | FullPage
    InstanceSelectLayout Select  // LeftRight | FullPageSidebar);
```

业务 Store **不**读 View 类型，只读 Profile 布尔/枚举。

---

## 5. 插件架构对齐（`dotnet-desktop-plugin-architect` + 现网）

现有 `PCL.Plugin` 已是完整插件平台（签名、ALC、能力、UI patch、Safe Mode）。补充计划**不重做插件**，只规定与 Shell 的边界：

### 5.1 边界

| 层 | 插件可见 |
|----|----------|
| Host 契约 (`pcl.*` services, UI slots) | ✅ |
| Session Stores 原始类型 | ❌（经 Host 查询/命令） |
| AppShell 内部控件树 | ❌（仅 `IPluginHostUiComposition`） |
| Application UseCases | 经 Host 暴露的 stable API |

### 5.2 壳迁移时的插件不变量

1. `DesktopHostUiComposition` / Navigation / Notifications 入口路径不变  
2. Safe Mode 仍可跳过高风险 UI patch  
3. 插件初始化 **bulkhead**：单插件失败不阻断 Shell（目标：catch + `plugin.init.fail` 计数；与 system-designer 失败表一致）  
4. 实验 UI 槽位 ID 稳定；改槽位走版本化 Host API（`PclHostApi` 0.3 → 0.4 需文档）

### 5.3 推荐演进（非阻塞）

- 插件侧继续 SDK 契约；Desktop 仅 Host 适配器  
- UI Patch 与 MVVM 并存：Patch 作用在 **已物化 Visual**；新页优先 slot 注入而非 replace 整页

---

## 6. 后端 skill 在桌面的映射（`engineering-backend-architect`）

| 后端概念 | 桌面映射 |
|----------|----------|
| 模块化单体 | Feature modules + 清晰 DI 边界 |
| Circuit breaker | 镜像源 / 下载源切换；JvmHost 失败建议关实验 |
| Expand-migrate-contract | 设置 JSON 键迁移；文件夹列表 schema |
| 幂等 | 任务 Id、启动防重入 |
| Health | 启动自检：设置可读、插件目录可写、Java 探测 |
| 不拆微服务 | **明确禁止** 为“干净”拆多进程 |

`PCL.Server` 仍独立；本计划不把启动器做成客户端-服务端强耦合。

---

## 7. 修订后的迁移阶段（在基线 Phase 上加密）

### Phase 0 — 契约与依赖（0.5 周）

- [ ] 引入 `CommunityToolkit.Mvvm` + DI 组合根草图（可编译）  
- [ ] 定义 Message 类型与 `IDesktopFeatureModule`  
- [ ] 架构测试草案：`ViewModels` 目录禁止 `using Avalonia`  
- [ ] 更新本目录 ADR 索引  

### Phase 1 — Shell MVVM（1–2 周）★ 最高优先级

1. `ExtraDockViewModel` + `TitleBarViewModel` + `ExperimentalUiProfile`  
2. 从 MainWindow 迁出 chrome / FAB / 空 dock 逻辑  
3. `GameRunningChanged` / `TaskProgress` 经 Messenger 或 Store 订阅  
4. **行为零回归**：关游戏、日志、回顶、任务 FAB  

### Phase 2 — Session Stores + DI 单例（1 周）

1. `MinecraftFolderStore` / `InstanceSelectionStore` / `TaskSessionStore` / `GameSessionStore`  
2. 设置持久化只经 Store  
3. 选择版本页只消费 Store  

### Phase 3 — Instances + Launch Feature 模块（2–3 周）

1. 整页/经典 select 作为 **同一 VM + 不同 View**（Profile 切换）  
2. Launch 主页实验/经典同理  
3. UseCase：`SelectFolder` / `StartMinecraft` 门面  
4. 列表刷新采用 Refreshable 薄模式（可不用 DynamicData）  

### Phase 4 — Downloads / Tasks / Settings / Community（2–4 周）

- 每路由一个 FeatureModule.Register  
- Settings binder 逐步改为 VM  

### Phase 5 — 收尾与可选 FRP

- MainWindow ≤ 500 行或删除  
- Domain 实体补齐  
- Headless 测试按 Feature 拆分  
- **可选：** 热点列表引入 DynamicData；评估 Zafiro 是否值得（默认 **否**，除非社区/长列表痛点实测）

---

## 8. ADR 草稿

### ADR-001 — 模块化单体，不拆微服务  
**状态：** Accepted（基线）  
**原因：** 单用户桌面、小团队；微服务运维成本无收益。

### ADR-002 — Experimental 仅为 Presentation Profile  
**状态：** Accepted  
**原因：** 双业务分叉是当前混乱主因。

### ADR-003 — 采用 CommunityToolkit.Mvvm + ME.DI + WeakReferenceMessenger  
**状态：** Proposed  
**备选：** ReactiveUI + DynamicData 全栈  
**否决全栈原因：** 与现有 code-behind 落差过大；迁移窗口过长。

### ADR-004 — 不强制 Zafiro  
**状态：** Proposed  
**采纳原则：** Pure VM、绑定优先；不采纳命名风格与强制 Result 全局化。

### ADR-005 — 插件边界冻结，Shell 适配  
**状态：** Accepted  
**原因：** `PCL.Plugin` 已成熟；壳重构不得破坏 Host ABI。

### ADR-006 — CQRS-lite 无 MediatR  
**状态：** Proposed  
**原因：** 用例数量与团队规模不够支撑消息中间件；命名分离即可。

### ADR-007 — MainWindow Strangler  
**状态：** Accepted  
**原因：** 9.2k 上帝对象；分阶段降低风险。

---

## 9. 目录目标态（补充）

```
PCL.Desktop/
  Composition/
    DesktopCompositionRoot.cs
  Shell/
    AppShellViewModel.cs
    TitleBarViewModel.cs
    ExtraDockViewModel.cs
    ExperimentalUiProfile.cs
    Views/   # 可选：壳控件
  Navigation/
    NavigationHost.cs
  Session/
    MinecraftFolderStore.cs
    …
  Messaging/
    NavigateRequestMessage.cs
    HintMessage.cs
    …
  Features/
    Launching/
      LaunchFeatureModule.cs
      ViewModels/
      Views/
    Instances/
      …
  Views/          # 过渡期 MainWindow
  Hosting/        # 插件桥（保持）
  Controls/       # 遗留控件库
```

```
PCL.Application/
  UseCases/
    Instances/
      SelectMinecraftFolder.cs
      ListInstances.cs
    Launching/
      StartMinecraft.cs
```

---

## 10. 测试策略（随 MVVM）

| 层级 | 工具 | 内容 |
|------|------|------|
| Store / UseCase | MSTest | 无 Avalonia |
| ViewModel | MSTest + Messenger | 命令、可见性、Profile |
| View | Headless（现有） | 关键导航与选择流；逐步变薄 |
| 架构 | DesktopArchitectureTests | VM 无 Avalonia；分层引用 |

目标：把 `AvaloniaHeadlessTests.cs`（~10k）按 Feature 拆文件，避免单测上帝文件。

---

## 11. 风险

| 风险 | 缓解 |
|------|------|
| 同时上 MVVM + 抽 Shell 范围爆炸 | Phase 1 只动 Shell VM，Feature 仍可先 code-behind 适配 |
| 双轨状态（Store vs MainWindow 字段） | Phase 2 完成后删除字段；过渡期单一写入点 |
| 插件 UI patch 与 MVVM 冲突 | 保持 composition host；新页优先 slot |
| 引入 DynamicData 过早 | Phase 5 可选，需列表性能数据 |
| 风格 skill（无 `_`）与仓库冲突 | 明确不采纳 |

---

## 12. 建议执行顺序（给你勾选）

1. **批准 ADR-003/004/006**（工具链）  
2. **Phase 0** 组合根 + Message 类型  
3. **Phase 1** Shell MVVM（立刻消灭空 dock / chrome 混乱源）  
4. Phase 2 Stores  
5. Phase 3 Launch + Instances  

---

## 13. 与基线文档关系

| 基线章节 | 本补充增量 |
|----------|------------|
| Shell / Stores / Features | 加上 **VM + DI + Messenger** 实现策略 |
| Experimental Profile | 不变，绑定到 Shell VM |
| 插件 | 明确 **ABI 冻结 + bulkhead** |
| 迁移 Phase | 插入 Phase 0 工具链；Phase 1 改为 Shell **MVVM** |
| Domain | CQRS-lite + 实体清单 |

**一句话：**  
系统层继续模块化单体 Strangler；表示层用 **CommunityToolkit.Mvvm + DI + Messenger** 落地；Zafiro 只借原则不借整栈；插件平台保持现设计，壳去适配而非重写。
