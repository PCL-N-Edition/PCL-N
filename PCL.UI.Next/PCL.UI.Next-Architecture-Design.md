# PCL.UI.Next 架构设计

> **文档状态**：Architecture Draft / 可进入实现评审  
> **目标项目**：`PCL.UI.Next`  
> **适用范围**：PCL N 下一代桌面 UI Runtime  
> **当前阶段**：只冻结 UI 架构与运行时边界；暂不重写具体业务页面  
> **核心范式**：Declarative Authoring + Reactive ECS + Incremental Layout + Retained Rendering + Platform Backend

---

## 0. 摘要

`PCL.UI.Next` 是 PCL N 的下一代 UI Runtime。它不再以 Avalonia `Control`/`UserControl` 继承树、MVVM `INotifyPropertyChanged`、运行时 Binding 与回调式动画为核心，而采用：

- **声明式 Blueprint** 描述 UI；
- **Reactive ECS** 作为运行时数据模型；
- **Dirty/Event Driven** 的增量更新模型；
- **自研 Incremental Layout** 管理 UI 几何关系；
- **Retained Render Scene** 保存渲染状态；
- **Render Diff / Commit Batch** 将最小变更提交给后端；
- **Avalonia 退化为平台后端**，只处理窗口、输入、IME、剪贴板、无障碍、Native Host 与渲染 Surface；
- **Presentation State / Command** 与业务层交互；
- **Scope + Generation** 统一解决页面、动画、异步资源和过期回调的生命周期问题；
- **Virtualization、Animation、Focus、Popup、Navigation、Accessibility、DevTools** 均作为 Runtime 一级能力存在。

最终目标不是“在 Avalonia 上加一层 ECS”，而是构建一个独立的、后端可替换的 UI Engine：

```text
PCL Application
      │
      ▼
PCL.Presentation
      │
      ▼
PCL.UI.Next Authoring
      │
      ▼
Reactive ECS Runtime
      │
      ├─ Binding
      ├─ Style
      ├─ Layout
      ├─ Animation
      ├─ Input
      ├─ Virtualization
      ├─ Accessibility
      └─ Render Diff
      │
      ▼
Retained Render Scene
      │
      ▼
Backend
      │
      ├─ Avalonia
      ├─ Headless
      └─ Future Native/Skia/Vulkan backend
```

---

# 1. 背景

PCL N 当前 UI 主要依赖传统桌面 UI 架构：

- Avalonia Control Tree；
- AXAML / UserControl；
- StyledProperty；
- Binding / `INotifyPropertyChanged`；
- Dispatcher；
- 页面级回调与动画回调；
- `ScrollViewer + Panel`；
- 布局属性动画；
- 控件实例承担状态、行为、视觉与生命周期。

该模型在中小型桌面应用中足够成熟，但 PCL N 的交互和视觉需求具有明显的“游戏式 UI”特征：

- 页面切换动画多；
- 卡片与 Hover 动画多；
- 大型版本/模组/下载列表；
- 高频滚动；
- 部分页面数据量极大；
- 动态主题、图标和异步资源较多；
- 希望进一步降低 UI Thread 占用；
- 希望未来能够摆脱对具体 UI Framework 的架构绑定。

继续在旧模型上局部修补会造成：

```text
旧 Control/MVVM
    +
新 ECS Runtime
    +
旧 Animation
    +
新 Animation
    +
旧 Binding
    +
新 Binding
```

长期并存，增加认知负担与维护成本。

因此新架构采用独立项目：

```text
PCL.UI.Next
```

在新项目内彻底建立一致的新范式。旧 UI 与新 UI 可以在仓库开发期同时存在，但**新项目内部不允许新旧范式交叉**。

---

# 2. 设计目标

## 2.1 一级目标

### G1. 架构一致

`PCL.UI.Next` 内所有 UI 统一采用：

```text
Blueprint
→ Entity / Component
→ System
→ Render Scene
→ Backend
```

禁止在 Runtime 内部重新引入传统 Control inheritance 作为主要抽象。

### G2. 增量更新

静止 UI 应尽量做到：

```text
Idle CPU ≈ 0
Idle allocation = 0 B/frame
```

没有输入、状态变化、动画、滚动、资源完成事件时，Runtime 不主动保持 60 FPS tick。

### G3. 热路径数据导向

动画、Transform、Opacity、滚动、Hit Test 等高频路径采用：

- packed storage；
- sparse index；
- SoA；
- active set；
- 无反射；
- 无逐实体虚调用。

### G4. UI 与业务解耦

UI 不直接依赖：

```text
MinecraftService
DownloadService
AccountService
SettingsService
PluginService
```

只通过：

```text
Presentation State
UiCommand
State Patch
```

交互。

### G5. 后端可替换

`PCL.UI.Next` Runtime 不引用：

```text
Avalonia.Controls
Avalonia.Media
AvaloniaObject
StyledProperty
Dispatcher.UIThread
```

Avalonia 仅作为 Backend。

### G6. 运行时可调试

从第一版就提供：

- Entity Inspector；
- Component Inspector；
- Dirty Trace；
- Layout Trace；
- Render Tree；
- Hit Test 可视化；
- Active Animation；
- Virtualization Inspector；
- Frame Timeline。

---

# 3. 非目标

本阶段明确不做以下事项：

## NG1. 不重写 PCL 页面

当前阶段只实现 Runtime、Backend、Playground、Tests、Benchmarks。

## NG2. 不 ECS 化业务 Domain

以下仍然属于普通业务架构：

```text
Minecraft 管理
下载
账号
设置
网络
插件
云功能
版本解析
启动逻辑
```

## NG3. 不从零实现 Unicode 文本栈

文本 shaping、字体 fallback、复杂脚本、IME 等优先复用成熟实现。

## NG4. 不在第一版自研 TextBox

TextBox、PasswordBox 等复杂输入控件第一阶段使用 `NativeControlHost`。

## NG5. 不一开始追求 Unity DOTS 级别复杂 Archetype/Job Scheduler

PCL UI 的实体规模与访问模型不需要如此重型的 ECS。

---

# 4. 总体架构

```text
┌───────────────────────────────────────────────────────────────┐
│                         PCL.Core                              │
│ Minecraft / Download / Account / Settings / Plugin / Network │
└──────────────────────────────┬────────────────────────────────┘
                               │
                               ▼
┌───────────────────────────────────────────────────────────────┐
│                     PCL.Presentation                          │
│                                                               │
│  AppState / PageState / Selectors / Commands / Effects        │
└──────────────────────────────┬────────────────────────────────┘
                               │ State Patch / UiCommand
                               ▼
┌───────────────────────────────────────────────────────────────┐
│                        PCL.UI.Next                            │
│                                                               │
│  ┌──────────────── Authoring ──────────────────────────────┐   │
│  │ Ui / Blueprint / Template / Binding / Widget DSL       │   │
│  └────────────────────────┬────────────────────────────────┘   │
│                           │                                   │
│  ┌────────────────────────▼────────────────────────────────┐   │
│  │                  Reactive ECS Runtime                  │   │
│  │ Entity / Component / Hierarchy / Scope / Dirty        │   │
│  └────────────────────────┬────────────────────────────────┘   │
│                           │                                   │
│  ┌────────────────────────▼────────────────────────────────┐   │
│  │                       Systems                          │   │
│  │ Input / Binding / Style / Layout / Animation          │   │
│  │ Virtualization / Focus / Text / A11y / Render Diff    │   │
│  └────────────────────────┬────────────────────────────────┘   │
│                           │                                   │
│  ┌────────────────────────▼────────────────────────────────┐   │
│  │                Retained Render Scene                   │   │
│  └────────────────────────┬────────────────────────────────┘   │
└───────────────────────────┼───────────────────────────────────┘
                            │ UiCommitBatch
                            ▼
┌───────────────────────────────────────────────────────────────┐
│                   Backend.Avalonia                            │
│ Window / Surface / Input / IME / Clipboard / Native Host      │
└───────────────────────────────────────────────────────────────┘
```

---

# 5. 项目拆分

建议仓库结构：

```text
src/
├─ PCL.Core/
├─ PCL.Presentation/
│
├─ UI.Next/
│  ├─ PCL.UI.Next.Abstractions/
│  ├─ PCL.UI.Next.Runtime/
│  ├─ PCL.UI.Next.Authoring/
│  ├─ PCL.UI.Next.Layout/
│  ├─ PCL.UI.Next.Style/
│  ├─ PCL.UI.Next.Animation/
│  ├─ PCL.UI.Next.Input/
│  ├─ PCL.UI.Next.Text/
│  ├─ PCL.UI.Next.Rendering/
│  ├─ PCL.UI.Next.Accessibility/
│  ├─ PCL.UI.Next.Backend.Avalonia/
│  ├─ PCL.UI.Next.Backend.Headless/
│  └─ PCL.UI.Next.DevTools/
│
├─ PCL.Desktop/
│
tests/
├─ PCL.UI.Next.Tests/
├─ PCL.UI.Next.Rendering.Tests/
├─ PCL.UI.Next.Layout.Tests/
└─ PCL.UI.Next.Benchmarks/
│
playground/
└─ PCL.UI.Next.Playground/
```

---

# 6. 依赖规则

## 6.1 允许的依赖

```text
PCL.UI.Next.Authoring
        ↓
PCL.UI.Next.Runtime
        ↓
PCL.UI.Next.Abstractions
```

```text
PCL.UI.Next.Layout
PCL.UI.Next.Style
PCL.UI.Next.Animation
PCL.UI.Next.Input
PCL.UI.Next.Text
PCL.UI.Next.Accessibility
        ↓
PCL.UI.Next.Runtime / Abstractions
```

```text
PCL.UI.Next.Rendering
        ↓
PCL.UI.Next.Abstractions
```

```text
PCL.UI.Next.Backend.Avalonia
        ↓
PCL.UI.Next.Rendering
        ↓
PCL.UI.Next.Abstractions
```

## 6.2 禁止依赖

`Runtime` 禁止引用：

```text
Avalonia
PCL.Core
具体业务 Service
具体页面
```

页面层禁止引用：

```text
ComponentPool<T>
EntityRegistry
RenderScene
Avalonia Control
```

---

# 7. 核心术语

