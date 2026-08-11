# PCL.UI.Next 动画架构补充说明

> **文档类型**：Architecture Supplement  
> **所属项目**：`PCL.UI.Next`  
> **所属模块**：`PCL.UI.Next.Animation`  
> **依赖文档**：`PCL.UI.Next-Architecture-Design.md`  
> **状态**：Draft / 可进入实现评审  
> **核心原则**：Target-driven / Naturally Interruptible / Velocity-aware / ECS Hot Path

---

# 0. 摘要

`PCL.UI.Next.Animation` 不把动画视为一次性的“任务”或“回调链”，而是把动画定义为：

> **当前视觉状态（Current）向目标视觉状态（Target）连续收敛的状态求解过程。**

因此，“动画被打断”不应作为特殊情况存在。

在新模型中：

```text
Normal
↓
Hovered
↓
Pressed
↓
Hovered
↓
Normal
```

只是目标值在不断变化：

```text
TargetScale:
1.00
→ 1.03
→ 0.97
→ 1.03
→ 1.00
```

Animation Runtime 保留：

```text
Current
Velocity
Target
Solver
Continuity Policy
```

每次目标变化时直接 **Retarget**，而不是：

```text
Cancel old animation
↓
Create new animation
↓
Restart from predefined From
```

因此系统天然支持：

- Hover 过程中快速离开；
- Press 中途取消；
- 页面转场中再次导航；
- Scroll inertia 中再次滚轮输入；
- Drag 与 Spring 相互接管；
- 侧边栏尚未展开完成时立即收起；
- 多状态快速切换；
- 动态主题/Style Target 中途变化；
- Gesture 驱动值切换为物理动画；
- 物理动画再次被 Gesture 接管。

本模块的核心定义是：

```text
Animation
≠ Task

Animation
= Temporal State Solver
```

---

# 1. 设计目标

## A1. 自然打断

任何正在运行的动画都必须允许在任意时间改变 Target。

不允许因 Retarget 发生可见跳变。

最低要求：

```text
C0 continuity
```

即视觉值连续。

对于位置、缩放、滚动等运动属性，优先保证：

```text
C1 continuity
```

即速度尽可能连续。

---

## A2. 不存在业务层 Animation Callback

业务/UI 页面不允许：

```csharp
Animate(..., onCompleted: ...)
```

生命周期通过：

```text
Target State
Runtime State
Generation
TransitionCompleted Event
```

驱动。

---

## A3. Style 只声明目标，不管理动画实例

例如：

```text
Button:hover:
    Scale = 1.03
```

Style System 只输出：

```text
TargetScale = 1.03
```

它不关心：

```text
是否已有动画
动画运行到哪里
当前速度多少
是否需要反向
```

这些由 Animation Runtime 统一解决。

---

## A4. 每个属性只有一个最终 Animation Channel

同一个：

```text
Entity + Animatable Property
```

不得同时存在多个相互争抢的最终动画。

例如：

```text
Entity #120
Scale
```

只有一个：

```text
AnimationChannel<Scale>
```

Hover、Press、Selection、Navigation 等上层状态只能改变最终 Target。

---

## A5. Hot Path 无 GC

持续动画期间：

```text
0 B/frame
```

不允许：

```text
closure
Task
delegate allocation
LINQ
boxing
runtime expression tree
```

---

## A6. 动画数量决定复杂度

理想复杂度：

```text
O(active animation channels)
```

而不是：

```text
O(all UI entities)
```

---

# 2. 与总体 UI 架构的关系

动画数据流：

```text
Presentation State
        │
        ▼
      Binding
        │
        ▼
Interaction State
        │
        ▼
     Style System
        │
        ▼
Target Visual State
        │
        ▼
Transition Resolver
        │
        ▼
Animation Channels
        │
        ▼
Current Visual State
        │
        ▼
Transform / Render Diff
        │
        ▼
Backend
```

关键边界：

```text
State
↓
Style
↓
Target

Animation
↓
Current
```

Style 不直接写 Current。

Animation 不直接决定业务 State。

---

# 3. Target-driven Animation

传统动画 API：

```csharp
Animate(
    from: 0f,
    to: 1f,
    duration: 200ms
);
```

新架构更接近：

```text
TargetOpacity = 1
```

Runtime 当前可能是：

```text
CurrentOpacity = 0.42
Velocity = +2.1/s
```

Animation Resolver 根据：

```text
Target
Current
Velocity
Animation Definition
Continuity Policy
```

决定下一步如何运动。

因此 Animation 的主要输入不是：

```text
From + To
```

而是：

```text
Current + Velocity + Target
```

---

# 4. Animation Channel

## 4.1 定义

Animation Channel 是：

> 某个 Entity 的某个可动画属性的唯一时间连续状态。

唯一键：

```text
(Entity, PropertyId)
```

例如：

```text
(#120, Opacity)
(#120, ScaleX)
(#120, ScaleY)
(#245, TranslateX)
```

---

## 4.2 Channel 状态

概念模型：

```csharp
public struct AnimationChannelState
{
    public UiEntity Entity;
    public AnimatablePropertyId Property;

    public AnimationValue Current;
    public AnimationValue Target;
    public AnimationValue Velocity;

    public AnimationSolverKind Solver;
    public AnimationContinuity Continuity;

    public uint Generation;
    public UiScopeId Scope;

    public AnimationFlags Flags;
}
```

实际 Hot Store 应采用 SoA，不直接使用此结构数组。

---

# 5. SoA Animation Store

建议：

```text
Entity[]
Property[]

Current[]
Target[]
Velocity[]

SolverKind[]
Continuity[]

Start[]
Elapsed[]
Duration[]
Easing[]

SpringIndex[]
DecayIndex[]

Generation[]
Scope[]

Flags[]
```

不同数值类型可以拆 Store：

```text
FloatAnimationStore
Vector2AnimationStore
ColorAnimationStore
TransformAnimationStore
```

而不是使用 boxed union。

---

# 6. Animatable Property

所有动画不能直接通过字符串：

```text
"Opacity"
"Width"
"Scale"
```

应使用编译期稳定：

```text
AnimatablePropertyId
```

例如：

```text
Opacity
TranslateX
TranslateY
ScaleX
ScaleY
Rotation
BackgroundColor
ForegroundColor
BlurRadius
CornerRadius
ScrollOffset
```

页面不直接看到内部 PropertyId。

---

# 7. Property Registry

每个可动画属性注册：

```text
Value Type
Default Solver
Default Continuity
Blend semantics
Tolerance
Commit Target
```

例如：

