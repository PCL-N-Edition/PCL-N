# PCL N 架构补充计划（多 Skill 综合）

> **Status:** Proposed（工具链 skill **已就绪**，实现未开工）  
> **Date:** 2026-07-20  
> **Updated:** 2026-07-20（安装 architecture + mvvm-toolkit* 后按 skill 正文校准）  
> **Supplements:** [2026-07-20-modular-shell-replan.md](./2026-07-20-modular-shell-replan.md)

---

## 0. Skill 矩阵与适用边界

| Skill | 本仓库适用结论 | 纳入计划的要点 |
|-------|----------------|----------------|
| `engineering-system-designer` | ✅ 主线 | 需求/容量/故障/监控/Strangler |
| `architecture` | ✅ 主线（**已安装**） | 默认模块化单体；DDD/CQRS **仅在规则真复杂时**；依赖向内；反过度工程 |
| `avalonia-zafiro-development` | ⚠️ 原则可用，整栈不直接落地 | Pure MVVM、绑定优先；不强制 Zafiro/ReactiveUI/DynamicData |
| `mvvm-toolkit` | ✅ 采用（**已安装**） | `partial` + `[ObservableProperty]` / `[RelayCommand]`；基类选型 |
| `mvvm-toolkit-di` | ✅ 采用（**已安装**） | 单次组合根；Singleton/Transient；构造注入；禁滥用 `Ioc.Default` |
| `mvvm-toolkit-messenger` | ✅ 采用（**已安装**） | `WeakReferenceMessenger`；`ObservableRecipient` + `IsActive`；static 注册 lambda |
| `engineering-backend-architect` | ⚠️ 桌面适配 | bulkhead、设置 expand-migrate-contract、禁止微服务 |
| `dotnet-desktop-plugin-architect` | ✅ 对齐 `PCL.Plugin` | Host ABI 冻结；壳适配不重写插件 |

### 0.1 本机 skill 可用性

| Skill | 状态 | 路径（示例） |
|-------|------|----------------|
| engineering-system-designer | 已安装 | `~\.agents\skills\engineering-system-designer` |
| engineering-backend-architect | 已安装 | `~\.agents\skills\engineering-backend-architect` |
| avalonia-zafiro-development | 已安装 | `~\.agents\skills\avalonia-zafiro-development` |
| **architecture** | **已安装** | `~\.agents\skills\architecture`、`~\.codex\skills\architecture` |
| **mvvm-toolkit** | **已安装** | `~\.agents` / `~\.codex` / `~\.grok\skills\mvvm-toolkit` |
| **mvvm-toolkit-di** | **已安装** | 同上 `-di` |
| **mvvm-toolkit-messenger** | **已安装** | 同上 `-messenger` |
| dotnet-desktop-plugin-architect | 未单独包 | 以 `PCL.Plugin/README.md` + Host 契约为准 |