| 术语 | 含义 |
|---|---|
| Entity | UI Runtime 内的轻量标识 |
| Component | Entity 的数据状态 |
| System | 批量处理某类 Component 的逻辑 |
| Blueprint | UI 的声明式静态描述 |
| Template | 可实例化的 Blueprint 子图 |
| UiScope | 生命周期域 |
| Dirty | 需要重新计算的局部状态 |
| RenderNode | Retained Render Scene 节点 |
| NativeHost | 由平台 Backend 提供的原生控件 |
| Presentation State | UI 可消费的业务状态 |
| UiCommand | UI 发给业务层的命令 |
| Commit Batch | Runtime 提交到 Backend 的最小渲染变更集合 |

---

# 8. Entity

```csharp
public readonly struct UiEntity : IEquatable<UiEntity>
{
    public readonly int Index;
    public readonly uint Generation;
}
```

语义：

```text
Index
    = Entity slot

Generation
    = slot 生命周期版本
```

实体销毁：

```text
Entity(142, 31)
↓ destroy
Entity(142, 32)
```

旧的：

```text
Entity(142, 31)
```

自动失效。

## 8.1 Entity 设计原则

Entity：

- 不保存对象引用；
- 不保存业务状态；
- 不拥有行为；
- 不实现虚方法；
- 不暴露 mutable reference 给页面。

---

# 9. EntityRegistry

负责：

```text
Create
Destroy
Validate
Generation
Free list
Scope ownership
```

建议内部结构：

```text
Generation[]
AliveBitset
FreeIndices
ScopeId[]
```

创建：

```csharp
UiEntity entity = registry.Create(scope);
```

验证：

```csharp
bool alive = registry.IsAlive(entity);
```

所有异步结果、事件、动画在应用前必须验证 Entity / Scope generation。

---

# 10. Component Storage

基础方案：

> **Sparse Index + Packed Dense Array**

每类 Component 一个 `ComponentPool<T>`。

```text
Sparse[Entity.Index]
       ↓
DenseIndex
       ↓
DenseEntities[]
DenseComponents[]
```

例如：

```text
DenseEntities:
[Entity 12, Entity 91, Entity 142]

DenseComponents:
[Transform, Transform, Transform]
```

## 10.1 目标复杂度

```text
Has<T>      O(1)
Get<T>      O(1)
Add<T>      amortized O(1)
Remove<T>   O(1)
Enumerate   O(component count)
```

## 10.2 Component 规则

优先使用：

```text
struct
enum
handle
primitive
small fixed data
```

避免：

```text
object
delegate
closure
Dictionary
List
AvaloniaObject
```

大型动态数据使用 Handle 引用独立 Store。

---

# 11. 为什么不使用纯 Archetype ECS

UI 的状态结构变化频繁：

```text
Hovered 添加/移除
Popup 创建/销毁
VirtualList 回收
Tooltip 出现
Focus 变化
Transition 状态变化
```

严格 Archetype 会导致大量结构迁移。

因此第一版采用：

```text
Entity Registry
+
Sparse Packed Pools
+
Hot Path SoA Store
+
Dirty Active Sets
```

仅对热点组件单独优化。

---

# 12. Hot Path Storage

以下不一定使用普通 `ComponentPool<T>`：

```text
Animation
Transform
Opacity
ScrollState
HitTest bounds
Render mutation
```

可以独立为：

```text
Entity[]
X[]
Y[]
Scale[]
Opacity[]
```

即 SoA。

目标：

- cache locality；
- SIMD 友好；
- 连续访问；
- 避免虚调用。

---

# 13. HierarchyStore

UI 的树结构是核心数据结构，不应仅作为普通 `Parent` Component。

```csharp
struct HierarchyNode
{
    UiEntity Parent;

    UiEntity FirstChild;
    UiEntity LastChild;

    UiEntity PreviousSibling;
    UiEntity NextSibling;

    ushort Depth;
}
```

`HierarchyStore` 负责：

```text
AttachChild
Detach
Move
DestroySubtree
EnumerateChildren
EnumerateAncestors
Depth
StructuralVersion
```

---

# 14. 多树模型

Runtime 不应假设“UI 只有一棵树”。

至少存在：

## 14.1 Entity Hierarchy

逻辑 UI 结构。

## 14.2 Layout Tree

用于 Measure / Arrange。

部分 Entity 可以不参与布局。

## 14.3 Render Tree

一个 Entity 可以对应多个 RenderNode。

## 14.4 Semantic Tree

无障碍结构。

## 14.5 Hit Test Tree

用于快速 Pointer Target 查找。

## 14.6 Focus Tree

用于焦点导航。

## 14.7 Input Root

`Input Root` 表示一个独立 Window 或 Input Surface，使用 generation-safe 的
`UiInputRootId` 标识。它必须由 Window/Input Surface 对应的 Scope 显式注册，
不能通过寻找最顶层 `UiScope` 隐式推断。

```text
ApplicationScope
├─ WindowScope A  ← InputRoot A
│  └─ PageScope
└─ WindowScope B  ← InputRoot B
   └─ PageScope
```

一个 Runtime 可以同时拥有多个 Input Root。Input 状态必须按 Input Root 隔离：

```text
Focus:           InputRoot → FocusedEntity
PointerCapture: (InputRoot, PointerId) → Entity
Hover:          (InputRoot, PointerId) → Entity
Pressed:        (InputRoot, PointerId) → Entity
Gesture:        (InputRoot, PointerId) → Session
```

普通 Page/Popup Scope 解析到最近的已注册 Input Root；ApplicationScope 不会自动成为
所有 Window 的共享焦点根或 Pointer 状态根。

因此：

```text
Entity
≠ Widget
≠ LayoutNode
≠ RenderNode
≠ AccessibilityNode
```

---

# 15. UiScope

`UiScope` 是新 Runtime 中最重要的生命周期机制之一。

```csharp
public readonly struct UiScopeId
{
    public readonly int Index;
    public readonly uint Generation;
}
```

典型层级：

```text
ApplicationScope
└─ WindowScope
   └─ PageScope
      ├─ PopupScope
      ├─ TooltipScope
      └─ AsyncResourceScope
```

Scope 拥有：

```text
Entity
Animation
Async request
Binding subscription
Resource request
Transition
NativeHost
```

Scope Dispose：

```text
Destroy owned entities
Cancel active animations
Invalidate pending async work
Detach bindings
Destroy native hosts
Release scoped resources
Discard stale state patches
```

---

# 16. Generation Safety

异步结果必须携带：

```text
ScopeId
ScopeGeneration
RequestId
RequestGeneration
```

消费：

```csharp
if (!scopeRegistry.IsAlive(result.Scope))
    return;

if (result.Generation != currentGeneration)
    return;
```

用于彻底消除：

```text
旧页面 callback
旧动画 callback
旧资源请求
旧网络结果
```

污染新状态的问题。

---

# 17. Component 分类

---

## 17.1 Geometry Components

```text
Transform2D
LayoutRect
DesiredSize
ClipRect
ZIndex
PixelSnap
```

示例：

```csharp
public struct Transform2D
{
    public float TranslateX;
    public float TranslateY;

    public float ScaleX;
    public float ScaleY;

    public float Rotation;

    public float OriginX;
    public float OriginY;
}
```

---

## 17.2 Layout Components

```text
LayoutNode
Width
Height
MinSize
MaxSize
Margin
Padding
HorizontalAlignment
VerticalAlignment

StackLayout
GridLayout
WrapLayout
OverlayLayout
AbsoluteLayout
```

不存在：

```text
StackPanel : Panel
Grid : Panel
Border : Decorator
```

只有：

```text
Entity + StackLayout
Entity + GridLayout
```

---

## 17.3 Visual Components

```text
Opacity
Background
Foreground
Border
CornerRadius
Shadow
Blur
ImageContent
IconContent
TextContent
Visibility
RenderLayerHint
```

---

## 17.4 Interaction Components

```text
HitTestable
Hoverable
Pressable
Clickable
Focusable
Selectable
Scrollable
Draggable
Droppable
```

---

## 17.5 Interaction State

推荐位掩码：

```csharp
[Flags]
public enum InteractionState : ushort
{
    None      = 0,
    Hovered   = 1 << 0,
    Pressed   = 1 << 1,
    Focused   = 1 << 2,
    Selected  = 1 << 3,
    Disabled  = 1 << 4,
    Checked   = 1 << 5,
    Expanded  = 1 << 6,
    Dragging  = 1 << 7
}
```

---

## 17.6 Semantic Components

```text
SemanticRole
AccessibleName
AccessibleDescription
AccessibleValue
AccessibleState
AccessibleAction
```

---

# 18. 页面不能直接操作 ECS

页面不得：

```csharp
var e = world.Create();
world.Add(e, new TextContent(...));
world.Add(e, new Background(...));
```

否则会产生大量低级 ECS 操作代码，失去声明式 UI 的可维护性。

页面只能通过：

```text
PCL.UI.Next.Authoring
```

构建 Blueprint。

---

# 19. Authoring Layer

Authoring API 的职责：

```text
声明 UI 结构
声明 Style Class
声明 Binding
声明 Command
声明行为
声明 Template
声明 Virtualization
```

示例：

```csharp
Ui.Column(
    gap: Theme.Spacing.Medium,

    Ui.Text()
        .Class(UiClass.PageTitle)
        .BindText(AppSelectors.Title),

    Ui.Button(
        Ui.Text("启动")
    )
    .Command(AppCommands.Launch)
)
```

这里不会创建 Avalonia Control，也不应直接创建运行时 Entity。

它生成：

```text
UiBlueprint
```

---

# 20. UiBlueprint

Blueprint 是：

> Entity Graph 的静态、可编译描述。

包含：

```text
Template Node
Static Components
Style Classes
Binding Program
Command Binding
Structural Conditions
Virtualization Metadata
```

示意：

```text
Blueprint #Home
├─ Node 0 Column
│  ├─ Node 1 Text
│  └─ Node 2 Button
│     └─ Node 3 Text
```

实例化时：

```text
Blueprint Node
↓
Runtime Entity
```

---

# 21. 不采用“每次状态变化重建完整 Virtual DOM”

不建议：

```text
State changed
↓
Build whole tree
↓
Allocate virtual nodes
↓
Diff whole tree
```

因为会引入：

- 临时对象；
- GC；
- 全树 diff；
- 状态映射成本。

采用：

# Compiled Blueprint + Reactive Binding

```text
Build once
↓
Compile
↓
Instantiate once
↓
State Patch
↓
Run dependent binding only
```

---

# 22. Blueprint Compiler

可采用：