| Property | Solver | Continuity |
|---|---|---|
| Opacity | Tween | ContinueFromCurrent |
| BackgroundColor | Tween | ContinueFromCurrent |
| TranslateX/Y | Spring | PreserveVelocity |
| Scale | Spring | PreserveVelocity |
| Rotation | Spring/Tween | PreserveVelocity |
| ScrollOffset | Decay/Spring | MergeVelocity |
| BlurRadius | Tween | ContinueFromCurrent |

---

# 8. Animation Solver

第一版支持三类核心 Solver：

```text
Tween
Spring
Decay
```

后续可增加：

```text
Keyframe
Bezier Path
Timeline
Constraint
Gesture
```

但前三类已覆盖 PCL 大部分 UI 动画。

Solver 与公共入口的兼容关系必须冻结：

| API | 允许的 Motion Solver |
|---|---|
| `Retarget` | `Immediate` / `Tween` / `Spring` |
| `StartDecay` | `Decay` |
| `SetDirect` | 不接受 Motion token，由 API 直接进入 `Direct` |

Solver 与 Continuity 同样是封闭矩阵：

| Solver | 允许的 Continuity |
|---|---|
| `Immediate` | `ContinueFromCurrent` |
| `Tween` | `ContinueFromCurrent` / `PreserveSpeed` |
| `Spring` | `ContinueFromCurrent` / `PreserveVelocity` |
| `Decay` | `ContinueFromCurrent` / `MergeVelocity` |
| `Direct` | 仅由 `SetDirect` 建立，不接受 Motion token |

Runtime 必须先检查 token 的原始 Solver，再应用 Disabled / Reduced Motion policy，且检查
发生在创建 Channel 之前。不兼容组合必须抛出 `ArgumentException`，不得把 `Decay`
塞进 target-driven Retarget，也不得把 Tween/Spring token 强制解释为 Decay。

---

# 9. Tween Solver

Tween 适合：

```text
Opacity
Color
简单 fade
短暂视觉属性
不强调物理速度连续性的属性
```

基本状态：

```text
Start
Current
Target
Elapsed
Duration
Easing
```

---

# 10. Tween Retarget

Tween 被打断时禁止：

```text
Current jump to old From
```

最基本 Retarget：

```text
Start = Current
Target = NewTarget
Elapsed = 0
```

然后根据 Continuity Policy 决定新 Duration。

---

# 11. Tween Continuity Policies

定义：

```csharp
public enum AnimationContinuity : byte
{
    Restart,
    ContinueFromCurrent,
    PreserveRemainingRatio,
    PreserveSpeed,
    PreserveVelocity,
    MergeVelocity
}
```

---

## 11.1 Restart

```text
Start = predefined start
```

仅适用于明确要求重新播放的装饰动画。

默认禁止 UI interaction 使用。

首版 Runtime 尚未冻结 predefined start 的来源，因此该策略仅保留枚举值；构造
`AnimationSpec`、`TransitionDefinition` 或注册 `MotionDefinition` 时必须抛出
`NotSupportedException`，不得静默退化为 `ContinueFromCurrent`。

---

## 11.2 ContinueFromCurrent

```text
Start = Current
Duration = configured duration
```

保证视觉值连续。

适合：

```text
Opacity
Color
Blur
```

---

## 11.3 PreserveRemainingRatio

按照旧动画剩余比例重新映射新动画。

适合某些 timeline 型动画。

不是默认方案。

首版 Runtime 暂不实现 timeline remap，因此该策略仅保留枚举值，并与 `Restart`
一样在契约入口明确拒绝。

---

## 11.4 PreserveSpeed

估算当前运动速度：

```text
speed = |dValue/dt|
```

新 Duration：

```text
duration ≈ distance(Current, NewTarget) / speed
```

并施加：

```text
MinDuration
MaxDuration
```

适合：

```text
Progress
simple geometry
```

---

## 11.5 PreserveVelocity

保留一阶导数。

主要用于：

```text
Spring
Position
Scale
Navigation
```

---

## 11.6 MergeVelocity

将新输入速度与当前速度合并。

主要用于：

```text
Scroll
Pan
Fling
```

---

# 12. Tween 反向

场景：

```text
Opacity 1 → 0.8
```

进行到：

```text
0.89
```

突然 Target：

```text
1.0
```

必须从：

```text
0.89
```

立即反向。

不能：

```text
0.8 → 1.0
```

也不能：

```text
1.0 → 1.0
```

Animation Channel 保证：

```text
Current 是唯一真实视觉起点
```

---

# 13. Spring Solver

Spring 是 `PCL.UI.Next` 自然打断动画的核心。

适合：

```text
Translate
Scale
Sidebar
Card movement
Scroll settling
Drag release
Overscroll
Page transition
```

---

# 14. Spring 状态

概念：

```csharp
public struct SpringState
{
    public float Position;
    public float Velocity;
    public float Target;

    public float Stiffness;
    public float Damping;
    public float Mass;
}
```

核心方程：

```text
Fspring = -k(x - target)
Fdamping = -cv
```

系统根据固定/稳定积分方案更新：

```text
Velocity
Position
```

---

# 15. Spring 的自然 Retarget

Spring 在：

```text
Position = 0.62
Velocity = +2.7
Target = 1.0
```

时收到：

```text
Target = 0
```

只修改：

```text
Target
```

保留：

```text
Position
Velocity
```

结果：

```text
继续向前一小段
↓
减速
↓
反向
↓
收敛到 0
```

这是自然打断的理想语义。

---

# 16. Spring 参数表示

不建议页面直接配置：

```text
stiffness
damping
mass
```

因为页面作者难以形成统一视觉语言。

Authoring API 应提供语义 Token：

```text
Motion.Fast
Motion.Standard
Motion.Gentle
Motion.Bouncy
Motion.Navigation
Motion.ScrollBoundary
```

Theme/Motion System 映射到具体参数。

---

# 17. Motion Tokens

建议：

```text
Motion.Instant
Motion.Fast
Motion.Standard
Motion.Emphasized
Motion.Navigation
Motion.Overlay
Motion.SpringSubtle
Motion.SpringResponsive
Motion.SpringExpressive
```

Token 可以包含：

```text
Solver
Duration
Easing
Spring configuration
Continuity
Tolerance
```

---

# 18. Spring Stability

必须避免简单 Euler 在：

```text
低 FPS
大 dt
窗口拖拽卡顿
debug breakpoint
```

下爆炸。

建议：

- semi-implicit Euler；
- analytic damped spring；
- fixed subdivision；
- dt clamp。

第一版优先：