安装方式见 [README.md](./README.md#安装记录2026-07-20)。

---

## 1. 总原则（冲突裁决）

裁决顺序：

1. **不破坏现有分层与插件 ABI**（`PclHostApi`、`.pnp`、架构测试禁令）  
2. **模块化单体 + Strangler**（system-designer / architecture / backend-architect）  
3. **ViewModel 无 Avalonia 类型**（zafiro + 可测性）  
4. **渐进 MVVM**（toolkit 三件套）：先 Shell/新页，再老页  
5. **Zafiro / DynamicData / CSharpFunctionalExtensions**：非 Phase 0–2 阻塞项  

> 现状：`MainWindow` 为 code-behind 上帝对象，**零** `CommunityToolkit.Mvvm` / `ReactiveUI` / `DynamicData`。采用 **双速道**：壳与新 Feature 用 MVVM+DI；旧页 Strangler 迁出。

**`architecture` skill 额外约束（防过工程）：**

- 默认 **modular monolith**；不为“干净”拆微服务  
- DDD 聚合 / 完整 CQRS 管道 **仅**在业务规则真复杂时引入（启动/实例状态适合轻量实体 + UseCase，不是 Order 式聚合）  
- 不新增无 ownership 的空项目；现有 `Domain` / `Application` / `Desktop` 边界优先复用  

---

## 2. 目标架构（.NET 模块化单体 + 桌面壳）

### 2.1 逻辑分层

```
┌──────────────────────────────────────────────────────────────┐
│ Presentation (PCL.Desktop)                                    │
│  Shell VMs │ Feature VMs │ Views (AXAML) │ ValueConverters    │
│  IMessenger · ExperimentalUiProfile · NavigationHost         │
└─────────────────────────────┬────────────────────────────────┘
                              │ → Application + UI.Abstractions
┌─────────────────────────────▼────────────────────────────────┐
│ Application (PCL.Application)                                 │
│  UseCases (Command/Query 命名) │ Services │ Hosting registries│
└───────────────┬─────────────────────────────┬────────────────┘
                │                             │
     ┌──────────▼──────────┐       ┌──────────▼──────────┐
     │ Domain (PCL.Domain) │       │ Platform.Abstractions│
     └─────────────────────┘       └──────────┬──────────┘
                                   ┌──────────▼──────────┐
                                   │ Platform / Portable │
                                   └─────────────────────┘
```

**依赖规则（硬）：**

| 从 → 到 | 允许 |
|---------|------|
| Desktop → Application | ✅ |
| Desktop → Domain | ⚠️ 优先 Application DTO；Domain 仅展示值对象 |
| Application → Avalonia | ❌ |
| Domain → UI / 框架 IO | ❌ |
| Plugin payload → Desktop 内部类型 | ❌（只经 Host 契约） |

### 2.2 CQRS-lite（不上 MediatR）

与 `architecture`「CRUD 不硬套 CQRS」一致：

| 类型 | 例子 |
|------|------|
| Query | `ListInstances(root)`、`GetSelectedFolder` |
| Command | `SelectFolder`、`StartMinecraft`、`RemoveFolder` |
| Projection | Task 列表 → FAB 进度 |

禁止 View code-behind 拼装多服务；统一 UseCase / Facade。

### 2.3 DDD 轻量清单

| 概念 | 建议 | 来源 |
|------|------|------|
| `MinecraftRoot` | record + Kind | FolderStore |
| `GameInstanceId` | 强类型 | InstanceSelection |
| `LaunchSession` | 运行会话 VO | GameSessionStore |
| `DownloadTaskId` | 任务 id | TaskStore |
| `ExperimentalFeatureFlags` | flags VO | Settings |

---

## 3. 表示层：MVVM Toolkit + DI + Messenger

### 3.1 包（`mvvm-toolkit`）

```xml
<PackageReference Include="CommunityToolkit.Mvvm" Version="8.*" />
```

实现硬性约定（来自 skill）：

| 规则 | 说明 |
|------|------|
| `partial` class | 使用源生成器的类型必须 `partial` |
| 字段命名 | `[ObservableProperty] private string? name` / `_name`（**允许 `_`，与仓库一致**） |
| 异步命令 | `[RelayCommand]` 方法返回 `Task`，禁止 `async void` |
| 基类 | 默认 `ObservableObject`；收发消息用 `ObservableRecipient`；表单校验用 `ObservableValidator` |

```csharp
public sealed partial class ExtraDockViewModel : ObservableRecipient
{
    [ObservableProperty]
    private bool showBackToTop;

    [ObservableProperty]
    private bool showShutdown;

    [RelayCommand]
    private void BackToTop() { /* shell scroll */ }

    [RelayCommand]
    private async Task ShutdownGameAsync() { /* confirm + kill */ }
}
```

### 3.2 组合根（`mvvm-toolkit-di`）

**推荐：** Avalonia `App` 内构建一次 `IServiceProvider`（Generic Host 可选；Desktop 可先 `ServiceCollection` 以少依赖）。

```csharp
// DesktopCompositionRoot / App
services.AddSingleton<IMessenger>(WeakReferenceMessenger.Default);

// Session — app lifetime
services.AddSingleton<MinecraftFolderStore>();
services.AddSingleton<InstanceSelectionStore>();
services.AddSingleton<TaskSessionStore>();
services.AddSingleton<GameSessionStore>();
services.AddSingleton<ExperimentalUiProfileSource>();

// Shell — singleton
services.AddSingleton<AppShellViewModel>();
services.AddSingleton<TitleBarViewModel>();
services.AddSingleton<ExtraDockViewModel>();

// Feature pages — transient（每导航新实例，避免串状态）
services.AddTransient<InstanceSelectViewModel>();
// facades / use-cases — singleton
services.AddSingleton<IStartMinecraftUseCase, StartMinecraftUseCase>();
```

**生命周期表（skill）：**

| Lifetime | 用途 |
|----------|------|
| Singleton | Shell VM、Stores、`IMessenger`、设置、Application 服务 |
| Transient | 每页 / 每文档 ViewModel |
| Scoped | 桌面少用；多窗口时可 per-window scope |

**禁止：**

- ViewModel 内 `Ioc.Default.GetService<T>()`（隐藏依赖）  
- 多次 `BuildServiceProvider()`  
- 一切皆 Singleton 导致页状态串台  

**View 解析：** 导航宿主 `sp.GetRequiredService<TViewModel>()`，再设 `DataContext`；code-behind 不 `new` 业务 VM。

与现有 `IPclHost.Services`：插件 Host 的 `IServiceProvider` **可并存**；Desktop 组合根可注册 Host 为单例适配器，避免双容器各建一份业务服务（实现阶段需统一「权威容器」——建议 Desktop root 为主，Host 暴露子集）。

### 3.3 Messenger（`mvvm-toolkit-messenger`）

默认 **`WeakReferenceMessenger`**；Shell / Feature 注入同一 `IMessenger`。

**注册风格（skill 推荐）：**

```csharp
// 在 ObservableRecipient.OnActivated 或显式 Register 时：
Messenger.Register<ExtraDockViewModel, GameRunningChangedMessage>(
    this,
    static (r, m) => r.ShowShutdown = m.Value);
```

- lambda 用 **`static`**，经 `recipient` 访问实例，禁止捕获 `this`  
- 需要消息的 VM 继承 **`ObservableRecipient`**，页面 **进入 `IsActive = true` / 离开 `false`**（自动 RegisterAll / UnregisterAll）  
- UI 线程：后台线程 `Send` 后，handler 内用 Avalonia `Dispatcher` 切回 UI（VM 层可注入 `IUiScheduler`）

**消息表：**

| Message | 类型建议 | 发送方 | 接收方 |
|---------|----------|--------|--------|
| `NavigateRequestMessage` | record | Feature | NavigationHost |
| `TitleSubPageMessage` | record (Title, OnBack?) | Feature | TitleBar VM |
| `HintMessage` | record | 任意 | Shell |
| `FolderSelectionChangedMessage` | `ValueChangedMessage<string?>` 或 record | FolderStore | Launch/Instances |
| `GameRunningChangedMessage` | `ValueChangedMessage<bool>` | GameSessionStore | ExtraDock |
| `TaskProgressChangedMessage` | record | TaskStore | ExtraDock / Tasks |
| `ExperimentalProfileChangedMessage` | record | Settings | Shell + Features |

**可选：** `RequestMessage<T>` 做同步询问（如「当前选中实例」）；优先仍读 Store，避免隐式请求图。

**约定：**

- Store 对内 INPC；跨模块广播用 Messenger  
- 命令类消息单向（Feature → Shell）  
- 测试注入独立 `WeakReferenceMessenger` 实例  

### 3.4 Avalonia / Zafiro 务实映射

| Zafiro / FRP | PCL N |
|--------------|-------|
| Pure ViewModel | ✅ Phase 1 起新代码强制 |
| DynamicData | 🟡 Phase 5 可选 |
| Result 全局 | 🟡 UseCase 可用；不全盘替换 |
| 禁 `_` 字段 | ❌ **不采纳**（仓库 + toolkit 均允许 `_name`） |
| 绑定优先 | ✅ 新页强制 |

---

## 4. Shell 与 Feature 模块契约

```csharp
public interface IAppShell
{
    void ApplyExperimentalProfile(ExperimentalUiProfile profile);
    void ShowHint(string message, bool critical = false);
}

public sealed partial class AppShellViewModel : ObservableRecipient { /* title + chrome */ }

public sealed partial class ExtraDockViewModel : ObservableRecipient
{
    // BackToTop / Task / Shutdown / Log — IsActive 生命周期
}

public interface IDesktopFeatureModule
{
    string Id { get; }
    IReadOnlyList<NavigationRouteId> Routes { get; }
    void Register(IServiceCollection services);
    DesktopMainPage CreateMainPage(IServiceProvider sp);
    bool TryCreateSubPage(string subPageId, object? arg, IServiceProvider sp, out Control? page);
}

public sealed record ExperimentalUiProfile(
    bool HomepageUi,
    ChromeStyle Chrome,          // Classic | Glass
    LaunchHomeLayout LaunchHome, // Split | FullPage
    InstanceSelectLayout Select  // LeftRight | FullPageSidebar);
```

业务 Store **不**依赖 View 类型。

---

## 5. 插件架构对齐

现有 `PCL.Plugin` 已完整（签名、ALC、能力、UI patch、Safe Mode）。本计划**不重做插件**。

| 层 | 插件可见 |
|----|----------|
| Host `pcl.*` / UI slots | ✅ |
| Session Stores 原始类型 | ❌（经 Host） |
| AppShell 控件树 | ❌（`IPluginHostUiComposition`） |

**不变量：** composition / navigation / notifications 入口稳定；Safe Mode；插件 init bulkhead；槽位 ID 变更走 `PclHostApi` 版本。

---

## 6. 后端 skill 桌面映射

| 后端概念 | 桌面 |
|----------|------|
| 模块化单体 | Feature modules + DI |
| Circuit breaker | 镜像源；JvmHost 失败关实验 |
| Expand-migrate-contract | 设置 / 文件夹列表 schema |
| 幂等 | 任务 Id、启动防重入 |
| 禁止微服务 | 明确 |

---

## 7. 迁移阶段

### Phase 0 — 契约与依赖（0.5 周）

- [x] `PCL.Desktop` 引用 `CommunityToolkit.Mvvm` + `Microsoft.Extensions.DependencyInjection`  
- [x] `DesktopCompositionRoot`：`ServiceCollection` + `IMessenger` 单例  
- [x] `Messaging/ShellMessages.cs`  
- [x] `IDesktopFeatureModule` + 空模块列表可编译  
- [x] 测试：`DesktopCompositionTests`（Shell/Messaging/Composition 禁止 `using Avalonia`）  
- [x] ADR-003 Accepted；App 启动调用 `DesktopCompositionRoot.Initialize()`  

### Phase 1 — Shell MVVM（1–2 周）★

1. [x] `ExtraDockViewModel` / `TitleBarViewModel` / `ExperimentalUiProfileSource` / `AppShellViewModel`  
2. [x] `ObservableRecipient` + 构造时 `IsActive = true`（壳级常驻）  
3. [x] MainWindow chrome / FAB 可见性经 Shell VM（控件绘制仍在 Window；继续瘦身）  
4. [ ] 回归：关游戏、日志、回顶、任务 FAB、实验开关（手动/Headless）  
5. [ ] 进一步：Title 子页动画仅读 TitleBarViewModel；减少 MainWindow 字段  

### Phase 2 — Session Stores

1. [x] `MinecraftFolderStore` / `InstanceSelectionStore` / `TaskSessionStore` / `GameSessionStore` 注册为 Singleton  
2. [x] 文件夹列表与选中 root 持久化只经 `MinecraftFolderStore`  
3. [x] 实例偏好路径经 `InstanceSelectionStore`  
4. [x] 任务快照 / 游戏运行态经 Task/Game stores + Messenger → ExtraDock  
5. [x] MainWindow 委托 Store（仍负责 UI 挂载与异步启动刷新）  
6. [ ] 选择版本页直接注入 Store（去掉 MainWindow 中转）— Phase 3 Feature 模块  

### Phase 3 — Instances + Launch Feature

1. [x] `InstancesSelectSurface` + `InstancesSelectBindings`：经典/整页布局由 Profile 驱动  
2. [x] `InstancesFeatureModule` / `LaunchFeatureModule` 注册进组合根  
3. [x] MainWindow `ApplyInstanceSelectPage` 委托 Surface；Launch 使用 `LaunchHomeProfileResolver`  
4. [x] `LaunchHomeSurface` + `LaunchHomeBindings` 创建启动主页  
5. [x] `StartMinecraftUseCase` 门面（Bind → MainWindow 重实现）  
6. [ ] 列表 Refreshable 薄模式（不必 DynamicData）  

### Phase 4 — Downloads / Tasks / Settings / Community

1. [x] `DownloadFeatureModule` + `DownloadFeatureSurface`  
2. [x] `SettingsFeatureModule` + `SettingsFeatureSurface`  
3. [ ] Tasks / Community Feature 模块  
4. [ ] 进一步从 MainWindow 删除遗留 Create* 私有页工厂

### Phase 5 — 瘦 MainWindow、拆 Headless、可选 DynamicData  

**DoD（每阶段）：** 编译 + 相关测试绿；实验开/关冒烟；不扩大业务分叉；可独立回滚。

---

## 8. ADR

| ID | 标题 | 状态 |
|----|------|------|
| ADR-001 | 模块化单体，不拆微服务 | **Accepted** |
| ADR-002 | Experimental 仅为 Presentation Profile | **Accepted** |
| ADR-003 | CommunityToolkit.Mvvm + ME.DI + WeakReferenceMessenger | **Accepted**（skill 已装，实现待 Phase 0） |
| ADR-004 | 不强制 Zafiro 整栈 | **Accepted** |
| ADR-005 | 插件边界冻结，Shell 适配 | **Accepted** |
| ADR-006 | CQRS-lite 无 MediatR | **Accepted** |
| ADR-007 | MainWindow Strangler | **Accepted** |

### ADR-003 细节（更新）

- 包：`CommunityToolkit.Mvvm` 8.x  
- Messenger：`WeakReferenceMessenger` + 注入 `IMessenger`  
- 收消息 VM：`ObservableRecipient` + 导航/挂载时 `IsActive`  
- 组合根：单次 build；Shell/Store Singleton；页 VM Transient  
- 明确拒绝：全局 `Ioc.Default` 作为主路径  

---

## 9. 目录目标态

```
PCL.Desktop/
  Composition/DesktopCompositionRoot.cs
  Shell/
    AppShellViewModel.cs
    TitleBarViewModel.cs
    ExtraDockViewModel.cs
    ExperimentalUiProfile.cs
  Navigation/NavigationHost.cs
  Session/{MinecraftFolder,InstanceSelection,TaskSession,GameSession}Store.cs
  Messaging/*.cs
  Features/<Area>/{*FeatureModule.cs,ViewModels/,Views/}
  Views/          # 过渡 MainWindow
  Hosting/        # 插件桥
  Controls/
```

```
PCL.Application/UseCases/{Instances,Launching}/...
```

---

## 10. 测试策略

| 层 | 方式 |
|----|------|
| Store / UseCase | MSTest，无 Avalonia |
| ViewModel | MSTest + 独立 `WeakReferenceMessenger` |
| View | Headless 薄集成 |
| 架构 | VM 无 Avalonia；分层引用 |

目标：拆分 `AvaloniaHeadlessTests.cs`（~10k）。

---

## 11. 风险

| 风险 | 缓解 |
|------|------|
| MVVM + 抽 Shell 范围爆炸 | Phase 1 只动 Shell |
| Store 与 MainWindow 双写 | Phase 2 单一写入点 |
| 插件 patch vs MVVM | 保持 composition host |
| Host.Services 与 Desktop 双容器 | Phase 0 定权威 root |
| DynamicData 过早 | Phase 5 可选 |

---

## 12. 建议下一步

1. ~~安装 skill~~ **完成**  
2. **Phase 0** 组合根 + Message + 包引用  
3. **Phase 1** Shell MVVM  

---

## 13. 与基线文档关系

| 基线 | 本补充 |
|------|--------|
| Shell / Stores / Features | **VM + DI + Messenger** 实现细则（对齐已装 skill） |
| Experimental Profile | 绑 Shell VM |
| 插件 | ABI 冻结 + bulkhead |
| Phase | Phase 0 工具链；Phase 1 Shell **MVVM** |

**一句话：**  
模块化单体 Strangler 不变；表示层按已安装的 **CommunityToolkit.Mvvm + DI + WeakReferenceMessenger** skill 落地；Zafiro 只借原则；插件平台保持现设计。