```text
Source Generator
```

编译：

```csharp
Ui.Text()
    .BindText(s => s.UserName)
```

为：

```text
BindingId = 17
Dependency = UserState.UserName
Target = TemplateNode 31 / TextContent
Updater = GeneratedBinding_17
```

生成代码：

```csharp
static void UpdateBinding17(
    ref TextContent target,
    in UserState state)
{
    target.Value = state.UserName;
}
```

目标：

```text
无 reflection
无 PropertyChanged 字符串
无运行时表达式树解析
```

---

# 23. Structural Program

Blueprint 除属性 Binding 外，还应支持结构变化：

```text
Ui.If
Ui.Switch
Ui.ForEach
Ui.VirtualList
```

例如：

```csharp
Ui.If(
    AppSelectors.IsLoggedIn,
    whenTrue: LoggedInView(),
    whenFalse: LoginView()
)
```

编译为：

```text
StructuralBinding
↓
Condition version change
↓
Instantiate / detach corresponding template subtree
```

不是每帧重新 Build。

---

# 24. Presentation State

业务层与 UI ECS 之间必须存在 `PCL.Presentation`。

数据流：

```text
PCL.Core
↓
Presentation State
↓
Selectors
↓
PCL.UI.Next Binding
```

UI 不保存：

```text
DownloadService
MinecraftInstanceManager
AccountManager
```

---

# 25. Versioned State

建议 Presentation State 使用版本化 slice：

```csharp
public readonly struct Versioned<T>
{
    public readonly T Value;
    public readonly ulong Version;
}
```

每个 Selector 可以有独立 Version。

例如：

```text
AppState.Version             = 230
DownloadState.Version        = 81
ThemeState.Version           = 12
SelectedInstance.Version     = 19
```

Binding 只在自己依赖的 Version 变化时运行。

---

# 26. Selector

页面依赖：

```text
Selector<TState, TValue>
```

而不是直接订阅整个 AppState。

例如：

```text
AppSelectors.Title
AppSelectors.UserAvatar
DownloadSelectors.ActiveCount
InstanceSelectors.CurrentVersion
```

依赖图：

```text
Selector
↓
Dependent Binding IDs
```

---

# 27. UiCommand

UI 对业务只发送 Command。

```csharp
public readonly struct UiCommand
{
    public readonly CommandId Id;
    public readonly PayloadHandle Payload;
}
```

示例：

```text
LaunchMinecraft(instanceId)
OpenSettings
RetryDownload(taskId)
Navigate(PageId)
```

流程：

```text
Pointer
↓
InteractionSystem
↓
UiCommand
↓
Presentation Command Dispatcher
↓
Application / Domain
```

禁止：

```text
Button callback
↓
DownloadService.Start()
```

---

# 28. Effect / Async Model

异步业务：

```text
UiCommand
↓
Presentation
↓
Effect
↓
Domain async operation
↓
State Patch
↓
UI Binding
```

UI Entity 不持有：

```text
Task
CancellationTokenSource
业务 Service
```

UI Runtime 自己的资源异步除外。

---

# 29. Reactive ECS

这是整个系统最关键的运行模型。

传统游戏 ECS：

```text
60 FPS
↓
Every System
↓
Every Matching Entity
```

不适合 UI。

PCL UI 使用：

```text
Event/Dirty Driven
```

只有：

```text
发生输入
状态变化
布局变化
动画活动
滚动惯性
资源加载完成
窗口变化
```

才执行相关 System。

---

# 30. DirtyTracker

不能只有：

```csharp
bool Dirty;
```

定义细粒度 flags：

```csharp
[Flags]
public enum UiDirtyFlags : uint
{
    None          = 0,

    Binding       = 1 << 0,
    Structure     = 1 << 1,
    Style         = 1 << 2,

    TextMeasure   = 1 << 3,

    LayoutMeasure = 1 << 4,
    LayoutArrange = 1 << 5,

    Transform     = 1 << 6,
    Clip          = 1 << 7,

    HitTest       = 1 << 8,
    Render        = 1 << 9,

    Accessibility = 1 << 10,
}
```

DirtyTracker 维护：

```text
DirtyBindingSet
DirtyStyleSet
DirtyMeasureSet
DirtyArrangeSet
DirtyTransformSet
DirtyRenderSet
...
```

System 只处理对应集合。

---

# 31. Dirty 原因链

Debug Build 中 Dirty 应记录来源：

```text
Entity #481 TextContent changed
↓
TextMeasure dirty
↓
DesiredSize changed
↓
Parent #465 Measure dirty
↓
Parent #412 Measure dirty
```

DevTools 可输出：

```text
Entity #412 invalidated by:
#481 TextContent.Value
→ #465 DesiredSize
→ #412 GridLayout.Measure
```

---

# 32. Frame Scheduler

Runtime 有三个状态：

## 32.1 Idle

没有：

```text
Input
State Patch
Animation
Scroll inertia
Resource Ready
```

则 Runtime 完全不 tick。

## 32.2 Reactive Frame

以下事件请求一帧：

```text
Pointer event
Keyboard event
State patch
Window resize
Theme change
Resource ready
NativeHost update
```

## 32.3 Continuous Frame

仅当存在：

```text
ActiveAnimation
Scroll inertia
Caret blink
Video
Realtime visual effect
```

才持续 request frame。

---

# 33. Runtime Clock

所有动画和时间逻辑必须依赖：

```csharp
public interface IUiClock
{
    UiTimestamp Now { get; }
}
```

禁止 Runtime 内散落：

```text
DateTime.Now
Stopwatch.StartNew()
Environment.TickCount
```

测试时使用 Deterministic Clock。

---

# 34. System Pipeline

推荐固定、确定性的 pipeline：

```text
1. Drain Platform Events
2. Drain State Patches

3. Input Normalize
4. Hit Test
5. Interaction
6. Focus / Gesture / Shortcut

7. Binding Update
8. Structural Reconcile

9. Style Resolve
10. Virtualization Plan

11. Text / Image Measure

12. Layout Measure
13. Layout Arrange

14. Transition Planning
15. Animation Tick

16. Transform
17. Clip / HitTest Update

18. Accessibility Update

19. Render Diff
20. Backend Commit
```

第一版不做动态 DAG Scheduler。

原因：

- UI pipeline 天然有明确先后；
- deterministic 更容易调试；
- 性能分析更可控；
- 避免 scheduler 本身增加复杂度。

---

# 35. Layout Engine

`PCL.UI.Next.Layout` 自主管理布局，不使用 Avalonia Measure/Arrange 作为核心。

---

# 36. UiLength

```csharp
public enum UiLengthKind : byte
{
    Auto,
    Pixels,
    Percent,
    Star,
    MinContent,
    MaxContent
}
```

```csharp
public readonly struct UiLength
{
    public readonly UiLengthKind Kind;
    public readonly float Value;
}
```

---

# 37. Measure / Arrange

Measure：

```text
Children
↓
Desired Size
↓
Parent
```

Arrange：

```text
Parent Final Rect
↓
Children Final Rect
```

只处理 Dirty subtree。

---

# 38. Layout Boundary

Dirty propagation 不能无条件走到 Root。

例如：

```text
Root
└─ Sidebar Width=280
   └─ Text
```

Text 内容改变：

```text
Text DesiredSize changed
```

如果 Sidebar 已被固定 Width 约束吸收，则：

```text
Dirty propagation stops here
```

可以显式组件：

```text
LayoutBoundary
```

也可以由 Layout Engine 推断约束稳定性。

---

# 39. Layout Components

```text
StackLayout
GridLayout
WrapLayout
OverlayLayout
AbsoluteLayout
```

示例：

```csharp
public struct StackLayout
{
    public Orientation Orientation;
    public float Gap;
}
```

复杂数组数据不直接放 Component。

例如 Grid Tracks：

```text
GridTrackSetHandle
```

存储在：

```text
LayoutResourceStore
```

---

# 40. Grid

Grid Track 类型：

```text
Fixed
Auto
Star
```

支持：

```text
Min
Max
Span
Row
Column
```

推荐将 Grid definition 在 Blueprint Compile 阶段预解析为运行时紧凑结构。

---

# 41. Transform 与 Layout 强制分离

原则：

```text
Layout = 最终静态几何
Transform = 视觉变换
```

动画优先：

```text
Translate
Scale
Opacity
Clip
```

禁止高频：

```text
Width
Height
Margin
GridLength
```

逐帧变化。

---

# 42. Layout Animation

必须使用 FLIP 等方式避免重复布局：

```text
First
↓
Last
↓
Invert
↓
Play
```

流程：

```text
旧 LayoutRect
↓
计算新 LayoutRect 一次
↓
生成差异 Transform
↓
动画 Transform → Identity
```

---

# 43. Style Engine

Style 分为：

```text
Theme Tokens
Static Rules
Dynamic State Rules
Resolved Style
Transitions
```

---

# 44. Theme Tokens

页面不得硬编码：

```text
颜色
圆角
间距
字体大小
阴影
```

统一：

```text
Color.Accent
Color.Surface
Color.SurfaceHover
Color.TextPrimary

Spacing.Small
Spacing.Medium
Spacing.Large

Radius.Small
Radius.Card

Typography.Title
Typography.Body
```

---

# 45. Theme Version

Theme Token 每个可以独立拥有 Version。

例如：

```text
AccentColor.Version = 12
SurfaceColor.Version = 4
```

修改 Accent 时，只 Dirty 依赖 Accent 的实体。

不需要全 UI 重算。

---

# 46. Style Class

Widget/页面使用：

```text
UiClass.Button
UiClass.Card
UiClass.NavigationItem
UiClass.Title
```

Dynamic State：

```text
Normal
Hovered
Pressed
Focused
Selected
Disabled
```

Blueprint Compiler 可预计算静态 selector 匹配。

---

# 47. ResolvedStyle

```csharp
public struct ResolvedStyle
{
    public BrushHandle Background;
    public BrushHandle Foreground;

    public float Opacity;
    public float CornerRadius;

    public ShadowHandle Shadow;
    public TransitionSetHandle Transitions;
}
```

Selector 不应进入 Render Hot Path。

---

# 48. Animation Architecture

Animation 作为独立 Hot Store。

```text
Entity[]
Property[]
From[]
To[]
Elapsed[]
Duration[]
Easing[]
Generation[]
Scope[]
```

Animation System 仅遍历：

```text
ActiveAnimations
```

复杂度：