```text
analytic / stable spring solution
```

避免依赖非常小的 dt。

---

# 19. Large Delta Time

如果 Runtime 停顿：

```text
dt = 300ms
```

不能直接进行单次不稳定积分。

策略：

```text
dt = min(dt, MaxSimulationDelta)
```

或者：

```text
substep
```

并设置：

```text
MaxSubsteps
```

如果超过阈值：

```text
fast-forward toward stable state
```

避免恢复窗口时出现巨幅弹飞。

---

# 20. Spring Rest Detection

当：

```text
|Target - Current| < positionTolerance
```

且：

```text
|Velocity| < velocityTolerance
```

则：

```text
Current = Target
Velocity = 0
Channel = Sleeping
```

停止 request continuous frame。

---

# 21. Decay / Inertia Solver

用于：

```text
Scroll
Pan
Drag release
Fling
```

状态：

```text
Position
Velocity
Friction
```

每帧：

```text
Velocity decays
Position integrates
```

---

# 22. Decay → Spring

滚动越界：

```text
Decay
↓
Boundary crossed
↓
Spring target = nearest valid boundary
```

保留：

```text
Position
Velocity
```

形成自然 Overscroll 回弹。

---

# 23. Gesture → Decay

拖拽中：

```text
Current = Gesture Position
```

释放：

```text
Velocity = Gesture Release Velocity
Solver = Decay
```

无需重新创建视觉状态。

---

# 24. Gesture → Spring

例如侧边栏拖到 70% 后释放：

```text
Current = gesture position
Velocity = release velocity
```

System 根据：

```text
position
velocity
threshold
```

决定：

```text
Target = Open
or
Target = Closed
```

切换到 Spring。

---

# 25. Animation Ownership

每个 Channel 必须拥有：

```text
Scope
Generation
OwnerReason
```

例如：

```text
StyleTransition
Navigation
Gesture
Scroll
Programmatic
```

这有利于 DevTools 与生命周期控制。

---

# 26. Generation

Channel 不一定每次 Retarget 都重建，但逻辑目标版本必须增加：

```text
TargetGeneration++
```

异步/完成事件记录：

```text
Generation
```

旧完成事件不能影响新状态。

---

# 27. Completed Event

完成不是 callback。

发布：

```text
AnimationSettled
```

携带：

```text
Entity
Property
Generation
Target
Scope
```

消费者自行检查是否仍然相关。

完成事件必须进入持久的 Runtime event journal / queue，并至少额外携带：

```text
Sequence
FrameIndex
```

事件只能由消费者显式 `Drain` 后移除。禁止在 `TransitionPlanning` 等帧中间阶段
调用 `Clear()`，因为 Input / Gesture 可能已经在更早阶段发布 Immediate completion。

---

# 28. Transition Group

某些状态完成依赖多个属性：

例如页面退出：

```text
Opacity
TranslateX
Scale
```

不能监听三个独立 callback。

定义：

```text
TransitionGroupId
```

Group 完成条件：

```text
all required channels settled
```

发布：

```text
TransitionGroupCompleted
```

---

# 29. Transition Group Generation

页面每次导航：

```text
NavigationGeneration
```

创建对应：

```text
TransitionGroup(gen=51)
```

旧：

```text
TransitionGroupCompleted(gen=50)
```

直接丢弃。

---

# 30. Style Transition Resolution

Style System 输出：

```text
Resolved Target Style
```

例如：

```text
Scale = 0.97
Opacity = 0.85
Background = PressedBrush
```

Transition Resolver 对比：

```text
Current Target Style
vs
New Target Style
```

判断哪些属性：

```text
Immediate
Tween
Spring
```

---

# 31. Target 与 Current 分离

必须明确存在：

```text
TargetOpacity
CurrentOpacity
```

概念上不能让：

```text
Opacity Component
```

同时承担两种语义。

实现可选择：

```text
StyleTargetStore
VisualCurrentStore
```

或：

```text
ResolvedStyleTarget
ComputedVisualStyle
```

---

# 32. Immediate Property

不是所有属性都动画。

例如：

```text
Visibility
HitTestEnabled
SemanticRole
```

通常立即应用。

Property Registry 标记：

```text
Animatable = false
```

---

# 33. Animation Suppression

需要支持：

```text
DisableAnimations
ReducedMotion
InitialMount
FastForward
Testing
```

例如：

```text
System Reduced Motion
```

时某些 Motion Token 转为：

```text
Instant
```

或更短动画。

---

# 34. Initial Mount

首次创建 Entity 时不应该自动从默认值动画到 Style Target。

例如：

```text
Opacity default = 0
Resolved target = 1
```

不能导致所有初始控件自动 fade in。

默认：

```text
InitialMount
→ snap Current = Target
```

只有 Blueprint 显式声明：

```text
EnterTransition
```

才动画进入。

---

# 35. Removed Entity / Exit Animation

如果一个 subtree 逻辑上被移除，但有 Exit Transition：

不能立即 Destroy Entity。

流程：

```text
Structural remove requested
↓
mark Leaving
↓
detach from logical interaction
↓
keep render/layout snapshot as needed
↓
run exit transition
↓
TransitionGroupCompleted
↓
Destroy subtree
```

---

# 36. Leaving State

离开中的 Entity 默认：

```text
HitTest disabled
Focus released
Business binding frozen
```

但：

```text
Render state retained
Animation active
```

避免退出动画期间业务状态继续修改旧 UI。

---

# 37. Natural Interruption of Exit

如果离开过程中结构条件又重新成立：

可以支持：

```text
Leaving
↓
Reentering
```

复用原 Entity/Subtree：

```text
Current visual state preserved
↓
retarget enter state
```

比 destroy/recreate 更自然。

---

# 38. Navigation Animation

页面 Transition 不应是：

```text
await FadeOut(A)
await FadeIn(B)
```

而是目标状态：

```text
A.Role = Leaving
B.Role = Active
```

Navigation Style 定义：

```text
Active:
    opacity = 1
    translateX = 0

Entering:
    opacity = 0
    translateX = +24

Leaving:
    opacity = 0
    translateX = -16
```

Animation Runtime 负责连续求解。

---

# 39. Navigation 中再次导航

场景：

```text
A → B
```

进行一半：

```text
A current opacity = 0.55
B current opacity = 0.45
```

用户立即：

```text
→ C
```

Navigation System 不等待旧动画完成。

直接：

```text
A target = offscreen/dispose
B target = leaving
C target = active
```

B 从当前：

```text
0.45
```

继续离开。

整个过程无跳变。

---

# 40. Navigation Scope

A/B/C 各自 PageScope。

Navigation Transition Group 只引用对应 Scope + Generation。

旧页面可以在：

```text
visual exit settled
```

后销毁。

---

# 41. Sidebar 动画

推荐使用：

```text
layout fixed
+
transform/clip animation
```

例如：

```text
Sidebar Width = 280
```

关闭时：

```text
TranslateX = -280
ClipWidth = 0/visual clip
```

打开目标：

```text
TranslateX = 0
```

快速开关时 Spring Retarget。

不动画：

```text
Width: 0 → 280
```

---

# 42. Hover/Press 状态

Button：

```text
Normal:
    scale = 1.00

Hovered:
    scale = 1.025

Pressed:
    scale = 0.965
```

在 100ms 内：

```text
Normal
→ Hovered
→ Pressed
→ Hovered
→ Normal
```

只产生 Target 更新。

Animation Channel 始终唯一。

---

# 43. Property Priority

若多个上层系统可能提出目标，需要明确优先级。

例如：

```text
Base Style
Hover
Press
Disabled
Navigation
Gesture
```

不是由 Animation 解决。

应在 Style/State Resolution 阶段得到唯一：

```text
Final Target
```

Animation Runtime 不做多来源仲裁。

---

# 44. Gesture Override

Gesture 是特殊情况。

拖动时：

```text
Solver = Direct/Gesture
```

直接驱动 Current。

此时 Style Target 仍可存在，但不生效。

Gesture release：

```text
Resolver restores target-driven solver
```

并将：

```text
Gesture velocity
```

传入 Spring/Decay。

---

# 45. Direct Solver

增加：

```text
AnimationSolverKind.Direct
```

语义：

```text
Current = externally provided value
```

用于：

```text
drag
scrub
interactive page back gesture
slider
```

---

# 46. Interactive Transition

页面转场未来可支持：

```text
Progress 0..1
```

由 Gesture 直接驱动。

释放时：

```text
progress
velocity
```

决定：

```text
complete
or
cancel
```

然后切换 Spring 到：

```text
TargetProgress = 1
or
0
```

---

# 47. Composite Transform

不要为：

```text
ScaleX
ScaleY
TranslateX
TranslateY
Rotation
```

每次都重建 Matrix。

建议运行时保存结构化 Transform Channel。

最终 Transform System 一次合成：

```text
Layout Transform
× Animation Transform
× User Transform
× Scroll Transform
```

---

# 48. Transform Composition Order

必须冻结顺序，例如：

```text
Layout Placement
↓
Scroll Offset
↓
FLIP Inversion
↓
Transition Translate
↓
Scale/Rotation around origin
↓
Backend Matrix
```

不能不同 Widget 自己随意组合。

---

# 49. FLIP 与自然打断

FLIP 动画也必须使用 Channel。

流程：

```text
First Rect
Last Rect
↓
calculate Inverse Transform
↓
Current FLIP Transform = inverse
Target = identity
```

如果布局再次变化：

```text
读取当前视觉 transform
↓
计算新的 Last
↓
重新构造等效 inverse
↓
保持 visual continuity
```

禁止重新从旧 First 开始。

嵌套 FLIP 必须先求 Entity 所需的 world-space inverse，再移除父节点已经承担的
当前 world transform：

```text
LocalFlip = DesiredWorldFlip
          × inverse(ParentCurrentWorld)
          × inverse(CurrentStyleTransform)
```

因此，完全随父节点同比例变化的子节点得到 `LocalFlip = Identity`，不会与父节点
重复补偿。首版使用六个 float channel 表达完整 `Matrix3x2` local delta，避免把
world-space Rect 的 scale/translate 直接逐节点叠乘。

---

# 50. Layout Change During Animation

如果动画过程中布局变动：

例如：

```text
Card translating
+
Window resize
```

Runtime 应保持：

```text
visual position continuity
```

而不是：

```text
layout rect changes
↓
visual suddenly jumps
```

可以通过：

```text
world-space current transform
→ recompute local transform
```

完成 rebasing。

---

# 51. Animation Rebase

Rebase 指：

> 基础坐标系变化后重新表达动画 Current，使屏幕空间结果不变。

适用：

```text
Window resize
Layout update
Parent transform update
Virtualized item rebind
DPI change
```

---

# 52. Color Animation

颜色动画不能简单对任意字节做插值。

应明确颜色空间。

建议：

```text
linear-light color space
```

或者至少统一固定算法。

不允许不同 Backend 各自决定颜色插值语义。

---

# 53. Brush Animation

第一版可支持：

```text
SolidColor → SolidColor
```

复杂 Brush：

```text
Gradient
ImageBrush
```

优先：

```text
crossfade
```

而不是直接复杂参数逐项 morph。

---

# 54. Discrete Properties

例如：

```text
Visibility
Icon kind
Font family
```

采用：

```text
Discrete
```

在：

```text
start
midpoint
end
```

某一固定策略切换。

---

# 55. Animation Value Types

第一版建议：

```text
float
Vector2
Color
Transform scalar components
```

暂不设计任意泛型动画对象。

理由：

```text
减少分支
避免 boxing
便于 SIMD
便于 Backend Diff
```

---

# 56. Easing

Tween easing 应使用稳定 ID：

```text
Linear
EaseIn
EaseOut
EaseInOut
CubicBezier
```

预定义 UI Token：

```text
MotionEase.Standard
MotionEase.Emphasized
MotionEase.Decelerate
MotionEase.Accelerate
```

页面不应到处自定义随机 Bézier。

---

# 57. Motion Design Consistency

Motion 参数属于 Theme/Motion System。

统一：

```text
Duration.Fast
Duration.Standard
Duration.Slow

Spring.Subtle
Spring.Responsive
Spring.Navigation
```

使整个 PCL 动画语言一致。

---

# 58. Reduced Motion

Runtime 必须能接收：

```text
ReducedMotion = true
```

策略：

```text
Decorative motion → disabled
Large spatial movement → fade / shorter
Essential progress → retained
Scroll inertia → reduced
```

不能简单把所有 animation duration 设 0 而破坏必要状态反馈。

---

# 59. Continuous Frame Ownership

Frame Scheduler 维护：

```text
ActiveAnimationCount
ActiveDecayCount
ActiveInteractiveTransitions
```

只要：

```text
count > 0
```

才保持 Continuous Frame。

全部 settled：

```text
release continuous frame
```

---

# 60. Animation Tick Ordering

推荐：

```text
Style Resolve
↓
Transition Planning
↓
Animation Tick
↓
Transform Composition
↓
Render Diff
```