```text
O(active animations)
```

---

# 49. Declarative Transition

页面不写：

```text
AniStart
AnimateTo
OnCompleted
```

而是声明：

```text
Button:hover
    Background = HoverSurface
    Opacity = 0.92

Transition:
    Background 120ms
    Opacity 100ms
```

Style System 发现：

```text
Resolved target changed
```

Transition System 自动产生 Animation。

---

# 50. Animation Generation

每个 animatable property 拥有 generation：

```text
Entity #100
Opacity Generation = 31
```

新 transition：

```text
generation = 32
```

旧动画即便稍后结束，也无法覆盖新状态。

---

# 51. Animation Completion

禁止 callback。

完成后发布内部事件：

```text
AnimationCompleted
TransitionCompleted
```

由对应 System 消费。

例如：

```text
NavigationSystem
```

在页面退出动画完成后销毁 PageScope。

---

# 52. Animation Override Policy

需要冻结规则：

```text
Replace
ContinueFromCurrent
Queue
MergeVelocity
IgnoreIfSameTarget
```

默认 UI Transition：

```text
ContinueFromCurrent
```

即 Hover 快速进出时从当前视觉值继续，而不是跳回旧起点。

---

# 53. Input Backend

Avalonia 输入先标准化为：

```csharp
UiPointerEvent
UiKeyEvent
UiTextInputEvent
UiScrollEvent
UiWindowEvent
```

写入：

```text
PlatformEventQueue
```

Runtime 不处理 Avalonia 原生 EventArgs。

---

# 54. Hit Test

维护增量 Hit Test Structure。

每个可命中节点包含：

```text
Bounds
Transform
Clip
ZOrder
Flags
Entity
```

更新仅发生在：

```text
Layout
Transform
Clip
ZIndex
Visibility
```

变化时。

---

# 55. Hit Test 数据结构

初版可采用：

```text
按 Render Layer 分层
+
按 Z 逆序
+
局部 bounds tree
```

实体量增加后可考虑：

```text
BVH
R-Tree
Spatial Index
```

但第一版不必过度设计。

---

# 56. Routed Events

保留成熟 UI 的 routed event 语义：

```text
Capture
Target
Bubble
```

例如：

```text
Root
↓ capture
Card
↓
Button
↑ bubble
Card
↑
Root
```

区别是：

- 不调用 Control 虚方法；
- 不依赖对象事件订阅；
- 统一由 Interaction System 处理。
- 正常 dispatch 不为 handler snapshot 分配临时数组；
- dispatch 期间移除 handler 使用 tombstone，退出最外层 dispatch 后再 compact；
- dispatch 期间新增的 handler 不参与当前节点的本次调用。

---

# 57. Pointer Capture

Runtime 原生支持：

```text
PointerCapture
```

用于：

```text
Slider
Scrollbar
Drag
Window resize region
Custom gestures
```

---

# 58. Gesture System

识别：

```text
Click
DoubleClick
LongPress
Drag
Pan
Pinch
```

Gesture 与 Pointer Raw Event 解耦。

---

# 59. Focus System

Runtime 自主管理：

```text
Focusable
FocusScope
TabIndex
FocusState
FocusTrap
```

支持：

```text
Tab
Shift+Tab
Arrow Navigation
Dialog Focus Trap
Popup Focus Scope
Restore Previous Focus
```

Focus 必须维持以下 invariant：

```text
FocusedEntity is alive
∧ has Focusable
∧ is enabled
∧ is visible
∧ belongs to the owning InputRoot
```

当任一条件失效时，Focus System 在同帧清除 `Focused` 状态并派发 `LostFocus`；
键盘事件和默认激活行为不得再路由到该 Entity。

---

# 60. Shortcut System

快捷键统一注册：

```text
Shortcut
→ UiCommand
```

例如：

```text
Ctrl+F
Ctrl+R
Esc
Enter
F5
```

不允许各 Widget 散落监听 Keyboard。

---

# 61. Text Engine

定义抽象：

```csharp
public interface ITextEngine
{
    TextLayoutHandle Layout(in TextLayoutRequest request);
}
```

Runtime 只依赖：

```text
TextLayoutHandle
Glyph Metrics
Desired Size
```

不绑定具体实现。

---

# 62. Text Cache

Key：

```text
String identity
FontHandle
FontSize
FontWeight
Width constraint
Wrapping
Culture
Direction
Feature set
```

Value：

```text
TextLayoutHandle
DesiredSize
GlyphRuns
LineMetrics
```

仅 key 变化才重新 layout。

---

# 63. String Storage

高频静态文本可：

```text
StringHandle
```

动态文本支持：

```text
OwnedString
```

避免强制将所有动态文本 intern。

---

# 64. NativeControlHost

第一版下列控件不自研：

```text
TextBox
PasswordBox
复杂 IME 输入
WebView
部分 Native Picker
```

UI 逻辑上仍是：

```text
Entity
├─ LayoutNode
├─ Focusable
├─ TextInput
└─ NativeHost
```

Backend 创建实际 Avalonia Control，并将其作为 Overlay/Native Host 同步布局。

因此：

> NativeHost 是 Backend 实现细节，不代表新旧 UI 架构混用。

---

# 65. NativeHost Contract

```csharp
public interface INativeHostBackend
{
    NativeHostHandle Create(in NativeHostDescriptor descriptor);
    void Update(NativeHostHandle handle, in NativeHostMutation mutation);
    void Destroy(NativeHostHandle handle);
}
```

必须支持：

```text
Bounds
Visibility
Focus
Text
Selection
Enabled
Style bridge
```

---

# 66. Virtualization

Virtualization 必须是 Runtime 一级能力。

不设计：

```text
VirtualizingStackPanel
```

而设计：

```text
CollectionBinding
Virtualization
ScrollViewport
ItemTemplate
```

---

# 67. Virtualized Collection

```csharp
public struct Virtualization
{
    public float EstimatedItemExtent;
    public ushort OverscanBefore;
    public ushort OverscanAfter;
}
```

例如：

```text
Logical item count = 100000
Visible = 30
Overscan = 6 + 6
Realized ≈ 42 item subtree
```

---

# 68. Recycling

维护：

```text
TemplateInstancePool
```

item 离开 viewport：

```text
detach
↓
return to pool
↓
rebind to new logical item
```

避免频繁：

```text
Destroy subtree
Create subtree
```

---

# 69. Variable Height Virtualization

必须支持不同 item 高度。

维护：

```text
EstimatedExtent
MeasuredExtent
Offset Index
```

推荐：

```text
Fenwick Tree
```

实现：

```text
logical index → scroll offset
scroll offset → logical index
```

复杂度：

```text
O(log N)
```

---

# 70. Scroll System

```csharp
public struct ScrollState
{
    public float Offset;
    public float Velocity;
    public float Target;

    public float Extent;
    public float Viewport;
}
```

负责：

```text
Wheel
Trackpad
Touch
Inertia
Spring
Overscroll
Anchor
Programmatic Scroll
```

---

# 71. Scroll 不应触发全量 Layout

滚动主要改变：

```text
Viewport Transform
```

只有当 Virtualization realized range 变化时，才触发局部结构和布局更新。

---

# 72. Retained Render Scene

Runtime 不采用每帧完整：

```text
DrawRect
DrawText
DrawImage
```

而维护：

```text
RenderScene
```

示意：

```text
RenderRoot
├─ Layer
│  ├─ RoundedRect
│  ├─ TextRun
│  └─ Image
└─ OverlayLayer
```

---

# 73. RenderNode

类型例如：

```text
Layer
Rectangle
RoundedRectangle
Text
Image
Vector
Clip
Effect
NativeHostPlaceholder
```

一个 Entity 可以对应：

```text
0
1
N
```

个 RenderNode。

---

# 74. Render Diff

ECS 状态变化：

```text
Background changed
```

仅生成：

```text
SetBrush(RenderNode #182)
```

而不是重新构建整个 Scene。

---

# 75. Render Mutation

```text
CreateNode
DestroyNode

SetParent
SetZOrder

SetBounds
SetTransform
SetOpacity
SetClip

SetBrush
SetBorder
SetShadow

SetTextLayout
SetImage
SetVector
```

---

# 76. UiCommitBatch

```csharp
public readonly struct UiCommitBatch
{
    public readonly ReadOnlyMemory<RenderMutation> Mutations;
    public readonly ulong FrameId;
}
```

Backend Commit 必须：

- 尽可能批处理；
- 不回调 Runtime；
- 不在 Commit 中执行未知业务代码。

---

# 77. Backend Contract

Backend 应偏 Retained，而不是 Immediate Draw API。

```csharp
public interface IUiBackend
{
    void Initialize(in UiBackendContext context);

    void Commit(in UiCommitBatch batch);

    void RequestFrame();

    UiBackendCapabilities Capabilities { get; }
}
```

当前 Rendering 实现冻结以下契约：

- `UiRenderingRuntime` 必须绑定一个显式 Window/Surface `UiScopeId`；一个
  `UiWorld` 中的多个窗口分别拥有独立 `RenderScene` 与 Backend Commit，不能把
  ApplicationScope 下的所有窗口隐式合并到同一个 Surface；
- `RenderNodeId` 使用 `Index + Generation`，Entity slot 或 RenderNode slot 复用后，
  旧 mutation 无法命中新节点；
- `RenderNode` 的 Transform / Opacity 是相对 Parent 的局部值，由 Backend retained
  tree 合成。父节点动画只提交父节点 mutation，不向所有后代展开；
- `RenderDiffSystem` 只消费 `UiDirtyFlags.Render` 和结构版本变化。无视觉变化的强制帧
  不产生空 `UiCommitBatch`，也不调用 Backend；
- `NodeKindComponent` 的 presence 属于 Render topology：dirty Entity 的 ECS presence 与
  `RenderScene` presence 不一致时必须回退完整 reconcile，使逻辑父节点增删能够同步
  重挂仍存活的 Render descendants；
- 结构销毁按 child-before-parent 提交；仍存活的子树必须先 `SetParent`，再销毁旧父节点；
- `UiCommitBatch` 在跨越 Backend 边界后不可变，Backend Commit 不允许回调 Runtime；
- `TextLayout` 同时持有 ECS/Layout lease 与 RenderScene lease；替换或销毁文本节点时，
  旧 render lease 只能在 Backend Commit 成功后释放，确保 retained backend 中可见的
  `TextLayoutHandle` 始终有效；
- `UiBackendCapabilities` 只能声明当前真正实现的能力。尚未实现的 Blur / Shadow /
  Vector / HDR 不得提前宣称支持；
- 第一版 Avalonia Backend 使用一个 `PclUiSurface` 绘制 retained state，不为每个
  Entity 创建 Avalonia Control；文本由 `AvaloniaTextEngine` 复用 Avalonia 的成熟
  shaping / fallback 实现；
- `HeadlessUiBackend` 与 Avalonia retained state 都会验证 node generation、父节点存在性、
  parent cycle 和严格递增的 FrameId。

---

# 78. Avalonia Backend

Avalonia 最终主要保留：

```text
Window
PlatformHandle
GPU Surface
Input
IME
Clipboard
Cursor
Drag & Drop
Accessibility
Native Host
```

主窗口内部尽量接近：

```text
Window
└─ PclUiSurface
   ├─ ECS render surface
   └─ NativeHost overlay
```

而不是：

```text
Window
└─ thousands of Avalonia Controls
```

---

# 79. Backend Capability

定义：

```text
CompositionTransform
CompositionOpacity
Clip
Blur
Shadow
Vector
HDR
NativeTextInput
Accessibility
```

Runtime 可以根据：

```text
UiBackendCapabilities
```

选择 fallback。

页面不能针对 Avalonia 编写条件逻辑。

---

# 80. Composition / Layer Promotion

以下场景可被提升为独立 Layer：

```text
Animated subtree
Scrollable subtree
Blur
Opacity group
Transform group
Isolated overlay
```

页面只能给：

```text
RenderLayerHint
```

具体是否提升由 Renderer/Backend 决定。

---

# 81. Asset Manager

统一资源系统：

```text
ImageHandle
IconHandle
VectorIconHandle
FontHandle
BrushHandle
ShadowHandle
```

页面不持有 Backend 资源。

---

# 82. 资源加载

```text
Asset request
↓
placeholder
↓
worker decode
↓
ResourceReady event
↓
validate scope/generation
↓
Render dirty
```

---

# 83. Resource Cache

分为：

```text
Raw Asset Cache
Decoded Resource Cache
GPU Resource Cache
```

支持：

```text
LRU
Memory Budget
Scope Ownership
Reference Count / Weak Reference
Eviction
```

---

# 84. Overlay System

Runtime 原生 OverlayLayer：

```text
Root
├─ MainLayer
└─ OverlayLayer
   ├─ Tooltip
   ├─ ContextMenu
   ├─ Popup
   └─ Modal
```

避免页面自己操作多个 Window/Panel。

---

# 85. Tooltip

Tooltip System 管理：

```text
Delay
Pointer anchor
Placement
Auto close
Scope
Input pass-through
```

---

# 86. Popup / Menu

Popup 是：

```text
PopupScope
+
Overlay placement
+
Focus scope
```

ContextMenu：

```text
MenuModel
+
Command
+
Keyboard navigation
```

---

# 87. Modal

Runtime 原生：

```text
ModalBarrier
InputBarrier
FocusTrap
Background dim
```

页面不应手动：

```text
IsHitTestVisible = false
```

整个主界面。

---

# 88. Navigation

Navigation 属于 Runtime Framework，不属于具体页面。

状态：

```text
Created
Preparing
Entering
Active
Leaving
Dormant
Destroyed
```

---

# 89. Navigation Flow

```text
Navigate(Page B)
↓
create PageScope(B)
↓
instantiate Blueprint(B)
↓
pre-layout
↓
A = Leaving
B = Entering
↓
transition
↓
TransitionCompleted
↓
A = Dormant/Destroyed
B = Active
```

无 callback 嵌套。

---

# 90. Navigation Generation

每次 navigation：

```text
NavigationGeneration++
```

System 只处理当前 generation。

旧 transition/event 自动失效。

---

# 91. Page Cache Policy

Runtime 支持：

```text
None
KeepPresentationState
KeepEntities
LRU
Pinned
```

具体页面后续只声明策略。

---

# 92. Accessibility

从第一版纳入架构。

Component：

```text
SemanticRole
AccessibleName
AccessibleDescription
AccessibleValue
AccessibleState
AccessibleAction
```

生成独立 Semantic Tree。

Backend.Avalonia 再映射到 Avalonia Automation/Accessibility 能力。

---

# 93. Thread Model

World 始终只有：

> **一个 Owner Thread**

禁止多线程并发直接读写 World。

---

# 94. 第一阶段：Inline Runtime

初版可以：

```text
Avalonia UI Thread
=
UiRuntime owner
```

但任何调度必须经过：

```csharp
IUiRuntimeScheduler
```

禁止页面/Runtime 直接使用：

```text
Dispatcher.UIThread.Post
```

以便后续迁移到 Dedicated Runtime Thread。

---

# 95. 第二阶段：Dedicated Runtime Thread

未来：

```text
Platform Thread
      │
      ├─ Input Queue
      ├─ Resize Queue
      └─ Native Host Events
      │
      ▼
UiRuntime Thread
      │
      ├─ ECS
      ├─ Binding
      ├─ Style
      ├─ Layout
      ├─ Animation
      └─ Render Diff
      │
      ▼
Commit Buffer
      │
      ▼
Platform Thread
```

World 内部：

```text
0 locks
```

---

# 96. Message Passing

跨线程只使用：

```text
InputQueue
StatePatchQueue
BackendCommitQueue
ResourceEventQueue
```

可采用：

```text
MPSC queue
double buffer
triple buffer
```

不共享可变 UI State。

---

# 97. Worker Pool

适合后台：

```text
Image decode
SVG/vector compile
Asset IO
Large collection filter/sort
部分 text shaping
Blueprint preparation
```

不允许 Worker 直接修改：

```text
UiWorld
Hierarchy
Focus
AnimationStore
RenderScene
```

Worker 输出结果，再由 Runtime Owner Thread 应用。

---

# 98. Frame Snapshot Consistency

每帧开始冻结：

```text
InputSequence <= X
PresentationVersion <= Y
ThemeVersion <= Z
ResourceSequence <= R
```

帧中途到达的新事件进入下一帧。

保证：

```text
一个 frame 使用一致状态
```

---

# 99. Error Boundary

支持：

```text
UiErrorBoundary
```

捕获：

```text
Binding error
Template error
Resource error
Widget construction error
```

错误只影响局部 subtree。

可 fallback：

```text
Error placeholder
```

并写入 Diagnostics。

---

# 100. Diagnostics

Runtime 所有关键子系统应使用结构化事件：

```text
EntityCreated
EntityDestroyed
DirtyMarked
BindingExecuted
LayoutMeasured
LayoutArranged
AnimationCreated
AnimationCompleted
RenderMutationGenerated
NativeHostCreated
```

Release Build 可按级别关闭高成本 trace。

首版 Runtime 通过 `UiDiagnosticsOptions` 冻结三档能力：默认仅记录固定容量的 lifecycle
事件，Developer 额外开启 Dirty Trace 与 Frame Timeline，Disabled 完全关闭。事件使用
sequence-based bounded multi-reader journal；慢 reader 只累计 `DroppedCount`，不能阻塞
Runtime 或令内存随进程时长增长。高频事件保持结构化数值字段，不预先格式化字符串。

`DirtyMarked` 同时记录 target 与 propagation source；Layout ancestor invalidation 必须逐级
写入 source chain，使 DevTools 能重建 `leaf → parent → boundary/root`，而不是只展示某一帧
最终残留的 Dirty flags。

---

# 101. DevTools

`PCL.UI.Next.DevTools` 至少提供：

## 101.1 Entity Inspector

查看：

```text
Entity
Generation
Scope
Components
Hierarchy
```

## 101.2 Layout Inspector

查看：

```text
DesiredSize
LayoutRect
Constraints
Layout parent
Dirty reason
Layout boundary
```

## 101.3 Render Inspector

查看：

```text
RenderNode
Bounds
Transform
Opacity
Layer
Clip
Backend handle
```

## 101.4 Interaction Inspector

显示：

```text
Hit test bounds
Pointer target
Capture
Focus
Hovered path
Bubble route
```

## 101.5 Animation Inspector

显示：

```text
Entity
Property
From
Current
Target
Duration
Easing
Generation
Scope
```

## 101.6 Virtualization Inspector

显示：

```text
Logical Count
Visible Range
Overscan Range
Realized Count
Recycle Pool
Measured Extent
```

---

# 102. Frame Timeline

示例：

```text
Frame #821

Platform Events      0.03 ms
Bindings             0.08 ms
Structure            0.01 ms
Style                 0.04 ms
Virtualization        0.02 ms
Text Measure          0.09 ms
Layout Measure        0.19 ms
Layout Arrange        0.11 ms
Animation             0.03 ms
Render Diff           0.06 ms
Commit                0.18 ms

Runtime Total         0.66 ms
Backend Commit        0.18 ms

Entities touched      84 / 7421
Render mutations      37
Allocations           0 B
```

逐系统计时由 `SystemPipeline` 统一包围 `IUiSystem.Update`，不能要求各 System 自行埋点。
Timeline 使用独立固定容量 ring 保存不可变 frame snapshot，包含 system timing、entity count、
dirty mark count、render mutation count 与当前线程 allocation delta；未启用时不得调用
`Stopwatch`/allocation counter 或创建 timeline 数组。

---

# 103. Headless Backend

测试不启动 Avalonia。

```text
PCL.UI.Next.Backend.Headless
```

实现：

```text
Fake text engine
Fake backend
Deterministic clock
Synthetic input
Deterministic render handles
```

---

# 104. Deterministic Tests

动画：

```text
clock.Advance(16ms)
Update()
Assert(opacity)
```

Navigation：

```text
Navigate(A)
Advance(...)
Navigate(B)
Advance(...)
Assert(scope A destroyed)
Assert(B active)
```

---

# 105. Input Replay

支持记录：

```text
Platform Event
State Patch
Clock Tick
Resource Ready
Window Size
```

输出：

```text
.uireplay
```

出现难复现的 transition/race：

```text
Replay
↓
deterministically reproduce
```

---

# 106. Benchmark Suite

至少包含：

## B1. Idle

```text
5000 logical entities
no animation
no input
```

目标：

```text
0 recurring frame
0 B/frame
```

## B2. Hover Stress

```text
1000 clickable nodes
rapid pointer movement
```

## B3. Animation Stress

```text
500
1000
5000
```