目标在本帧变化时，可以立刻进入首个动画 sample。

---

# 61. Fixed Step vs Variable Step

UI animation 推荐：

```text
variable dt
+
stable solver
+
dt clamp
```

不必强制游戏式固定 60Hz simulation。

原因：

```text
显示器 60/120/144/240Hz
后台恢复
窗口合成调度不同
```

应尊重实际 frame timestamp。

---

# 62. Presentation Time

如果 Backend 能提供：

```text
presentation timestamp
```

优先使用接近真实展示时间的时钟。

否则使用 Runtime monotonic clock。

禁止 wall clock。

每个活动 Channel 保存自己的：

```text
LastSampleTimestamp
```

Retarget / Direct / Decay 接管时以 monotonic clock 重置该时间；Tick 使用
`Frame.Now - LastSampleTimestamp`。禁止用 World frame index 猜测 Retarget 是否
发生在帧内，否则 idle 后的首个 reactive frame 会错误吞入整段 idle delta。

---

# 63. Frame Drop

动画不应：

```text
一帧一卡
↓
动画整体变慢
```

时间型 Tween 应按真实 elapsed time 前进。

Spring 应用稳定积分。

---

# 64. Snap Tolerance

浮点动画需要：

```text
PositionTolerance
VelocityTolerance
ColorTolerance
```

否则可能无限产生：

```text
0.9999998
```

导致 Continuous Frame 永不停止。

---

# 65. Same Target Optimization

新目标若：

```text
ApproximatelyEqual(CurrentTarget, NewTarget)
```

则：

```text
no retarget
no generation increment
```

避免 Hover 重复事件不断重置动画。

---

# 66. Current Already at Target

如果：

```text
Current ≈ Target
Velocity ≈ 0
```

则：

```text
snap exact target
sleep
```

不创建 Channel activity。

---

# 67. Target Change While Sleeping

Sleeping Channel 收到新 Target：

```text
activate
↓
register in ActiveSet
↓
request continuous frame
```

---

# 68. Active Set

AnimationStore 不扫描全部 Channel。

维护：

```text
ActiveChannelIndices[]
```

Channel sleep：

```text
swap-remove from active set
```

激活：

```text
append
```

---

# 69. Channel Lifetime

对于长期存在的 Entity 属性：

Channel 可以：

```text
lazy create
```

settled 后：

```text
保留 compact channel
```

或：

```text
延迟回收
```

具体用 Benchmark 决定。

---

# 70. Short-lived Animation

粒子/装饰性一次性动画若未来存在，可使用：

```text
Ephemeral Animation Entity
```

但不能污染核心 Widget animation model。

---

# 71. Animation System API

内部 API 可设计：

```csharp
public interface IAnimationSystem
{
    void Retarget(
        UiEntity entity,
        AnimatablePropertyId property,
        in AnimationValue target,
        in AnimationSpec spec);

    void SetDirect(
        UiEntity entity,
        AnimatablePropertyId property,
        in AnimationValue current,
        in AnimationValue velocity);

    void ReleaseDirect(
        UiEntity entity,
        AnimatablePropertyId property,
        in AnimationSpec resumeSpec);

    void Tick(UiDuration delta);
}
```

页面层不可直接调用。

---

# 72. AnimationSpec

```csharp
public readonly struct AnimationSpec
{
    public readonly AnimationSolverKind Solver;
    public readonly AnimationContinuity Continuity;

    public readonly MotionToken Motion;

    public readonly AnimationFlags Flags;
}
```

具体 stiffness/duration 可以通过 MotionToken lookup。

---

# 73. Transition Definition

Style 层：

```text
Property
MotionToken
Continuity Override
```

例如：

```text
Opacity:
    Motion.FastFade

Scale:
    Motion.SpringResponsive
```

---

# 74. Animation Flags

可能包括：

```text
Essential
AllowReducedMotion
AllowRetarget
AllowRebase
CompositorEligible
AffectsHitTest
```

---

# 75. Compositor Eligibility

某些属性：

```text
Opacity
Translate
Scale
Rotation
Clip
```

可以由 Backend compositor 直接执行。

但架构上要谨慎：

> 如果动画状态完全下放 Backend，Runtime 可能失去准确 Current/Velocity。

因此自然打断要求 Runtime 至少能够获得：

```text
current presentation state
```

或采用 Runtime authoritative simulation。

---

# 76. Runtime-authoritative vs Backend-authoritative

推荐第一版：

```text
Runtime authoritative
```

即：

```text
Animation Runtime 计算 Current
↓
Backend commit
```

优点：

- Retarget 精确；
- Replay deterministic；
- Current/Velocity 始终可知；
- Backend 可替换。

未来优化可对特定纯 compositor 动画下放。

---

# 77. Compositor Offload

若以后支持：

```text
Backend-authoritative animation
```

Backend 必须至少提供：

```text
interrupt at presentation value
query/snapshot current
cancel without visual jump
```

否则不能破坏自然打断语义。

---

# 78. Scroll Input Merge

滚轮输入：

```text
delta
```

不应该每次：

```text
cancel old scroll animation
start new
```

而是：

```text
Target += delta
```

或：

```text
Velocity += impulse
```

取决于 Scroll Motion 模式。

---

# 79. Scroll Modes

可支持：

```text
TargetSpring
VelocityImpulse
DirectTrackpad
```

鼠标滚轮：

```text
TargetSpring
```

高精度触控板：

```text
DirectTrackpad
↓ release
Decay
```

---

# 80. Hover Delay / Tooltip

Tooltip delay 不是 Animation。

应由：

```text
Timer/Scheduler
```

控制。

动画系统只处理：

```text
Tooltip visual enter/exit
```

避免把所有时间行为都塞进 Animation。

---

# 81. Progress Animation

真实进度：

```text
0.41 → 0.62
```

Visual progress 可平滑：

```text
Current → Target
```

推荐：

```text
PreserveSpeed
```

但绝不能使视觉进度超越业务 Target。

---

# 82. Loading Indeterminate

循环动画属于：

```text
Timeline/Periodic
```

它是少数真正需要：

```text
time-domain repeating animation
```

的场景。

应独立于 Target-driven property transition。

---

# 83. Timeline Animation

第一版如需要，可支持轻量：

```text
PeriodicTimeline
```

用于：

```text
spinner
skeleton shimmer
caret blink
```

但：

```text
UI interaction transition
```

仍必须走 Target-driven Channel。

---

# 84. Animation Composition

原则：

```text
one final channel per property
```

但某些效果需要多层 Transform。