同时动画。

## B4. Large Virtual List

```text
100,000 logical rows
30 visible
variable height
```

## B5. Layout Stress

```text
deep nested stack/grid
```

## B6. Theme Switch

```text
10k entities
subset depends on accent
```

测试增量 Dirty。

## B7. Render Diff

只改变：

```text
1 / 10 / 100
```

实体，验证 Mutation 数量接近变更量。

---

# 107. 性能预算

初始目标，不作为绝对保证，但进入 CI Benchmark：

## Idle

```text
Frame requests: 0
Recurring CPU: ≈ 0
Allocation: 0 B/frame
```

## 普通交互帧

```text
Runtime CPU < 1 ms typical
```

## 动画帧

```text
Runtime ECS/Animation < 1 ms typical
```

## 大列表

```text
100,000 logical items
< 100 realized item subtrees typical
```

## GC

普通交互与动画 Hot Path：

```text
0 B/frame
```

---

# 108. Public API 边界

页面未来只允许使用：

```text
Ui.*
UiNode
UiTemplate
UiClass
Selector
UiCommand
ThemeToken
```

页面不应看到：

```text
UiEntity
ComponentPool<T>
DirtyTracker
RenderNode
RenderMutation
Avalonia.Control
```

---

# 109. Widget 设计

Widget 不是 class inheritance。

例如：

```text
Button
Card
Toggle
NavigationItem
ProgressBar
```

只是：

```text
Blueprint function
+
Style class
+
behavior components
```

概念代码：

```csharp
public static UiNode Button(
    UiNode content,
    UiCommand command)
{
    return Ui.Container(content)
        .Class(UiClass.Button)
        .Behavior(UiBehavior.Clickable)
        .Command(command);
}
```

---

# 110. Behavior 组合

禁止产生大量：

```text
ButtonSystem
CardSystem
NavigationButtonSystem
ListButtonSystem
```

应组合：

```text
Hoverable
Pressable
Clickable
Selectable
Focusable
CommandBinding
```

由通用 Systems 处理。

---

# 111. 示例：Button 的 Runtime 组成

```text
Blueprint Button
↓ instantiate
Entity #120
├ LayoutNode
├ Padding
├ Background
├ CornerRadius
├ HitTestable
├ Hoverable
├ Pressable
├ Clickable
├ Focusable
├ CommandBinding
└ StyleClasses(Button)

Child #121
└ TextContent
```

Hover：

```text
PointerMove
↓
HitTest
↓
#120 Hovered
↓
Style dirty
↓
ResolvedStyle target changes
↓
Transition generated
↓
Animation store
↓
Render mutation
```

---

# 112. 示例：State 更新

```text
Download progress = 42%
↓
Presentation State version++
↓
StatePatchQueue
↓
Binding dependency table
↓
Binding #91 executes
↓
TextContent / ProgressValue update
↓
Render dirty
↓
minimal RenderMutation
```

无：

```text
PropertyChanged
BindingExpression
reflection
Control property invalidation chain
```

---

# 113. 示例：页面切换

```text
Navigate(Settings)
↓
Navigation generation = 51
↓
Create Settings PageScope
↓
Instantiate Settings Blueprint
↓
Pre-layout
↓
Home Leaving
Settings Entering
↓
FLIP / compositor transition
↓
TransitionCompleted(gen=51)
↓
Settings Active
↓
Home Dormant/Destroyed
```

如果旧事件：

```text
TransitionCompleted(gen=50)
```

晚到：

```text
discard
```

---

# 114. 示例：Virtual List

```text
Logical items = 100000
Viewport = 640px
Estimated row = 52px
Overscan = 6
```

Runtime：

```text
Visible 1000..1013
Realized 994..1019
```

滚动：

```text
994 leaves
↓
recycle
↓
rebind as 1020
```

无需构造 100000 个实体子树。

---

# 115. 代码规范

`PCL.UI.Next` 项目中禁止：

```text
UserControl
Control subclass 作为主要 Widget
INotifyPropertyChanged
ObservableCollection 作为 UI runtime collection
StyledProperty 作为业务状态
Reflection Binding
Dispatcher.UIThread.Post 散落调用
Task.Delay 做 UI Transition
Animation callback 驱动生命周期
Width/Height 高频动画
页面直接访问业务 Service
页面直接访问 UiWorld
直接持有 Avalonia Brush/Control
```

---

# 116. 允许的 Backend 特例

只有：

```text
PCL.UI.Next.Backend.Avalonia
```

允许出现：

```text
Avalonia.Window
Avalonia.Controls.TextBox
Avalonia.Input.*
Avalonia Automation
```

其他项目若确需平台互操作，只能通过 Abstractions contract。

---

# 117. API 设计草案

## 117.1 Runtime

```csharp
public sealed class UiRuntime
{
    public UiWorld World { get; }

    public void ApplyStatePatch(in UiStatePatch patch);

    public void EnqueuePlatformEvent(in UiPlatformEvent e);

    public UiFrameResult Update();

    public void Shutdown();
}
```

生产环境可不公开 `World` 给页面程序集，仅供内部/DevTools。

---

## 117.2 Scheduler

```csharp
public interface IUiRuntimeScheduler
{
    void RequestReactiveFrame();
    void RequestContinuousFrame(UiContinuousReason reason);
    void ReleaseContinuousFrame(UiContinuousReason reason);
}
```

---

## 117.3 Scope

```csharp
public interface IUiScopeManager
{
    UiScopeId Create(UiScopeId parent);
    void Dispose(UiScopeId scope);
    bool IsAlive(UiScopeId scope);
}
```

---

## 117.4 Blueprint

```csharp
public readonly struct UiBlueprint
{
    internal readonly BlueprintHandle Handle;
}
```

---

## 117.5 Backend

```csharp
public interface IUiBackend
{
    UiBackendCapabilities Capabilities { get; }

    void Initialize(in UiBackendContext context);
    void Commit(in UiCommitBatch batch);
    void RequestFrame();
    void Shutdown();
}
```

---

## 117.6 Text Engine

```csharp
public interface ITextEngine
{
    TextLayoutHandle Layout(in TextLayoutRequest request);
    UiSize Measure(TextLayoutHandle handle);
}
```

---

# 118. 内存布局原则

## 118.1 Entity

目标：

```text
8 bytes
```

```text
Index      4
Generation 4
```

## 118.2 常用 Component

尽量：

```text
<= 16 / 32 bytes
```

## 118.3 动态对象

通过：

```text
Handle
```

指向 Store。

例如：

```text
TextLayoutHandle
BrushHandle
ImageHandle
GridTrackSetHandle
TransitionSetHandle
```

## 118.4 不在热路径 Component 中存 GC object

特别避免：

```text
string
delegate
object
List<T>
Dictionary<K,V>
```

---

# 119. Handle Registry

统一建议：

```csharp
public readonly struct ResourceHandle<TTag>
{
    public readonly int Index;
    public readonly uint Generation;
}
```

防止资源释放后旧 Handle 指向新对象。

---

# 120. 生命周期分层

```text
Application
│
├ Window
│  │
│  ├ Navigation
│  │  └ Page
│  │     └ Popup
│  │
│  └ Global Overlay
│
└ Global Resources
```

资源、动画、Binding、NativeHost 都必须可追溯到 owner。

---

# 121. 错误处理策略

## Recoverable

例如：

```text
图片加载失败
Binding fallback
Widget local error
```

局部 fallback。

## Structural Fatal

例如：

```text
World corruption
Hierarchy cycle
Component storage corruption
```

应 Fail Fast，并生成诊断包。

Debug Build 必须积极 assert。

---

# 122. Debug Invariants

至少检查：

```text
Entity generation valid
Hierarchy no cycle
No dead entity in active component pool
No render handle attached to dead entity
Scope ownership valid
Focus target alive
Pointer capture target alive
Animation owner alive
No stale native host
```

---

# 123. Navigation 与 Popup 的生命周期原则

永远不能依赖：

```text
async void
event callback
Task.Delay
```

来决定销毁。

统一依赖：

```text
Runtime state
+
generation
+
explicit completed events
```

---

# 124. NativeHost 一致性

NativeHost 与 ECS 保持单向同步：

```text
ECS target state
↓
NativeHost Mutation
```

用户输入：

```text
NativeHost Event
↓
Platform Event Queue
↓
Runtime
↓
Command / State
```

不要允许 NativeHost 自己偷偷成为第二套状态源。

---

# 125. Theme / DPI / Window Resize

这些均作为版本化 Runtime Input：

```text
ThemeChanged
DpiChanged
WindowResized
ScaleFactorChanged
```

并只 Dirty 受影响的组件。

---

# 126. DPI

布局统一使用：

```text
logical pixels
```

Backend 负责：

```text
logical → physical
```

PixelSnap System 在最终阶段处理需要像素对齐的内容。

---

# 127. Windowing

第一版可只支持一个主 Window，但接口必须允许：

```text
Multiple UiRoot
Multiple WindowScope
```

每个 Window：

```text
独立 input root
独立 focus root
独立 render root
共享 theme/resource manager
```

---

# 128. 多窗口线程

默认仍使用：

```text
single UiRuntime owner
```

除非未来证明确实需要多 World。

不要一开始为每窗口做独立线程。

---

# 129. Plugin / Extension UI

未来插件 UI 不应直接注入 Avalonia Control。

应通过：

```text
UiBlueprint / Widget extension contract
```

或者 Sidecar/插件层的受限 UI 描述协议。

这部分不在第一阶段实现，但架构上必须避免把 Avalonia Control 作为扩展 ABI。

---

# 130. Native AOT 考虑

新 UI Runtime 应避免：

```text
Reflection-based binding
runtime code generation
dynamic type activation
```

Blueprint Source Generator、显式泛型注册、静态 Binding 更适合 Native AOT。

因此 `PCL.UI.Next` 应从第一天：

```text
AOT-friendly
Trim-friendly
```

---

# 131. Source Generator

推荐生成：

```text
Blueprint static data
Binding updater
Selector dependency map
Style class IDs
Template IDs
Resource IDs
```

避免运行时：

```text
reflection
string lookup
dictionary-heavy metadata
```

---

# 132. Stable IDs

编译期资源尽量使用稳定 ID：

```text
UiClassId
TemplateId
BindingId
ThemeTokenId
CommandId
```

Debug Build 可以保留 name table。

Release Build 热路径只用整数 ID。

---

# 133. Dev/Release 双表示

Debug：

```text
Id + Name
Source location
Dirty reason
Stack trace optionally
```

Release：

```text
compact IDs
no source strings in hot path
```

---

# 134. Logging

Runtime 日志分：

```text
Trace
Debug
Info
Warn
Error
Fatal
```

高频事件默认只进入 ring buffer，不直接格式化字符串。

---

# 135. Diagnostics Ring Buffer

保存最近：

```text
N frames
N commands
N navigation transitions
N scope lifecycle events
```

发生 Fatal：

```text
dump diagnostics
```

有利于现场复现 UI race。

---

# 136. 首期 Playground 场景

在页面重写前必须完成：

```text
1. Basic Geometry
2. Stack/Grid Layout
3. Text
4. Card/Button
5. Hover/Press
6. Focus/Keyboard
7. Animation/Transition
8. FLIP Layout Animation
9. Scroll
10. Fixed-height Virtual List
11. Variable-height Virtual List
12. 500 simultaneous animations
13. Theme Switch
14. Popup
15. Modal
16. Tooltip
17. Native TextBox
18. Image Async Loading
19. Navigation
20. Accessibility tree
```

---

# 137. 实施阶段

## Phase 0 — Contracts

建立：

```text
Abstractions
IDs
Handles
Math types
Backend contracts
Clock
Scheduler
```

完成后这些 API 尽量冻结。

---

## Phase 1 — ECS Kernel

实现：

```text
EntityRegistry
ComponentPool<T>
HierarchyStore
UiScope
DirtyTracker
EventQueue
StatePatchQueue
SystemPipeline
```

配套：

```text
unit tests
fuzz tests
generation tests
```

---

## Phase 2 — Authoring / Blueprint

实现：

```text
UiNode
UiBlueprint
Template
Instantiate
Structural Reconcile
Compiled Binding
Source Generator prototype
```

---

## Phase 3 — Layout / Style / Text

实现：

```text
Stack
Grid
Overlay
Absolute
Theme
Style
Text Engine
Text Cache
```

---

## Phase 4 — Input / Focus

实现：

```text
Hit Test
Pointer
Routed event
Focus
Keyboard
Shortcut
Gesture
```

---

## Phase 5 — Animation

实现：

```text
Animation Store
Easing
Transition
Generation
FLIP
Scheduler continuous-frame integration
```

---

## Phase 6 — Rendering

实现：

```text
RenderScene
RenderNode
RenderDiff
CommitBatch
Headless backend
Avalonia backend
```

---

## Phase 7 — Scroll / Virtualization

实现：

```text
ScrollState
Inertia
Virtualization
Recycling
Variable extent index
```

---

## Phase 8 — Native Host / A11y / Overlay

实现：

```text
TextBox NativeHost
Semantic Tree
Tooltip
Popup
Modal
Navigation
```

### Phase 8 已冻结的运行时契约

#### NativeHost

- ECS 中的 `NativeHostComponent` 是目标状态唯一来源；平台输入只进入
  `NativeHostFrameEvent` journal，不允许 Backend 直接反写 ECS；
- Backend 以 generation-safe `NativeHostHandle` 管理 create/update/destroy；
- bounds、visibility、enabled、focus、value、selection、read-only 与 multiline
  均通过 diff mutation 同步；
- 每帧所有 NativeHost diff 完成后，Backend 只执行一次 platform focus reconciliation：
  目标为 NativeHost 时聚焦对应 Control，否则将焦点交回 retained surface；
- `UiEffectiveState` 统一计算祖先感知的 visible/enabled/interactive；Focus、HitTest、
  Accessibility 与 NativeHost 不允许只读取 Entity 自身状态；
- sibling Overlay 的权限由共享 `UiInteractionPolicy` 计算；NativeHost 不得保留私有的
  barrier 判断。Pointer、KeyboardFocus、Accessibility、CommandInvoke 与 NativeHost
  分别选择当前 Window 中阻止该 capability 的最高 barrier；
- NativeHost 与所属 `UiScope` 同生共死，Scope 销毁时必须立即释放平台控件。

#### Semantic Tree / Accessibility

- `SemanticRole`、`AccessibleName`、`AccessibleDescription`、`AccessibleValue`、
  `AccessibleState`、`AccessibleAction` 生成独立 `UiSemanticTreeSnapshot`；
- Semantic parent 是最近的 semantic ancestor，不能复用 RenderNode parent；
- 每个 Window/Input Root 独立消费 Accessibility dirty，不能跨窗口清除或合并；
- Avalonia Backend 使用虚拟 `AutomationPeer` 暴露 retained Entity，不为每个渲染
  Entity 创建 Avalonia Control；NativeHost owner 不创建 virtual peer，只使用原生
  Control peer，确保同一语义节点在平台 Automation Tree 中只暴露一次；
- 平台 Invoke/Focus 先进入 `UiAccessibilityActionRequest`，再由 Runtime 校验
  Entity generation、Scope、支持的 action、effective state 与 `UiInteractionPolicy`，
  最后进入 Focus/Command；被 Modal 隔离的 background request 必须直接丢弃；
- Modal 打开时，范围外 semantic node 不进入 snapshot。遍历仍须穿过无语义的逻辑
  ancestor，保证位于 OverlayRoot 下、但属于 Modal allowed Scope 的后代可以进入树。

#### Overlay

- 每个 Window 只有一个 Runtime-owned `OverlayRoot`；Tooltip、Popup、Modal
  均创建独立 child Scope，并使用 generation-safe `UiOverlayHandle`；
- Tooltip 支持 delay、pointer anchor、viewport clamp、auto-close 和完整 subtree
  input pass-through；等待 timer 通过引用计数 lease 持有 continuous frame；
- Popup 使用 placement + optional outside-pointer barrier + FocusScope，关闭时恢复焦点；
- Popup anchor、NativeHost、HitTest 与 Accessibility 必须共享 `UiVisualGeometry`，其
  world bounds 同时包含 parent/scroll/FLIP/style transform，并以四角变换后的 AABB 表达；
- Modal 始终具有 dim barrier、input barrier 和 trapping FocusScope，不修改主页面的
  `IsHitTestVisible`；
- `UiInteractionBarrier` 只声明 RootScope、AllowedScope、Z 与被阻止的 capability flags；
  Modal 阻止范围外 pointer、keyboard focus、accessibility、command invoke 与 NativeHost，
  outside-pointer Popup 只阻止 pointer 与 NativeHost，使点击落到 retained barrier；
- visual stacking 不得依赖 input barrier：每个 Overlay content root 通过
  `UiNativeHostOcclusion` 独立声明 window Scope、allowed Scope、Z 与实时 visual bounds；
  更高 Overlay 与范围外 NativeHost 相交时 Backend 隐藏该平台控件，非相交控件保持显示。
  Modal dim layer 另外提供 viewport-wide occlusion；Tooltip 与无 barrier Popup 同样适用；
- Overlay Scope 被外部销毁时，handle 必须立即失效，routed handler 与 timer lease
  必须同步释放。

#### Navigation

- 页面状态固定为 `Created → Preparing → Entering → Active → Leaving →
  Dormant/Destroyed`；每个页面实例拥有独立 PageScope；
- 每次 `Navigate` 递增 `NavigationGeneration`。再次导航时，所有仍在 Entering/Leaving
  的页面加入新 transition group；旧 generation completion 只能被丢弃；
- 页面 lifecycle 只写入 `UiNavigationEventJournal`，公共 API 不允许 completion callback；
- Navigation 通过独立的 lossless internal completion queue 驱动生命周期；有界
  `UiAnimationEventJournal` 只服务 DevTools/Diagnostics/public reader，不能作为状态机
  的可靠消息通道，且任一观测 reader 都不能抢占其他 reader 的事件；
- Cache policy 固定为 `None / KeepPresentationState / KeepEntities / Lru / Pinned`；
  LRU 只计算 Dormant 且声明为 Lru 的页面，Pinned 永不被容量驱逐；
- Dormant/Preparing 页面从 HitTest 与 Semantic Tree 整棵移除，但仍可保留 Entity，
  不得只隐藏页面根而让后代继续接收输入。

---

## Phase 9 — DevTools / Benchmark

实现完整：

```text
Inspector
Timeline
Dirty Trace
Replay
Benchmark CI
```

### Phase 9 已冻结的 Inspector 契约

- `PCL.UI.Next.DevTools` 只依赖 backend-neutral Runtime，不引用 Avalonia，也不取得 ECS
  写权限；Inspector 输出 immutable snapshot，不向页面暴露可变 component reference；
- Entity Inspector 输出 generation-safe Entity/Scope、logical parent/children、当前 Dirty flags
  与 component type names；component 枚举由 `ComponentStore` 提供 AOT-safe catalog，不扫描字段；
- Layout Inspector 通过 `LayoutEngine.TryGetLastMeasureConstraint` 读取最近一次真实 measure
  constraint，并同时返回 DesiredSize、LayoutRect、layout parent 与 boundary；
- Render Inspector 复用 `UiRenderNodeSnapshot`；Interaction Inspector 输出 hit bounds、当前
  input root、focus/hover/press/capture 与 bubble route；
- Animation/Virtualization Inspector 只读取 Runtime snapshot API。`UiMotionTraceSession` 在
  BackendCommit phase 采样 active channel，并从 animation journal 补入 settle 终点；采样环
  固定容量，禁止因 DevTools 长期开启而无限增长；
- Dirty Trace reader 必须暴露 journal `DroppedCount`，UI 应明确标注被 retention 淘汰的历史，
  不得把不完整 trace 伪装为完整因果链。

---

## Phase 10 — Runtime Freeze

在正式重写 PCL 页面前冻结：

```text
Public Authoring API
Binding API
Theme API
Navigation contract
Backend contract
Virtualization contract
```

再进入页面迁移。

---

# 138. Runtime 完成验收标准

在开始重写实际页面前，应至少满足：

- [ ] Runtime 不依赖 Avalonia；
- [ ] Backend.Avalonia 可独立替换为 Headless；
- [ ] Idle 时不持续 request frame；
- [ ] Hot animation path 0 B/frame；
- [ ] 10 万项列表只实例化可见范围；
- [ ] Hover/Press 不通过回调实现；
- [x] Navigation 不通过回调实现；
- [x] 页面生命周期通过 Scope；
- [x] 过期 generation 事件能正确丢弃；
- [ ] Layout 是增量 Dirty；
- [ ] Scroll 不触发全量 Layout；
- [ ] Style/Theme 支持局部失效；
- [ ] Binding 无反射；
- [ ] UI 不使用 `INotifyPropertyChanged`；
- [ ] UI 不依赖业务 Service；
- [x] TextBox 通过 NativeHost 正常工作；
- [x] Semantic Tree 可以被 Backend 暴露；
- [ ] Headless 测试可重放输入；
- [ ] DevTools 能显示 Dirty chain；
- [ ] Benchmark CI 已建立。