应通过不同语义层：

```text
BaseTransform
LayoutTransitionTransform
InteractionTransform
GestureTransform
```

最终 Transform System 合成。

不要通过多个 animation 同时写一个 `Scale` 值。

---

# 85. Transform Layers

推荐：

```text
LayoutTransform
ScrollTransform
FlipTransform
NavigationTransform
InteractionTransform
GestureTransform
UserTransform
```

并固定 composition order。

这样 Navigation 与 Hover 可以同时存在，而不是争抢同一个 Translate。

---

# 86. Opacity Layers

Opacity 可类似：

```text
BaseOpacity
StateOpacity
TransitionOpacity
ParentOpacity
```

最终：

```text
ResolvedOpacity = product/clamped combination
```

具体层数应控制，避免过度复杂。

---

# 87. 多动画共存原则

能通过不同语义通道组合的，组合；

对同一语义最终属性的多个来源，必须在 Animation 前 Resolve。

---

# 88. Press vs Hover

例如：

```text
Hovered Scale = 1.02
Pressed Scale = 0.96
```

Pressed 优先级更高。

不是：

```text
Hover animation + Press animation 相加
```

而是：

```text
Interaction State
↓
Final TargetScale = 0.96
```

---

# 89. Selected + Hover

可以由 Style Resolver计算：

```text
Selected+Hovered
```

最终目标。

Animation 仍只看到一个 Target。

---

# 90. Parent/Child Animation

父节点 Transform 动画时：

```text
children no need retarget
```

Render Transform 继承即可。

不应把父 Transform 展开写进所有 Child。

---

# 91. Hit Test During Animation

如果属性影响视觉位置：

```text
Translate
Scale
```

Hit Test 必须使用当前视觉 Transform。

否则出现：

```text
按钮画在 A
点击区域还在 B
```

Transform System 每帧更新对应 Hit Test node。

---

# 92. Layout vs Hit Test

如果只是 compositor transform：

```text
LayoutRect 不变
```

Hit Test 使用：

```text
LayoutRect × CurrentTransform
```

不需要重新 Layout。

---

# 93. Focus During Exit

页面/控件进入 Leaving：

```text
Focus immediately transferred or released
```

不能等待视觉动画结束。

动画是视觉生命周期，Focus 属于交互生命周期。

---

# 94. Accessibility During Exit

Leaving subtree 默认可以：

```text
从 Semantic Tree 移除
```

即使 Render Tree 继续显示退出动画。

这再次说明：

```text
Semantic lifecycle
≠
Visual lifecycle
```

---

# 95. Virtualized Item Animation

Virtualized item 被回收前若仍在动画：

默认：

```text
cancel visual channel
reset pooled visual state
```

重新绑定时：

```text
Current = new item's initial target
```

防止动画状态泄漏到新 item。

---

# 96. Stable Item Key

如果列表 item 因排序移动，但逻辑 item 仍存在：

使用：

```text
Stable Key
```

保留其 Animation State，支持：

```text
move animation / FLIP
```

---

# 97. Collection Reorder

列表排序：

```text
old layout
↓
new layout
↓
FLIP
```

每个稳定 key item 计算视觉差异。

中途再次排序：

```text
Rebase from current visual state
```

自然打断旧 move animation。

---

# 98. Theme Change During Animation

例如 Button 正在：

```text
Background A → Hover A
```

主题切换后：

```text
Background B / Hover B
```

Style Target 更新。

Animation Channel 从当前实际颜色继续到新的 Target。

不先完成旧主题动画。

---

# 99. DPI Change During Animation

Position/Scale 动画应使用：

```text
logical pixels
```

DPI 变化只影响 Backend physical mapping。

尽量避免动画数值本身跳变。

---

# 100. Window Resize During Navigation

必须支持：

```text
Navigation transition
+
Window resize
```

Layout 先计算新 geometry。

Animation Rebase 保持当前视觉连续。

---

# 101. Cancellation

内部仍可能需要强制 Cancel，例如：

```text
Scope disposed
Entity destroyed
Fatal reset
ReducedMotion switch
```

Cancel 模式：

```text
SnapToCurrent
SnapToTarget
Discard
```

正常交互 Retarget 不应调用 Cancel。

---

# 102. Scope Disposal

Scope Dispose 时：

```text
remove active channels
remove transition groups
invalidate pending completed events
```

不触发业务 callback。

---

# 103. Animation Debug Data

Debug Build 每个 channel 可暴露：

```text
Entity
Property
Current
Velocity
Target
Solver
Continuity
Generation
Scope
Age
Settled threshold
Last retarget reason
```

---

# 104. Animation Inspector

DevTools UI：

```text
Entity #120 / Scale

Current:    0.991
Velocity:  -0.84/s
Target:     0.965

Solver:     Spring
Motion:     Responsive
Generation: 42

State source:
Hovered + Pressed

Last retarget:
PointerPressed
```

---

# 105. Motion Trace

记录：

```text
t=0    target 1.00
t=20   target 1.025
t=55   target 0.965
t=96   target 1.025
t=131  target 1.00
```

用于检查自然打断效果。

---

# 106. Curve Visualizer

DevTools 可绘制：

```text
Current
Target
Velocity
```

随时间曲线。

便于调 Spring 参数。

---

# 107. Deterministic Animation Test

使用：

```text
FakeClock
```

测试：

```text
Set target A
Advance 40ms
Set target B
Advance 20ms
```

断言：

```text
Current continuous
No jump
Generation updated
Correct final settlement
```

---

# 108. Retarget Invariant

核心测试不变量：

```text
Current_before_retarget
≈
Current_after_retarget
```

即 Retarget 操作本身不能改变 Current。

---

# 109. Velocity Invariant

对于 `PreserveVelocity`：

```text
Velocity_before
≈
Velocity_after
```

除非 Solver 转换语义明确要求映射。

---

# 110. Solver Switch

例如：

```text
Direct Gesture
↓
Spring
```

需要明确定义速度转换。

```text
Gesture release velocity
↓
Spring initial velocity
```

而：

```text
Tween
↓
Spring
```

可以估算 Tween 当前 derivative 作为 Spring velocity。

---

# 111. Tween Derivative

若 easing 可求导：

```text
v = derivative(easing, t) * distance / duration
```

Retarget 到 Spring 时保存这个速度。

这样 Tween → Spring 也能更自然。

---

# 112. Color Velocity

颜色通常不需要 PreserveVelocity。

默认：

```text
ContinueFromCurrent
```

即可。

---

# 113. Rotation Wrap

Rotation Retarget 必须选择：

```text
shortest path
clockwise
counterclockwise
absolute
```

Property metadata 中定义。

---

# 114. Scroll Velocity Clamp

滚动连续输入时 Velocity 可能不断增加。

必须：

```text
MaxVelocity
```

防止极端 wheel spam 造成不可控运动。

---

# 115. Spring Overshoot

不同 Motion Token 可控制：

```text
AllowOvershoot
OvershootClamp
```

例如：

```text
Opacity
```

绝不能物理 overshoot 到：

```text
1.08
```

因此 Opacity 不建议 Spring，或必须 clamp。

---

# 116. Value Constraints

Property Registry 定义：

```text
ClampMin
ClampMax
Wrap
Normalize
```

例如：

```text
Opacity = [0,1]
Scale > 0
CornerRadius >= 0
```

---

# 117. Constraint Timing

约束应在：

```text
solver output
↓
property commit
```

前应用。

但某些 Spring 若直接 clamp Current 会破坏速度，需要对应的 boundary policy。

---

# 118. Motion Token Example

概念示例：

```text
Motion.FastFade
    Solver       = Tween
    Duration     = 100ms
    Easing       = Standard
    Continuity   = ContinueFromCurrent

Motion.HoverScale
    Solver       = Spring
    Response     = Fast
    Damping      = High
    Continuity   = PreserveVelocity

Motion.Navigation
    Solver       = Spring
    Response     = Standard
    Damping      = MediumHigh
    Continuity   = PreserveVelocity

Motion.Scroll
    Solver       = Decay/Spring
    Continuity   = MergeVelocity
```

具体数值应通过 Playground 调参后冻结。

---

# 119. Authoring Example

页面/Widget 层概念 API：

```csharp
Ui.Button("启动")
    .Style(ButtonStyle.Default)
    .Transition(
        UiProperty.Opacity,
        Motion.FastFade)
    .Transition(
        UiProperty.Scale,
        Motion.HoverScale);
```

仍然不显式：

```text
StartAnimation
```

---

# 120. Style Example

```text
Button.Normal
    Scale = 1.00
    Opacity = 1.00

Button.Hovered
    Scale = 1.02

Button.Pressed
    Scale = 0.96
```

Animation Runtime 自动处理。

---

# 121. Navigation Example

```text
Page.Active
    Opacity = 1
    TranslateX = 0

Page.Entering
    Opacity = 0
    TranslateX = 24

Page.Leaving
    Opacity = 0
    TranslateX = -16
```

Navigation System 只切换 role。

---

# 122. Slider Example

拖动：

```text
Direct Solver
Current = pointer-mapped position
```

释放：

```text
Target = nearest snap value
Spring
Velocity = release velocity
```

---

# 123. Scroll Example

Wheel：

```text
Velocity/Target receives impulse
```

继续滚轮：

```text
merge into existing channel
```

不 cancel/restart。

---

# 124. Animation Pipeline

完整：

```text
Interaction State Change
        │
        ▼
Style Resolve
        │
        ▼
New Target Values
        │
        ▼
Transition Resolver
        │
        ├─ Same target → ignore
        ├─ Immediate → snap
        └─ Animated → retarget channel
                    │
                    ▼
             Animation Tick
                    │
                    ▼
             Current Values
                    │
                    ▼
              Transform/Visual
                    │
                    ▼
               Render Diff
```

---

# 125. System Ordering

建议：

```text
StyleSystem
TransitionPlanningSystem
AnimationRetargetSystem
AnimationTickSystem
AnimationSettlementSystem
TransformSystem
RenderDiffSystem
```

Settlement Event 在本帧结束前统一发布。

---

# 126. Retarget Queue

Style/System 不应直接在遍历期间修改 ActiveStore 结构。

可写：

```text
AnimationRetargetQueue
```

包含：

```text
Entity
Property
Target
Motion
Source
```

统一由 RetargetSystem 合并。

---

# 127. Retarget Coalescing

同一帧对同一个：

```text
Entity + Property
```

有多次 Target 更新：

只保留最终解析结果。

例如：

```text
Hover
Press
Disable
```

同一帧发生，最终：

```text
Disabled Target
```

避免创建中间无意义动画。

---

# 128. Immediate State vs Animated State

逻辑状态改变必须立即完成。

例如：

```text
Disabled = true
```

Interaction 立刻禁止点击。

视觉：

```text
Opacity
Color
```

可以随后动画。

不能等 Disabled fade 完才真的 disabled。

---

# 129. Visual Lag Principle

Animation 允许视觉状态滞后于逻辑 Target：

```text
Logical Pressed = false
Visual Scale still returning
```

这是正常的。

因此：

```text
Current Visual State
```

绝不能被业务层当成逻辑状态来源。

---

# 130. Reading Animated Values

页面层禁止同步读取：

```text
CurrentOpacity
CurrentScale
```

只有内部：

```text
Renderer
HitTest
DevTools
Gesture takeover
```

可以访问。

否则业务会和动画时间耦合。

---

# 131. Animation as Derived State

最终原则：

```text
Logical State
↓
Target Visual State
↓
Current Visual State
```

Current 是派生的短生命周期状态。

---

# 132. Benchmark

必须加入：

## ANIM-B1

```text
500 active float tween
```

## ANIM-B2

```text
500 active spring
```

## ANIM-B3

```text
5000 active spring
```

## ANIM-B4

快速 Retarget：

```text
1000 channels
100 target changes/sec
```

## ANIM-B5

Hover storm：

```text
pointer crosses 1000 buttons rapidly
```

## ANIM-B6

Navigation interruption：

```text
A→B→C→D
within 300ms
```

## ANIM-B7

Scroll impulse merge。

目标：

```text
0 B/frame
```

---

# 133. Correctness Tests

必须测试：

- [ ] Retarget 不改变 Current；
- [ ] PreserveVelocity 不无故清零 Velocity；
- [ ] Old Generation Completed Event 被丢弃；
- [ ] Scope Dispose 后无活动 Channel；
- [ ] Same Target 不重新启动；
- [ ] Spring 最终 exact snap Target；
- [ ] Continuous Frame 在全部 settled 后停止；
- [ ] Gesture → Spring 保留 release velocity；
- [ ] Layout Rebase 无视觉跳变；
- [ ] Virtualized recycled item 不继承旧动画；
- [ ] Reduced Motion 行为正确；
- [ ] Theme Change 中途 Retarget 无跳变。

---

# 134. Architecture Red Lines

禁止：

```csharp
await AnimateAsync(...);
```

用于 UI 生命周期。