---

# 139. 新旧 UI 的仓库边界

开发期允许：

```text
PCL.UI        ← 旧 UI
PCL.UI.Next   ← 新 Runtime
```

但：

```text
PCL.UI.Next
```

内部禁止引用旧 UI。

允许的唯一临时桥接应该发生在应用最外层，例如：

```text
PCL.Desktop
```

决定：

```text
OldWindow
or
NextWindow
```

而不是在一个页面内混搭。

---

# 140. 页面迁移阶段原则

页面重写开始后建议：

```text
页面作为完整单元迁移
```

而不是：

```text
旧 Grid
里面嵌新 ECS Card
再嵌旧 Button
```

理想切换：

```text
旧页面
↓
完整替换
↓
Next Blueprint 页面
```

---

# 141. 后端迁移策略

第一版：

```text
Avalonia Window
+
PclUiSurface
```

后续如果需要：

```text
Skia
DirectComposition
Vulkan
```

只新增：

```text
PCL.UI.Next.Backend.*
```

Authoring 与 Runtime 不改。

---

# 142. 关键 ADR

---

## ADR-001：UI Runtime 使用 ECS

**决定**：使用 Entity + Component + System，而不是 Control inheritance。

**原因**：

- 数据导向；
- 热路径批量处理；
- 状态与行为解耦；
- 可增量计算；
- 适合统一动画/交互/虚拟化。

---

## ADR-002：使用 Reactive ECS，而非每帧全量 ECS

**决定**：绝大多数 System 仅处理 Dirty Set。

**原因**：

UI 大部分时间静止，全量 tick 会浪费 CPU。

---

## ADR-003：使用 Sparse Packed Component Storage

**决定**：第一版不使用严格 Archetype Chunk。

**原因**：

UI 结构变化频繁，Sparse Pool 更简单稳定。

---

## ADR-004：页面使用 Blueprint

**决定**：页面不直接访问 World。

**原因**：

避免低级 ECS 操作污染页面表达。

---

## ADR-005：Blueprint 采用编译式 Binding

**决定**：避免 Reflection Binding 与 PropertyChanged。

---

## ADR-006：Runtime 自研 Layout

**决定**：不将 Avalonia Layout 作为核心。

---

## ADR-007：Rendering 使用 Retained Scene

**决定**：不每帧全量 Immediate Draw。

---

## ADR-008：Avalonia 作为 Backend

**决定**：Runtime 不引用 Avalonia。

---

## ADR-009：复杂文本输入通过 NativeHost

**决定**：第一版不重造 TextBox。

---

## ADR-010：World Single Owner Thread

**决定**：禁止多线程共享写 World。

---

## ADR-011：生命周期使用 Scope + Generation

**决定**：禁止 UI 生命周期依赖 callback chain。

---

## ADR-012：Virtualization 为 Runtime 一级能力

**决定**：不是某个特殊 Panel 的特性。

---

# 143. 风险

## R1. 自研 Layout 工作量大

缓解：

- 第一阶段只实现 PCL 必需布局；
- Stack/Grid/Overlay/Absolute 优先；
- 不追求 CSS 完整能力。

## R2. Text 与 IME 复杂

缓解：

- 抽象 Text Engine；
- TextBox 使用 NativeHost。

## R3. Accessibility 容易后补失败

缓解：

- Semantic Tree 从第一版设计。

## R4. Debug ECS UI 困难

缓解：

- Dirty Trace；
- Entity Inspector；
- Deterministic Replay。

## R5. Authoring DSL 可能过于低级

缓解：

- Widget 层只组合 Blueprint；
- 页面永远不暴露 Entity。

## R6. 自研 Render Backend 工作量大

缓解：

- 第一阶段尽量借用 Avalonia Surface / composition 能力；
- 保持 Backend contract 独立。

---

# 144. 暂不冻结的开放问题

这些可以在 Playground 阶段通过 Benchmark 决定：

1. `ComponentPool<T>` 使用 array 还是 `NativeMemory`；
2. 是否值得对部分组件使用 unmanaged storage；
3. Animation Store 是否直接 SIMD；
4. Hit Test Tree 最终使用简单层级、BVH 还是 R-Tree；
5. Variable Height List 使用 Fenwick Tree 或 Segment Tree；
6. Text Engine 第一版具体基于哪一实现；
7. Avalonia Backend 是更多依赖 DrawingContext，还是更多依赖 Composition；
8. Dedicated Runtime Thread 在首个正式版本是否启用；
9. Render Scene 是否需要跨帧 triple buffering；
10. Blueprint Source Generator 的最终语法形式。

这些属于实现策略，不改变顶层架构。

---

# 145. 不应再讨论的已冻结方向

在 `PCL.UI.Next` 立项后，以下原则除非有重大证据，否则不再反复摇摆：

```text
UI ECS
Reactive / Dirty Driven
Blueprint Authoring
Compiled Binding
Incremental Layout
Retained Rendering
Backend Isolation
Scope + Generation
Runtime Virtualization
Command-based Business Interaction
Single-owner World
```

---

# 146. 最终架构定义

`PCL.UI.Next` 的完整定义：

> **一个面向 PCL N 工作负载优化的、声明式、响应式、数据导向的 Retained UI Engine。**

其主要数据流为：

```text
Presentation State
        │
        ▼
Compiled Binding
        │
        ▼
Reactive ECS Components
   ▲            │
   │            ▼
Input        Style Target
   │            │
   └── Interaction
                │
      ┌─────────┴─────────┐
      ▼                   ▼
    Layout             Animation
      │                   │
      └─────────┬─────────┘
                ▼
           Render State
                │
                ▼
           Render Diff
                │
                ▼
           Commit Batch
                │
                ▼
             Backend
```

架构边界：

```text
Domain
  ≠
Presentation
  ≠
Blueprint
  ≠
Runtime ECS
  ≠
Render Scene
  ≠
Platform Backend
```

只要这些边界保持稳定，后续 PCL 首页、下载页、版本页、设置页、插件页等都只是 `PCL.UI.Next` 的消费者，而不再反过来塑造 Runtime。

---

# 147. 推荐的第一批实际代码文件

```text
PCL.UI.Next.Abstractions/
├─ UiEntity.cs
├─ UiScopeId.cs
├─ UiRect.cs
├─ UiSize.cs
├─ UiColor.cs
├─ UiLength.cs
├─ UiTimestamp.cs
├─ Handles.cs
├─ IUiClock.cs
├─ IUiBackend.cs
└─ UiBackendCapabilities.cs

PCL.UI.Next.Runtime/
├─ UiRuntime.cs
├─ UiWorld.cs
├─ EntityRegistry.cs
├─ ComponentPool.cs
├─ HierarchyStore.cs
├─ ScopeRegistry.cs
├─ DirtyTracker.cs
├─ UiDirtyFlags.cs
├─ SystemPipeline.cs
├─ UiFrameScheduler.cs
├─ EventQueue.cs
├─ StatePatchQueue.cs
└─ Diagnostics/

PCL.UI.Next.Authoring/
├─ Ui.cs
├─ UiNode.cs
├─ UiBlueprint.cs
├─ UiTemplate.cs
├─ UiClass.cs
├─ UiBinding.cs
├─ UiCommandBinding.cs
└─ Compiler/

PCL.UI.Next.Layout/
├─ LayoutSystem.cs
├─ MeasureSystem.cs
├─ ArrangeSystem.cs
├─ LayoutBoundary.cs
├─ StackLayout.cs
├─ GridLayout.cs
├─ OverlayLayout.cs
└─ AbsoluteLayout.cs

PCL.UI.Next.Animation/
├─ AnimationStore.cs
├─ AnimationSystem.cs
├─ TransitionSystem.cs
├─ Easing.cs
└─ FlipSystem.cs

PCL.UI.Next.Rendering/
├─ RenderScene.cs
├─ RenderNode.cs
├─ RenderMutation.cs
├─ RenderDiffSystem.cs
└─ UiCommitBatch.cs

PCL.UI.Next.Backend.Avalonia/
├─ AvaloniaUiBackend.cs
├─ PclUiSurface.cs
├─ AvaloniaInputBridge.cs
├─ AvaloniaNativeHost.cs
├─ AvaloniaAccessibilityBridge.cs
└─ AvaloniaTextEngine.cs
```

---

# 148. 下一步

正式开始实现前建议按顺序完成：

```text
1. 建立 PCL.UI.Next solution/project structure
2. 写 Abstractions
3. 写 ECS Kernel
4. 写 Headless Backend
5. 建 deterministic tests
6. 写 Dirty/Frame scheduler
7. 写 Blueprint prototype
8. 写最小 Layout
9. 写最小 RenderScene
10. 接 Avalonia Surface
11. 再进入 Style/Input/Animation/Virtualization
```

在此之前，不建议开始重写任何 PCL 业务页面。

---

## 附录 A：架构一句话版本

```text
PCL.UI.Next
=
Declarative Blueprint
+
Compiled Reactive Binding
+
Reactive ECS
+
Incremental Layout
+
Declarative Animation
+
Runtime Virtualization
+
Retained Render Scene
+
Backend Isolation
+
Scope/Generation Lifetime
```

---

## 附录 B：架构红线

如果未来代码出现以下形态，应直接视为架构回退：

```csharp
class XxxButton : Button
```

```csharp
class XxxPage : UserControl
```

```csharp
PropertyChanged?.Invoke(...)
```

```csharp
Dispatcher.UIThread.Post(...)
```

```csharp
await Task.Delay(200);
NextPage();
```

```csharp
control.Width = animationValue;
```

```csharp
world.Create(); // 出现在页面代码
```

```csharp
downloadService.Start(); // 出现在 Widget/UI Runtime
```

`PCL.UI.Next` 的价值正是把这些隐式、分散、对象驱动的行为，统一变成：

```text
State
Component
System
Command
Dirty
Scope
Render Diff
```

并通过严格边界保证这种架构不会再次退化。