禁止：

```csharp
animation.Completed += ...
```

驱动业务。

禁止：

```csharp
Cancel();
StartNew();
```

作为正常 Hover/Press 打断实现。

禁止：

```text
多个动画同时直接写同一个最终属性
```

禁止：

```text
Style System 直接写 Current Visual Value
```

禁止：

```text
动画 Width/Height 作为常规过渡
```

禁止：

```text
每帧扫描全部 Entity 查动画
```

---

# 135. 推荐代码结构

```text
PCL.UI.Next.Animation/
├─ AnimationSystem.cs
├─ AnimationStore.cs
├─ AnimationChannel.cs
├─ AnimationPropertyRegistry.cs
├─ AnimationRetargetQueue.cs
├─ AnimationRetargetSystem.cs
├─ AnimationTickSystem.cs
├─ AnimationSettlementSystem.cs
│
├─ Solvers/
│  ├─ TweenSolver.cs
│  ├─ SpringSolver.cs
│  ├─ DecaySolver.cs
│  └─ DirectSolver.cs
│
├─ Motion/
│  ├─ MotionToken.cs
│  ├─ MotionRegistry.cs
│  ├─ Easing.cs
│  └─ SpringConfiguration.cs
│
├─ Transition/
│  ├─ TransitionDefinition.cs
│  ├─ TransitionResolver.cs
│  ├─ TransitionGroup.cs
│  └─ TransitionGroupSystem.cs
│
├─ Rebase/
│  ├─ AnimationRebaseSystem.cs
│  └─ FlipAnimationSystem.cs
│
└─ Diagnostics/
   ├─ AnimationTrace.cs
   └─ AnimationDebugSnapshot.cs
```

---

# 136. 建议核心类型

```csharp
public enum AnimationSolverKind : byte
{
    Immediate,
    Tween,
    Spring,
    Decay,
    Direct
}
```

```csharp
public enum AnimationContinuity : byte
{
    Restart,
    ContinueFromCurrent,
    PreserveRemainingRatio,
    PreserveSpeed,
    PreserveVelocity,
    MergeVelocity
}
```

```csharp
public readonly struct AnimationTarget
{
    public readonly UiEntity Entity;
    public readonly AnimatablePropertyId Property;
    public readonly AnimationValue Value;
    public readonly MotionToken Motion;
}
```

---

# 137. Motion Token API

页面只应该选择：

```text
Motion.FastFade
Motion.Hover
Motion.Press
Motion.Navigation
Motion.Scroll
Motion.Overlay
```

而不是配置：

```text
Damping = 23.18
Stiffness = 481
```

具体参数属于设计系统与 Runtime 调优。

---

# 138. 默认策略建议

| 场景 | Solver | Continuity |
|---|---|---|
| Opacity | Tween | ContinueFromCurrent |
| Color | Tween | ContinueFromCurrent |
| Hover Scale | Spring | PreserveVelocity |
| Press Scale | Spring | PreserveVelocity |
| Card Translate | Spring | PreserveVelocity |
| Sidebar | Spring | PreserveVelocity |
| Page Navigation | Spring + Tween | PreserveVelocity |
| Scroll Wheel | Spring/Impulse | MergeVelocity |
| Trackpad | Direct → Decay | PreserveVelocity |
| Overscroll | Spring | PreserveVelocity |
| Progress | Tween | PreserveSpeed |
| Modal Fade | Tween | ContinueFromCurrent |
| Popup Transform | Spring | PreserveVelocity |

---

# 139. 自然打断的定义

`PCL.UI.Next` 中“自然打断”正式定义为：

> 当目标视觉状态在动画尚未结束时变化，Runtime 必须从当前呈现状态重新求解后续运动，而不发生位置、数值或视觉上的不连续；对于运动型属性，应尽可能保持速度连续性。

换言之：

```text
Retarget
```

是正常状态更新。

```text
Cancel
```

才是异常/生命周期行为。

---

# 140. 关键不变量

## I1

```text
Current 是唯一真实视觉状态
```

## I2

```text
Target 可以任意时刻改变
```

## I3

```text
Retarget 本身不修改 Current
```

## I4

```text
一个最终属性只有一个 Channel
```

## I5

```text
Animation 不拥有业务逻辑
```

## I6

```text
Animation Completed 不是 callback
```

## I7

```text
Scope/Generation 决定结果是否仍有效
```

## I8

```text
Active Channel 才消耗每帧 CPU
```

---

# 141. 最终架构图

```text
                 Logical / Interaction State
                            │
                            ▼
                     Style Resolution
                            │
                            ▼
                     Target Property
                            │
                 ┌──────────┴──────────┐
                 │                     │
          Same Target             Target Changed
                 │                     │
                 ▼                     ▼
              Ignore              Retarget
                                       │
                                       ▼
                         ┌─────────────────────────┐
                         │   Animation Channel     │
                         │                         │
                         │ Current                 │
                         │ Velocity                │
                         │ Target                  │
                         │ Solver                  │
                         │ Continuity              │
                         └───────────┬─────────────┘
                                     │
                      ┌──────────────┼──────────────┐
                      ▼              ▼              ▼
                    Tween          Spring         Decay
                      │              │              │
                      └──────────────┼──────────────┘
                                     ▼
                              Current Visual
                                     │
                                     ▼
                           Transform / Render
```

---

# 142. 最终结论

`PCL.UI.Next.Animation` 应从第一版就建立为：

```text
Target-driven
+
Channel-based
+
Velocity-aware
+
Naturally Interruptible
+
Generation-safe
+
Scope-owned
+
Active-set ECS
```

其最重要的设计转换不是：

```text
“如何把旧动画取消得更漂亮”
```

而是：

```text
“不再把动画打断视为取消”
```

任何 Hover、Press、Scroll、Navigation、Gesture、Theme 或 Layout 变化，都只是：

```text
Target changed
```

Runtime 始终从：

```text
Current + Velocity
```

继续求解。

因此用户在任意时刻改变交互方向，视觉都应该保持连续。

---

## 附录 A：一句话原则

```text
State chooses Target;
Animation solves Current;
Retarget is normal;
Cancel is exceptional.
```

---

## 附录 B：实现优先级

```text
1. Float Channel
2. Tween Retarget
3. Spring + PreserveVelocity
4. Active Set
5. Transition Resolver
6. Transition Group
7. Direct/Gesture Solver
8. Decay Scroll
9. FLIP/Rebase
10. DevTools Motion Trace
```

完成以上十项后，`PCL.UI.Next` 已具备完整的“自然打断动画”基础。
