# PXML 1.0 语言、编译器与二进制打包规范

> **PXML = PCL eXtensible Markup Language**
>
> **规范状态：1.0 Frozen Candidate**
>
> PXML 是面向 `PCL.UI.Next` 的声明式、强类型、编译型 UI 描述语言。
>
> PXML **不是** Avalonia XAML 的方言，不是运行时对象序列化格式，也不是 `Control` 对象树构造语言。
>
> 核心原则：
>
> ```text
> PXML Source
>     ↓
> PXML Compiler
>     ↓
> Semantic IR
>     ↓
> UiBlueprint / Binding / Style / Resource Program
>     ↓
> Native Code 或 PXML Binary Package
>     ↓
> PCL.UI.Next Runtime
> ```
>
> 正式版本中：
>
> - 不允许运行时解析 XML；
> - 不允许 Reflection Binding；
> - 不允许根据标签动态实例化 C# 类型；
> - 不允许运行时解释任意 C#；
> - 不允许 PXML 编译器成为 `PCL-N` 主仓库的一部分。

---

# 1. 架构边界

PXML 被划分为三个完全不同的层次：

```text
Language
    ↓
Compiler Toolchain
    ↓
Runtime ABI
```

其中：

```text
Language
= PXML / PXSS / PXRES 语法和语义

Compiler Toolchain
= Parser / Semantic Analyzer / Optimizer / CodeGen / Packager / LSP

Runtime ABI
= UiBlueprint / Binding Program / Package Loader 所需稳定结构
```

`PCL.UI.Next` Runtime 不负责理解 PXML 源代码。

Runtime 只理解：

```text
UiBlueprint
BindingProgram
StructuralProgram
StyleProgram
ResourceId
MotionTokenId
UiCommandId
```

---

# 2. 编译器必须独立仓库

这是 PXML 1.0 的**强制架构约束**。

PXML 编译器不得作为：

```text
PCL-N/PCL.UI.Next/PxmlCompiler
```

之类的目录长期存在。

推荐独立仓库：

```text
PCL-N-Edition/PXML
```

或：

```text
PCL-N-Edition/PXML-Compiler
```

推荐最终名称：

```text
PCL-N-Edition/PXML
```

因为该仓库不仅包含 compiler，还负责语言规范、formatter、language server、packager 和工具链。

---

# 3. 仓库职责

## PXML 独立仓库负责

```text
Lexer
Parser
Syntax Tree
Semantic Model
Type System
Binding Compiler
Component Expander
PXSS Compiler
Optimizer
Blueprint Lowering
Native Code Generator
Binding VM Generator
PXB Serializer
PXPK Packager
Formatter
Diagnostics
Language Server
IDE Protocol
MSBuild Integration
CLI
Tests
Specification
```

## PCL-N 主仓库负责

```text
PCL.UI.Next Runtime

UiBlueprint Runtime Representation
Reactive ECS
Layout
Style Runtime
Input
Animation
Rendering
Virtualization
Native Host

Runtime ABI implementation
PXPK Runtime Loader
Binding VM Runtime（若需要）
```

---

# 4. 禁止编译器与 Runtime 循环依赖

禁止：

```text
PXML Compiler
    ↓
PCL-N executable implementation
    ↓
PXML Compiler
```

编译器只能依赖稳定 contract。

推荐边界：

```text
          PXML Repository
               │
               ▼
       PXML ABI Contracts
               │
        ┌──────┴──────┐
        ▼             ▼
 PXML Compiler    PCL.UI.Next
```

ABI Contract 可以通过独立 NuGet 包发布：

```text
PCL.Pxml.Abstractions
```

包含：

```text
UiPropertyId definitions
NodeKind definitions
Package ABI structures
Binding opcode definitions
Version structures
Primitive metadata
Diagnostic contracts
```

不得包含：

```text
ECS implementation
Avalonia
PCL.Application
Minecraft business logic
Desktop Host
```

---

# 5. 推荐 PXML 仓库结构

```text
PXML/
├ docs/
│ ├ specification/
│ │ ├ PXML-1.0.md
│ │ ├ PXSS-1.0.md
│ │ ├ PXB-1.0.md
│ │ ├ PXPK-1.0.md
│ │ └ Binding-VM-1.0.md
│ │
│ └ language-design/
│
├ src/
│ ├ PCL.Pxml.Abstractions/
│ ├ PCL.Pxml.Syntax/
│ ├ PCL.Pxml.Compiler/
│ ├ PCL.Pxml.Binding/
│ ├ PCL.Pxml.Styles/
│ ├ PCL.Pxml.CodeGen/
│ ├ PCL.Pxml.Packaging/
│ ├ PCL.Pxml.Cli/
│ ├ PCL.Pxml.MSBuild/
│ ├ PCL.Pxml.SourceGenerator/
│ ├ PCL.Pxml.LanguageServer/
│ └ PCL.Pxml.Formatter/
│
├ tests/
│ ├ PCL.Pxml.Syntax.Test/
│ ├ PCL.Pxml.Compiler.Test/
│ ├ PCL.Pxml.CodeGen.Test/
│ ├ PCL.Pxml.Packaging.Test/
│ └ PCL.Pxml.Conformance.Test/
│
├ benchmarks/
│
├ samples/
│
├ Directory.Build.props
├ Directory.Packages.props
└ PXML.slnx
```

---

# 6. PCL-N 如何使用编译器

`PCL-N` 不引用 compiler source project。

只能通过以下方式消费：

```text
NuGet Package
dotnet tool
MSBuild task package
Source Generator package
Compiler SDK package
```

推荐：

```xml
<ItemGroup>
    <PackageReference
        Include="PCL.Pxml.MSBuild"
        Version="1.x.x"
        PrivateAssets="all" />

    <PackageReference
        Include="PCL.Pxml.SourceGenerator"
        Version="1.x.x"
        PrivateAssets="all" />
</ItemGroup>
```

禁止：

```xml
<ProjectReference Include="../PXML/PCL.Pxml.Compiler.csproj" />
```

作为正式架构。

本地开发可以通过 NuGet local feed 或 repository checkout override 调试，但不能形成产品依赖。

---

# 7. 文件类型

PXML 1.0 定义：

```text
*.pxml      UI 页面、组件、模板源码
*.pxss      PXML Style Sheet
*.pxres     Resource Manifest
*.pxi       编译后的公开 Interface Metadata

*.pxb       Blueprint Binary
*.pxs       Style Binary
*.pxr       Resource Metadata Binary

*.pxpkg     PXML Package
*.pxmap     独立 Source Map
```

---

# 8. 推荐源码布局

```text
UI/
├ Pages/
│ ├ MainPage.pxml
│ ├ DownloadPage.pxml
│ └ SettingsPage.pxml
│
├ Components/
│ ├ Button.pxml
│ ├ Card.pxml
│ └ VersionCard.pxml
│
├ Styles/
│ ├ Base.pxss
│ ├ Controls.pxss
│ └ Themes/
│   ├ Light.pxss
│   └ Dark.pxss
│
├ Resources/
│ ├ Common.pxres
│ └ Images/
│
└ pxml.json
```

---

# 9. PXML 不是 XML Runtime

PXML 使用 XML-like lexical syntax：

```xml
<Page>
    <Column>
        <Text Text="Hello" />
    </Column>
</Page>
```

但不承诺完整 W3C XML compatibility。

禁止：

```text
DTD
DOCTYPE
XML Entity Expansion
External Entity
Executable CDATA
Processing Instruction 扩展
Runtime Namespace URL Resolution
XInclude
XPath
XSLT
```

这样可以避免：

```text
XXE
复杂 XML 兼容负担
运行时动态加载
不必要的 parser complexity
```

---

# 10. 文件头

推荐：

```xml
<?pxml version="1.0"?>

<Page
    xmlns="pcl://ui"
    xmlns:x="pcl://language">
</Page>
```

严格模式：

```xml
<?pxml
    version="1.0"
    strict="true"
?>
```

---

# 11. Namespace

内建 Namespace：

```xml
xmlns="pcl://ui"
xmlns:x="pcl://language"
xmlns:sys="pcl://system"
xmlns:motion="pcl://motion"
```

本地：

```xml
xmlns:local="./Components"
```

Package：

```xml
xmlns:controls="package://PCL.Controls"
```

禁止：

```xml
xmlns:test="https://example.com/ui"
```

通过互联网解析组件。

---

# 12. Element 分类

所有 Element 属于：

```text
Primitive
Component
Directive
Template
```

---

# 13. Primitive

Primitive 是能够直接 Lower 到 `UiBlueprint` 的框架原语。

PXML 1.0 Primitive：

```text
Node
Text
Image

Row
Column
Grid
Overlay
Absolute

Scroll
VirtualList

Content
NativeHost
```

Primitive 不代表 C# Control。

例如：

```xml
<Column
    Gap="12"
    Padding="16">

    <Text Text="Hello" />

</Column>
```

Lower 后：

```text
BlueprintNode #0
├ LayoutKind = Column
├ Gap = 12
└ Padding = 16

BlueprintNode #1
├ NodeKind = Text
└ TextContent = "Hello"
```

---

# 14. Component

Component 是**编译期宏组件**。

例如：

```xml
<Button
    Text="启动"
    Command="{cmd Launch}" />
```

Compiler 必须在 Lowering 前展开。

Runtime 中不存在：

```text
ButtonControl
ButtonWidget
Button Runtime Class
```

最终只存在 Primitive 对应 component set。

---

# 15. Component 定义

```xml
<Component
    xmlns="pcl://ui"
    xmlns:x="pcl://language"
    x:Name="Button">

    <x:Property
        Name="Text"
        Type="string"
        Required="true" />

    <x:Property
        Name="Command"
        Type="command?"
        Default="null" />

    <Node
        Class="Button"
        Behaviors="Hover Press Focus Click"
        Command="{component.Command}">

        <Text
            Class="Button.Content"
            Text="{component.Text}" />

    </Node>

</Component>
```

---

# 16. Component 展开

调用：

```xml
<Button
    Text="启动游戏"
    Command="{cmd LaunchGame}" />
```

经过：

```text
Resolve Component
↓
Validate Property
↓
Insert Slot
↓
Substitute Property
↓
Expand Nested Components
↓
Constant Fold
↓
Primitive IR
```

二进制中不保存 Component object graph。

---

# 17. Property 类型系统

PXML 1.0 基础类型：

```text
bool

i32
i64
u32
u64

f32
f64

string

color
length
size
point
rect
thickness
corner-radius
matrix

duration

resource
image
font

entity
command
motion

enum<T>

optional<T>
list<T>
```

语法糖：

```text
string?
```

等价：

```text
optional<string>
```

---

# 18. Boolean Literal

```xml
Visible="true"
Enabled="false"
```

---

# 19. Number Literal

```xml
Opacity="0.8"
Gap="12"
ZIndex="100"
```

---

# 20. Length

```xml
Width="120"
Width="50%"
Width="auto"
Width="1*"
Width="2*"
```

语义：

```text
120    Fixed(120)
50%    Percentage(0.5)
auto   Auto
1*     Star(1)
2*     Star(2)
```

不支持的 Layout Length 必须编译错误，不允许 silent fallback。

---

# 21. Thickness

```xml
Padding="12"
Padding="12,8"
Padding="12,8,12,8"
```

定义：

```text
1 value:
all

2 values:
horizontal, vertical

4 values:
left, top, right, bottom
```

---

# 22. Color

支持：

```xml
Color="#RRGGBB"
Color="#AARRGGBB"
```

例如：

```xml
Background="#FF2288"
Background="#8022AAFF"
```

---

# 23. Dynamic Expression

动态值统一：

```text
{ expression }
```

例如：

```xml
<Text
    Text="{bind Launcher.VersionName}" />
```

所有表达式必须可以在编译期确定静态结果类型。

---

# 24. Markup Expression 分类

PXML 1.0 定义：

```text
bind
cmd
event
res
loc
theme
motion
feature
const
template
x:ref
```

---

# 25. Binding

```xml
<Text
    Text="{bind User.Name}" />
```

Compiler 输出：

```text
TargetNode
TargetProperty
DependencySet
Typed Binding Program
```

禁止 runtime reflection：

```text
"User.Profile.Name"
↓
PropertyInfo
↓
reflection
```

---

# 26. Binding Path

支持：

```text
{bind User}
{bind User.Name}
{bind Download.Progress}
{bind User.Profile.Avatar}
```

静态索引：

```text
{bind Items[0]}
```

动态集合应通过：

```text
VirtualList
For
typed selector
```

处理。

---

# 27. Binding Expression

支持：

```text
+
-
*
/
%

==
!=

<
<=
>
>=

&&
||

!

??
?:
```

例如：

```xml
<Text
    Text="{bind Download.Progress >= 1 ? '完成' : '下载中'}" />
```

---

# 28. Binding 必须纯函数化

禁止：

```text
new
await
lock
throw
file I/O
network I/O
reflection
Process
service call
arbitrary method invocation
```

Binding expression 只能：

```text
Read State
Read Local
Read Item
Compute Pure Value
Return
```

---

# 29. Pure Function

内建：

```text
format
clamp
min
max
round
floor
ceil

upper
lower
trim

is-null
not-null

rgb
rgba
```

扩展函数必须显式注册。

例如：

```csharp
[PxmlFunction("format-size")]
public static string FormatSize(long value);
```

Compiler 在 AOT path 中生成静态直接调用。

Dynamic Package path 只能调用 whitelist function ID。

---

# 30. Command

业务动作必须通过 `UiCommand`。

```xml
<Button
    Command="{cmd LaunchGame}" />
```

参数：

```xml
<Button
    Command="{cmd LaunchGame}"
    CommandParameter="{bind SelectedInstance.Id}" />
```

编译：

```text
UiCommandId
TypedArgumentProgram
```

禁止：

```xml
OnClick="LaunchGame()"
```

或：

```xml
Click="LauncherService.Start"
```

---

# 31. UI Event

底层 UI Behavior 可以：

```xml
<Node
    OnPointerDown="{event BeginDrag}"
    OnPointerUp="{event FinishDrag}" />
```

`event` 只能进入 UI 行为层。

业务动作使用：

```text
cmd
```

---

# 32. Structural If

```xml
<x:If Condition="{bind Account.IsLoggedIn}">

    <Text Text="已登录" />

    <x:Else>
        <Button
            Text="登录"
            Command="{cmd Login}" />
    </x:Else>

</x:If>
```

编译为：

```text
StructuralProgram
DependencySet
Branch Blueprint
```

---

# 33. Switch

```xml
<x:Switch Value="{bind Download.State}">

    <x:Case Value="Idle">
        <Text Text="等待" />
    </x:Case>

    <x:Case Value="Downloading">
        <ProgressBar
            Value="{bind Download.Progress}" />
    </x:Case>

    <x:Default>
        <Text Text="未知状态" />
    </x:Default>

</x:Switch>
```

---

# 34. For

小规模非虚拟列表：

```xml
<x:For
    Each="{bind Versions}"
    As="version"
    Key="{version.Id}">

    <VersionCard Version="{version}" />

</x:For>
```

动态集合默认必须有稳定 `Key`。

---

# 35. VirtualList

大型集合必须优先使用：

```xml
<VirtualList
    Items="{bind Mods}"
    Key="{item.Id}"
    EstimatedItemHeight="48">

    <Template As="item">
        <ModItem Mod="{item}" />
    </Template>

</VirtualList>
```

编译：

```text
VirtualizationTemplate
KeySelector
ItemBindingProgram
EstimatedMetrics
```

生成代码必须把集合适配为 Runtime 的强类型虚拟数据源，而不是在运行时反射模型：

```csharp
sealed class ModsVirtualSource : IUiVirtualItemSource
{
    public int Count { get; }
    public ulong Version { get; }
    public long GetKey(int index);
    public void BindItem(int index, PresentationStore presentation);
    public bool TryGetIndex(long key, out int index);
}
```

Lowering 结果分成两部分：

```text
VirtualList host Blueprint
├ ScrollViewport
├ ScrollState
└ Virtualization(EstimatedItemExtent, OverscanBefore, OverscanAfter)

Item Blueprint
└ Template 中的静态节点、BindingProgram 与结构指令
```

挂载时由生成代码执行一次注册：

```csharp
UiVirtualListRegistration registration = runtime.Virtualization.Register(
    virtualListEntity,
    generatedSource,
    itemBlueprint);
```

注册对象属于页面 Scope 生命周期，卸载页面时必须 Dispose。编译器不得为每个逻辑项预创建 Entity；Runtime 只为可见区和 overscan 创建 slot，并在滚动后重绑定 `PresentationStore`。

`Key` 在同一集合版本内必须唯一且稳定。变长项的实测 extent 按 Key 保存；集合插入、删除或重排后，Runtime 通过 Key 恢复测量结果和当前可见锚点。`Version` 只有在集合拓扑或 Key/绑定数据发生可观察变化时递增；生成的集合变更适配器随后调用 `runtime.Virtualization.Invalidate(host)` 请求一次响应式规划帧，禁止为了轮询 Version 常驻 60 FPS。

`EstimatedItemHeight` Lower 为逻辑主轴的 `EstimatedItemExtent`。横向 VirtualList 使用同一字段表示估算宽度。offset 到 index、index 到 offset 的实现契约均为 `O(log N)`；100,000 个逻辑项不得导致 `O(N)` 的逐帧扫描或实体实例化。

---

# 36. Template

```xml
<x:Template
    Name="VersionTemplate"
    Type="VersionModel"
    As="version">

    <Text Text="{version.Name}" />

</x:Template>
```

调用：

```xml
<Content
    Template="{template VersionTemplate}"
    Value="{bind SelectedVersion}" />
```

---

# 37. Slot

```xml
<Component x:Name="Card">

    <x:Slot Name="Content" />

    <Node Class="Card">
        <x:Content Slot="Content" />
    </Node>

</Component>
```

使用：

```xml
<Card>
    <Text Text="内容" />
</Card>
```

---

# 38. Multiple Slots

```xml
<Card>

    <x:Into Slot="Header">
        <Text Text="标题" />
    </x:Into>

    <x:Into Slot="Content">
        <Text Text="正文" />
    </x:Into>

</Card>
```

---

# 39. Style Class

```xml
<Node Class="Card Elevated" />
```

Compiler 转换为：

```text
StyleClassId[]
```

Release 默认不需要保留 class name。

---

# 40. PXSS

PXSS 是 PXML 的静态样式语言。

```css
Button {
    background: $Surface.Button;
    corner-radius: 8;
}

Button:hover {
    background: $Surface.ButtonHover;
}

Button:pressed {
    transform.scale: 0.97;
}

Button:disabled {
    opacity: 0.45;
}

.Primary {
    background: $Accent.Primary;
}
```

---

# 41. PXSS Selector

PXML 1.0 支持：

```text
Type

.Class

:hover
:pressed
:focused
:disabled
:selected

Type.Class

Parent > Child
Ancestor Child
```

1.0 不支持：

```text
:nth-child
:nth-of-type
复杂 attribute selector
regex selector
:has()
```

---

# 42. Theme Token

```css
Button {
    background: $Control.Background;
    color: $Control.Foreground;
}
```

Compiler 输出：

```text
ThemeTokenId
```

而不是运行时字符串。

---

# 43. Inline Property

允许：

```xml
<Node
    Background="{theme Surface.Layer1}"
    CornerRadius="8" />
```

但复杂视觉规则建议放到 PXSS。

---

# 44. Resource

```xml
<Image
    Source="{res Images.Logo}" />
```

`.pxres`：

```json
{
  "Images.Logo": "Assets/logo.webp",
  "Images.DefaultAvatar": "Assets/avatar.webp"
}
```

Compiler：

```text
canonical resource name
↓
ResourceId
```

---

# 45. Localization

```xml
<Text
    Text="{loc Home.Title}" />
```

Compiler 输出：

```text
LocalizationKeyId
```

---

# 46. Motion

```xml
<Node
    Opacity="{bind Visible ? 1 : 0}"
    Transition.Opacity="{motion Standard}" />
```

Compiler 必须根据 P5 Runtime ABI 校验：

```text
Property
Solver
Continuity
MotionToken
```

兼容性。

---

# 47. Layout Animation

```xml
<Card
    x:AnimateLayout="true"
    x:LayoutMotion="{motion Standard}" />
```

Lower：

```text
AnimateLayoutComponent
LayoutMotionToken
```

由 Runtime FLIP 处理。

---

# 48. Behavior

```xml
<Node
    Behaviors="Hover Press Focus Click" />
```

Lower 成 Component Set：

```text
Hoverable
Pressable
Focusable
Clickable
```

不是 runtime `Behavior` class collection。

---

# 49. x:Name

```xml
<Text x:Name="Title" />
```

仅在需要引用时生成 symbol。

引用：

```text
{x:ref Title}
```

普通无名 node 不生成 runtime name。

---

# 50. x:Key

```xml
<x:Template
    x:Key="VersionTemplate">
```

`x:Key` 也是 Hot Reload / Structural Reconcile 的稳定 identity 输入之一。

---

# 51. Scope

```xml
<Overlay x:Scope="Modal">
```

Compiler 必须阻止页面创建非法：

```text
ApplicationScope
WindowScope
```

等由 Host 管理的 scope。

---

# 52. Focus Scope

```xml
<Column
    Focus.Scope="true"
    Focus.Trap="true"
    Focus.Restore="true">
```

Lower：

```text
FocusScopeComponent
FocusTrapComponent
FocusRestorePolicy
```

---

# 53. NativeHost

例如编辑文本：

```xml
<NativeHost
    Kind="TextBox"
    Value="{bind Search.Text}"
    Placeholder="{loc Search.Placeholder}" />
```

PXML 只描述 NativeHost contract。

具体 Avalonia Control 由 backend 创建。

`Value` 是 Runtime target binding。平台侧输入不得直接修改 Presentation Store；它必须先
形成 generation-safe NativeHost event，再经显式 Command/StatePatch 写回：

```text
Native TextChanged / SelectionChanged / Submitted
↓
NativeHost event journal
↓
validate Entity + Scope generation
↓
Command / StatePatch
↓
next binding evaluation
```

---

## 53.1 Accessibility 语义

PXML 元素可声明独立于视觉树的语义：

```xml
<Button
    AccessibleRole="Button"
    AccessibleName="{loc Download.Install}"
    AccessibleDescription="{loc Download.InstallDescription}"
    AccessibleActions="Invoke Focus"
    Command="{cmd Install}" />
```

编译器必须将其展开为：

```text
SemanticRole
AccessibleName
AccessibleDescription
AccessibleValue
AccessibleState
AccessibleAction
```

禁止将 RenderNode hierarchy 当作 Semantic Tree。`AccessibleName`/`Value` 的 binding
必须进入静态 dependency index；PasswordBox 的 Value 不得进入 semantic output。

---

## 53.2 Tooltip / Popup / Modal

Overlay primitive 描述的是 Runtime overlay contract，不是 Avalonia Popup/Window：

```xml
<Button Text="Help">
    <Button.Tooltip Delay="500ms" Placement="Pointer">
        <Text Text="{loc Help.Description}" />
    </Button.Tooltip>
</Button>

<Popup
    Anchor="{ref MoreButton}"
    Placement="Auto"
    DismissOnOutsidePointer="true"
    DismissOnEscape="true">
    <Menu Items="{bind Page.Actions}" />
</Popup>

<Modal DismissOnEscape="true">
    <DialogContent />
</Modal>
```

Compiler/IR 必须显式编码 placement、dismiss policy、focus policy 与 child Scope ownership。
Tooltip 默认 input pass-through；Modal 默认生成 dim/input barrier 与 trapping FocusScope。
页面不得通过设置主树 `IsHitTestVisible=false` 模拟 Modal。

---

## 53.3 NavigationHost / Page

```xml
<NavigationHost Current="{bind Shell.Route}">
    <Page Key="Home" Cache="Pinned">
        <HomePage />
    </Page>
    <Page Key="Download" Cache="Lru">
        <DownloadPage />
    </Page>
</NavigationHost>
```

`Key` 必须在同一 NavigationHost 内编译期唯一。`Cache` 只允许：

```text
None
KeepPresentationState
KeepEntities
Lru
Pinned
```

生成物必须是静态 `UiPageDefinition + UiBlueprint` 表，不得生成页面对象或 completion
callback。Navigation request、state change 与 completion 通过 generation-safe journal 表达；
旧 generation 的 transition completion 不得提交页面状态。

---

# 54. Import 与 Namespace

允许：

```xml
<x:Import Source="./Templates.pxml" />
```

但组件组织优先：

```xml
xmlns:shared="./Shared"
```

使用：

```xml
<shared:Toolbar />
```

Import 必须在 compile time resolution。

---

# 55. Compile-Time Constant

```xml
<x:Const
    Name="SidebarWidth"
    Type="f32"
    Value="256" />
```

使用：

```xml
Width="{const SidebarWidth}"
```

Release 必须 constant fold。

---

# 56. Build Condition

```xml
<x:IfBuild Condition="WINDOWS">
    <WindowsOnlyView />
</x:IfBuild>
```

标准 symbol：

```text
DEBUG
RELEASE

WINDOWS
LINUX
MACOS

COMMUNITY
ULTIMATE
TEAMS
DEVELOPER
```

未选分支不进入 Blueprint。

---

# 57. Runtime Feature

```xml
<x:If Condition="{feature PluginMarket}">
```

区别：

```text
IfBuild
= compile time

feature / bind If
= runtime structural binding
```

---

# 58. 注释

```xml
<!-- comment -->
```

Release 必须丢弃。

---

# 59. 基础 EBNF

```text
document
    = prolog? element EOF ;

prolog
    = "<?pxml" prolog_attribute* "?>" ;

element
    = "<" qualified_name attribute* "/>"
    | "<" qualified_name attribute* ">"
        content*
      "</" qualified_name ">" ;

content
    = element
    | text
    | comment ;

attribute
    = qualified_name "=" string_literal ;

qualified_name
    = identifier
    | identifier ":" identifier ;

identifier
    = letter
      (letter | digit | "_" | "-" | ".")* ;
```

Markup expression 在 attribute string 中由独立 lexer 解析。

---

# 60. Expression Grammar

```text
expression
    = conditional ;

conditional
    = null_coalesce
      ("?" expression ":" expression)? ;

null_coalesce
    = logical_or
      ("??" logical_or)* ;

logical_or
    = logical_and
      ("||" logical_and)* ;

logical_and
    = equality
      ("&&" equality)* ;

equality
    = relational
      (("==" | "!=") relational)* ;

relational
    = additive
      (("<" | "<=" | ">" | ">=") additive)* ;

additive
    = multiplicative
      (("+" | "-") multiplicative)* ;

multiplicative
    = unary
      (("*" | "/" | "%") unary)* ;

unary
    = ("!" | "-" | "+") unary
    | primary ;

primary
    = literal
    | path
    | function_call
    | "(" expression ")" ;
```

---

# 61. Compiler Pipeline

独立 PXML Compiler 必须遵循：

```text
Source Discovery
      ↓
Lexing
      ↓
Parsing
      ↓
Syntax Tree
      ↓
Namespace Resolution
      ↓
Symbol Resolution
      ↓
Component Interface Resolution
      ↓
Type Checking
      ↓
Component Expansion
      ↓
Binding Analysis
      ↓
Dependency Extraction
      ↓
Structural Analysis
      ↓
PXSS Compilation
      ↓
Semantic IR
      ↓
Optimization
      ↓
Blueprint Lowering
      ↓
Native CodeGen / VM CodeGen
      ↓
Binary Serialization
      ↓
Packaging
```

---

# 62. Compiler Frontend

推荐：

```text
PCL.Pxml.Syntax
```

负责：

```text
SourceText
Lexer
Token
Parser
SyntaxNode
SyntaxTrivia
SourceSpan
Incremental Syntax Tree
```

Compiler Core 不应直接依赖 IDE。

IDE/LSP 复用 Syntax/Semantic API。

---

# 63. Syntax Tree

必须保留：

```text
FileId
StartOffset
Length
Line
Column
Trivia
```

以支持：

```text
Diagnostics
Formatting
Navigation
Refactoring
Source Map
Hot Reload
```

---

# 64. Semantic Model

Semantic 阶段必须产生：

```text
ResolvedElementSymbol
ResolvedPropertySymbol
ResolvedType
ResolvedBinding
ResolvedCommand
ResolvedResource
ResolvedStyle
ResolvedTemplate
DependencySet
```

禁止在 Backend Lowering 阶段才发现基础类型错误。

---

# 65. Semantic IR

Syntax Tree 不直接序列化。

必须经过平台无关 IR：

```text
PxmlIR
```

示例：

```text
IrNode
{
    NodeKind
    ParentIndex

    StaticProperties
    Bindings
    StyleClasses
    Behaviors

    StructuralProgram?
    SourceLocation
}
```

---

# 66. Binding Compile

源码：

```xml
<Text
    Text="{bind User.Name}" />
```

Semantic：

```text
TargetType      = string
Dependency      = UserSlice
ReadPath        = User.Name
```

Lower：

```text
BindingProgram
{
    TargetNode = 17
    TargetProperty = TextContent

    Dependencies = [UserSliceId]

    Program = ...
}
```

---

# 67. Core UI 编译路径

PCL 主程序自身 UI 推荐：

```text
*.pxml
↓
PXML Compiler
↓
Semantic IR
↓
Generated C#
↓
C# Compiler
↓
Native AOT
```

核心页面默认不通过 Binding VM。

目标：

```text
zero reflection
zero source parser runtime
zero general binding interpreter
```

---

# 68. Generated Binding

概念生成：

```csharp
private static void Binding_17(
    ref UiGeneratedBindingContext context)
{
    context.SetString(
        NodeIds.Title,
        context.State.User.Name);
}
```

Runtime 不需要知道源表达式字符串。

---

# 69. Dynamic Package 编译路径

插件/外部 package：

```text
PXML
↓
Compiler
↓
Semantic IR
↓
Typed Binding VM
↓
PXB/PXPK
↓
Runtime Loader
```

VM 必须：

```text
strongly typed
bounded
sandboxable
no reflection
no arbitrary native call
```

---

# 70. Binding VM

建议 1.0 opcode 类别：

```text
LOAD

LOAD_SLICE
LOAD_LOCAL
LOAD_ITEM
LOAD_FIELD

CONST

PUSH_I32
PUSH_I64
PUSH_F32
PUSH_F64
PUSH_BOOL
PUSH_STRING

ARITHMETIC

ADD
SUB
MUL
DIV
MOD
NEGATE

COMPARE

EQ
NE
LT
LE
GT
GE

LOGIC

NOT
AND
OR
COALESCE

FLOW

JUMP
JUMP_IF_FALSE

FUNCTION

CALL_PURE_FUNC

OUTPUT

SET_PROPERTY
SET_TEXT
EMIT_COMMAND_ARGUMENT
```

---

# 71. VM 校验

加载 `.pxpkg` 时必须验证：

```text
Opcode validity
Instruction boundary
Stack depth
Type safety
Function whitelist
Property compatibility
Jump range
Program size
Dependency declarations
```

加载失败时整个 program 不得执行。

---

# 72. `.pxi`

组件 public interface 输出：

```text
Component Name
Property Signatures
Slot Signatures
Template Signatures
Required Features
ABI Version
Interface Hash
```

例如：

```text
Button

Text:string required
Command:command?
IsDefault:bool

Slot Content?
```

---

# 73. Incremental Compilation

Cache key 至少包括：

```text
SourceHash
ImportedInterfaceHash
ResourceInterfaceHash
StyleInterfaceHash
CompilerVersion
LanguageVersion
RuntimeAbiVersion
BuildSymbols
```

---

# 74. Interface-Based Dependency

如果：

```text
Button.pxml
```

内部实现变化，但：

```text
Button.pxi
```

Interface Hash 不变，则引用 Button 的其他页面无需重新进行完整 semantic compile。

---

# 75. Blueprint Binary `.pxb`

Header：

```text
Offset  Size
0x00    4     Magic = "PXB1"
0x04    2     Major
0x06    2     Minor
0x08    4     Flags
0x0C    8     ContentHashLow
0x14    8     ContentHashHigh
0x1C    4     SectionCount
0x20    4     HeaderSize
```

所有 integer 规定：

```text
Little Endian
```

---

# 76. PXB Section Directory

```text
struct PxbSection
{
    u32 Type;
    u32 Flags;

    u64 Offset;
    u64 Size;

    u32 Alignment;
    u32 Reserved;
}
```

Section 必须允许 Runtime 跳过未知 optional section。

---

# 77. PXB Sections

1.0：

```text
STRS    String Table
SYMB    Symbol Table
NODE    Blueprint Node Table
PROP    Static Properties
BIND    Binding Programs
DEPS    Dependency Index
STRU    Structural Programs
TMPL    Template Programs
STYL    Style References
RESR    Resource References
META    Metadata
SMAP    Source Map
```

---

# 78. Node Table

概念结构：

```text
PackedNode
{
    ParentIndex
    FirstChild
    ChildCount

    NodeKind

    PropertyOffset
    PropertyCount

    BindingOffset
    BindingCount

    StyleOffset
    StyleCount

    Flags
}
```

具体 binary width 由 ABI specification 固定。

不得依赖 C# struct 的默认 layout 直接 `MemoryMarshal` 序列化。

---

# 79. String Table

使用：

```text
offset table
+
UTF-8 blob
```

所有字符串全局 deduplicate。

Release 可以移除仅调试需要的 symbol string。

---

# 80. Stable ID

定义不同 ID 域：

```text
UiPropertyId
NodeKindId
StyleClassId
ThemeTokenId
ResourceId
LocalizationKeyId
UiCommandId
PureFunctionId
MotionTokenId
```

不同 ID domain 不得混用。

---

# 81. Symbol ID

推荐：

```text
XXH3_64(
    package-identity
    + "\0"
    + canonical-symbol-name
)
```

Compiler 必须执行 collision detection。

任何当前 compilation graph 内 collision：

```text
hard error
```

---

# 82. Content Hash

Package 内容完整性推荐：

```text
BLAKE3-256
```

Asset lookup 可以使用：

```text
XXH3-64
```

但不能用快速 hash 代替完整性校验。

---

# 83. `.pxpkg`

Header 概念：

```text
PxmlPackageHeader
{
    Magic
    FormatVersion

    Flags

    PackageId
    BuildId

    EntryCount
    TocOffset

    ManifestOffset

    UncompressedSize
    CompressedSize

    ContentHash
}
```

---

# 84. PXPK Entry

```text
PackageEntry
{
    AssetId

    EntryType
    Compression

    Offset
    CompressedSize
    OriginalSize

    ContentHash
}
```

---

# 85. Entry Type

PXML 1.0：

```text
Blueprint
Style
ResourceManifest
Image
Font
Localization
Metadata
SourceMap
BindingProgram
```

---

# 86. Package Layout

```text
PXPK Header
│
├ Table of Contents
├ Manifest
│
├ Blueprint Entries
├ Style Entries
├ Binding Entries
├ Resource Metadata
├ Images
├ Fonts
├ Localization
│
└ Optional Source Maps
```

---

# 87. Compression

PXML 1.0 Release 默认：

```text
Zstandard
```

推荐：

```text
Level 8
```

Debug：

```text
None
```

或：

```text
LZ4
```

---

# 88. Compression 边界

不逐 Node 压缩。

采用：

```text
Semantic structure
↓
Packed section binary
↓
Entry compression
↓
Package
```

小 entry 可以：

```text
Compression=None
```

避免压缩开销。

---

# 89. Zstd Dictionary

允许使用标准 PXML Dictionary：

```text
PXML-DICT-1
```

Package Header 必须记录：

```text
DictionaryId
```

Runtime 不存在对应 dictionary 时必须拒绝该 entry，而不能用错误字典解压。

---

# 90. Resource Packaging

支持：

```text
Embedded
External
```

Embedded：

```text
PXPkg
├ Blueprint
├ Style
└ Small Assets
```

External：

```text
Main.pxpkg
Assets.pak
```

适合大图片、视频和可更新资源。

---

# 91. Manifest

推荐：

```json
{
  "name": "PCL.MainUI",
  "version": "1.0.0",

  "languageVersion": "1.0",
  "runtimeAbi": 1,

  "entry": "Pages/MainPage.pxml",

  "styles": [
    "Styles/Base.pxss",
    "Styles/Controls.pxss"
  ],

  "resources": [
    "Resources/Common.pxres"
  ],

  "release": {
    "bindingBackend": "native",
    "compression": "zstd",
    "compressionLevel": 8,
    "stripSymbols": true,
    "sourceMap": "external"
  }
}
```

---

# 92. CLI

正式 CLI：

```text
pxml
```

主命令：

```text
pxml check
pxml build
pxml pack
pxml inspect
pxml dump
pxml format
pxml lsp
```

可保留：

```text
pxmlc
```

作为 compiler executable alias。

---

# 93. Check

```powershell
pxml check UI/
```

只执行：

```text
Parse
Semantic Analysis
Type Check
Binding Check
Resource Check
Style Check
```

不生成最终 package。

---

# 94. Build

```powershell
pxml build UI/ `
    --configuration Release `
    --target native
```

生成：

```text
obj/PXML/
```

---

# 95. Pack

```powershell
pxml pack obj/PXML/ `
    -o PCL.MainUI.pxpkg
```

---

# 96. Inspect

```powershell
pxml inspect PCL.MainUI.pxpkg
```

输出：

```text
ABI
Sections
Entries
Compression
Hashes
Symbols
Dependencies
Blueprint statistics
```

---

# 97. Dump

```powershell
pxml dump MainPage.pxb
```

输出 human-readable IR。

这必须成为 compiler debugging 和 ABI compatibility 的标准工具。

---

# 98. Formatter

```powershell
pxml format UI/
```

Formatter 是语言规范的一部分。

统一风格：

```xml
<Button
    Text="启动"
    Command="{cmd Launch}" />
```

---

# 99. Language Server

PXML 独立仓库必须提供：

```text
PCL.Pxml.LanguageServer
```

支持：

```text
Diagnostics
Completion
Hover
Go To Definition
Find References
Rename
Document Symbol
Semantic Tokens
Formatting
Code Action
Component Property Completion
Binding Type Completion
Resource Completion
Command Completion
```

---

# 100. IDE 集成原则

IDE 不应自己实现第二套 PXML parser/compiler。

正确方式：

```text
PCL Developer Studio
VS Code extension
Rider integration
Other IDE
       │
       ▼
PXML Language Server / Compiler API
```

即：

> **所有 PXML 解析、语义检查、Binding 分析、编译诊断都由独立 PXML 工具链提供。**

IDE 只消费统一 compiler service。

这防止：

```text
CLI parser 一套
IDE parser 一套
Runtime parser 又一套
```

产生语义漂移。

---

# 101. MSBuild 集成

PCL-N：

```xml
<ItemGroup>
    <Pxml Include="UI/**/*.pxml" />
    <Pxss Include="UI/**/*.pxss" />
    <PxResource Include="UI/**/*.pxres" />
</ItemGroup>
```

由：

```text
PCL.Pxml.MSBuild
```

注入 Target。

---

# 102. Build Pipeline

```text
ResolvePxmlCompiler
↓
PxmlCheck
↓
PxmlGenerate
↓
Compile Generated C#
↓
Package Resources
↓
dotnet publish
↓
Native AOT
```

---

# 103. Source Generator

`PCL.Pxml.SourceGenerator` 只负责 Roslyn integration。

真正的语言逻辑必须调用：

```text
PCL.Pxml.Compiler
```

共享 Compiler Core。

禁止 Source Generator 自己实现一套 parser/semantic analyzer。

---

# 104. Compiler SDK

独立仓库应发布：

```text
PCL.Pxml.Compiler
```

供：

```text
MSBuild
LSP
CLI
Source Generator
PCL Developer Studio
Tests
```

共同使用。

形成：

```text
              Compiler Core
             /      |       \
            /       |        \
          CLI     MSBuild     LSP
                    |
              SourceGenerator
```

---

# 105. Release Optimization

Release 必须支持：

```text
Component Expansion

Constant Folding
Dead Branch Elimination
Dead Template Elimination
Unused Resource Elimination

Binding Constant Folding
Dependency Deduplication

String Deduplication
Resource Deduplication

Symbol Stripping

Node Packing
Property Packing

Style Rule Optimization
```

---

# 106. Static Node Elimination

如果中间 node：

```text
无布局意义
无视觉意义
无输入行为
无语义
无 scope
无 binding
无 structural identity
```

Compiler 可以消除。

必须保证行为等价。

---

# 107. Build Branch Elimination

```xml
<x:IfBuild Condition="WINDOWS">
```

非 Windows build 中对应 branch 不得出现在：

```text
IR
Blueprint
PXB
PXPK
```

最终结果中。

---

# 108. Debug Source Map

Source Map 至少可以：

```text
BlueprintNode
→ PXML File
→ SourceSpan
```

以及：

```text
BindingProgram
→ PXML Expression
```

DevTools 可以显示：

```text
Entity
→ Blueprint Node
→ DownloadPage.pxml:44:9
```

---

# 109. Debug 包

Debug 默认：

```text
Symbols           Full
SourceMap         Embedded
Compression       None/LZ4
Validation        Full
HotReloadMetadata Enabled
```

---

# 110. Release 包

Release 默认：

```text
ComponentExpansion Yes
BindingBackend     Native for Core

StripSource        Yes
StripComments      Yes
StripSymbols       Yes

SourceMap          External

StringDedup        Yes
ResourceDedup      Yes

Compression        Zstd
CompressionLevel   8

Integrity          BLAKE3-256
Alignment          16
```

---

# 111. Hot Reload

开发模式：

```text
PXML Changed
↓
Incremental Compile
↓
Blueprint Generation + 1
↓
Structural Reconcile
↓
Preserve Stable Key Entities
```

Identity 输入：

```text
x:Key
Structural Key
Template Key
Source Stable Node Identity
```

---

# 112. Hot Reload 不属于 Release ABI

Release package 不要求携带：

```text
source paths
local names
full syntax metadata
hot reload map
```

除非 Debug/Developer build 显式开启。

---

# 113. Diagnostics

格式：

```text
PXML2204 error:
DownloadPage.pxml:42:18

Property 'Text' expects 'string',
but expression produces 'VersionInfo'.

    Text="{bind SelectedVersion}"
          ^^^^^^^^^^^^^^^^^^^^^^
```

---

# 114. Diagnostic Category

```text
PXML1xxx  Syntax

PXML2xxx  Type System

PXML3xxx  Binding

PXML4xxx  Component / Template

PXML5xxx  PXSS / Style

PXML6xxx  Resource

PXML7xxx  Packaging

PXML8xxx  ABI / Compatibility

PXML9xxx  Security / Sandbox
```

---

# 115. Warning Policy

Compiler 必须支持：

```text
--warn-as-error
--nowarn
--warn
```

以及项目级：

```json
{
  "warningsAsErrors": [
    "PXML31*"
  ]
}
```

---

# 116. Security Model

动态 `.pxpkg` 不允许：

```text
Arbitrary C#
Reflection
Assembly.Load
Native DLL
P/Invoke
File IO
Network IO
Process.Start
Environment mutation
Dynamic code generation
Unbounded recursion
Unsafe pointer
```

---

# 117. Dynamic Package Capability

外部 package 只能：

```text
Declare UI
Read exported state
Emit exported command
Use permitted resource
Call whitelisted pure function
Use supported UI primitives
```

---

# 118. Permission Manifest

例如：

```json
{
  "permissions": [
    "ui.state:plugin.status",
    "ui.command:plugin.open-settings"
  ]
}
```

Compiler 可以进行静态验证。

Runtime Loader 必须再次验证。

---

# 119. ABI Version

必须独立记录：

```text
PxmlLanguageVersion
PxmlPackageVersion
UiRuntimeAbiVersion
PropertyTableVersion
NodeKindTableVersion
BindingVmVersion
StyleVmVersion
```

不得只使用一个版本号涵盖所有协议。

---

# 120. Compatibility

Package 声明：

```text
MinimumRuntimeAbi
MaximumTestedRuntimeAbi
RequiredFeatures
```

Runtime 必须：

```text
verify
↓
load
```

而不是“尽量尝试”。

---

# 121. ABI Breaking Change

以下变化必须提高相应 Major：

```text
Opcode semantic change
Binary field semantic change
PropertyId incompatible reuse
NodeKind incompatible reuse
Mandatory section incompatible change
Package validation semantic break
```

---

# 122. Stable ID 规则

已经分配并发布的 core：

```text
UiPropertyId
NodeKindId
BindingOpcode
```

禁止重新复用为不同语义。

删除后 ID 永久 reserved。

---

# 123. Compiler Version 与 Language Version 分离

例如：

```text
PXML language 1.0

Compiler 1.7.3
```

完全合法。

Compiler 更新优化器不需要改变 language version。

---

# 124. Deterministic Build

相同：

```text
Input
Compiler Version
Build Configuration
ABI
Build Symbols
```

必须产生字节级一致输出。

禁止将：

```text
wall clock
absolute local path
random GUID
machine name
```

写进 Release package。

---

# 125. Reproducible Build ID

BuildId 推荐：

```text
BLAKE3(
    sorted source hashes
    + compiler version
    + ABI versions
    + build options
)
```

而不是随机 GUID。

---

# 126. Core UI 与 Dynamic UI

PXML 统一语言，但存在两个部署 profile。

## Core Profile

```text
PXML
↓
Native Binding CodeGen
↓
PCL Desktop
↓
Native AOT
```

## Package Profile

```text
PXML
↓
Typed Binding VM
↓
PXPK
↓
Sandboxed Loader
```

语法一致。

允许能力由 profile 限制。

---

# 127. Profile Diagnostics

如果 Package Profile 使用只允许 Core Profile 的能力：

```text
PXML compile error
```

不能生成后运行时失败。

---

# 128. PXML 不允许演化为 XAML 2.0

PXML 1.x 明确不引入：

```text
Control class hierarchy
DependencyProperty
MarkupExtension class system
Runtime object factory
Reflection Binding
Arbitrary code-behind
Runtime DataTemplate class
Dynamic TypeConverter
Runtime XML resource dictionary
```

---

# 129. Runtime 绝不包含完整 Compiler

`PCL.UI.Next` 正式 Runtime 不引用：

```text
PCL.Pxml.Syntax
PCL.Pxml.Compiler
Roslyn
Language Server
Formatter
```

Core Release 最终只需要：

```text
Generated Blueprint
Runtime ABI
Optional PXPK Loader
Optional Binding VM
```

---

# 130. 编译器发布策略

独立 PXML Repository 发布：

```text
PCL.Pxml.Abstractions
PCL.Pxml.Compiler
PCL.Pxml.MSBuild
PCL.Pxml.SourceGenerator
PCL.Pxml.LanguageServer
```

以及：

```text
pxml
```

dotnet tool / standalone executable。

---

# 131. CI Compatibility Matrix

PXML Repo CI 必须测试：

```text
Compiler HEAD
×
Latest Runtime ABI

Compiler Release
×
Minimum Supported Runtime ABI

PXPK Golden Files
×
Runtime Loader

Native Generated Code
×
Native AOT
```

---

# 132. Golden Test

必须维护：

```text
tests/Golden/
```

内容：

```text
source.pxml
expected.ir
expected.pxb
expected.diagnostics
```

用于防止无意改变：

```text
parser semantics
binding lowering
binary ABI
diagnostic behavior
```

---

# 133. Fuzz

Compiler 独立仓库应对：

```text
Lexer
Parser
PXB Reader
PXPK Reader
Binding VM Validator
```

做 fuzz。

特别是外部 Plugin Package loader 属于不可信输入边界。

---

# 134. IDE Single Source of Truth

PCL Developer Studio 后续即使内置 PXML 编辑能力，也必须复用：

```text
PCL.Pxml.Compiler SDK
```

或：

```text
PCL.Pxml.LanguageServer
```

不能复制语言实现。

因此所谓“IDE 内置 PXML 编译/检查”指的是：

> IDE 产品体验内置，但语言实现仍来自独立 PXML Compiler Repository。

---

# 135. 完整示例

```xml
<?pxml version="1.0"?>

<Page
    xmlns="pcl://ui"
    xmlns:x="pcl://language"
    xmlns:local="../Components"
    x:Name="DownloadPage">

    <Column
        Padding="{theme Spacing.Large}"
        Gap="{theme Spacing.Medium}">

        <Row
            Gap="12"
            Align="Center">

            <Text
                Class="PageTitle"
                Text="{loc Download.Title}" />

            <Spacer />

            <Button
                Text="{loc Common.Refresh}"
                Command="{cmd RefreshVersions}" />

        </Row>

        <NativeHost
            Kind="TextBox"
            Value="{bind Download.SearchText}"
            Placeholder="{loc Download.SearchPlaceholder}" />

        <x:If Condition="{bind Download.IsLoading}">

            <ProgressRing />

            <x:Else>

                <VirtualList
                    Items="{bind Download.VisibleVersions}"
                    Key="{item.Id}"
                    EstimatedItemHeight="56">

                    <Template As="item">

                        <local:VersionCard
                            Version="{item}"
                            Command="{cmd DownloadVersion}"
                            CommandParameter="{item.Id}" />

                    </Template>

                </VirtualList>

            </x:Else>

        </x:If>

    </Column>

</Page>
```

---

# 136. 编译结果

上面的源码在 Runtime 中不再存在：

```text
Page
Column
Button
VersionCard
XML DOM
Attribute String
```

而是：

```text
UiBlueprint
├ BlueprintNode[]
├ StaticComponentData
├ BindingProgram[]
├ DependencyIndex
├ StructuralProgram[]
├ TemplateProgram[]
├ StyleClassId[]
├ ResourceId[]
├ MotionTokenId[]
└ UiCommandId[]
```

---

# 137. PCL.UI.Next 最终运行链

```text
UiBlueprint
      ↓
Instantiate
      ↓
Reactive ECS
      ↓
Style
      ↓
Layout
      ↓
Input
      ↓
Animation
      ↓
RenderScene
      ↓
RenderDiff
      ↓
UiCommitBatch
      ↓
Platform Backend
```

PXML 在 `Instantiate` 前已经完成使命。

---

# 138. 最终系统架构

```text
┌──────────────────────────────────────┐
│         Independent PXML Repo        │
│                                      │
│ Syntax                               │
│ Semantic                             │
│ Compiler                             │
│ PXSS                                 │
│ CodeGen                              │
│ Packaging                            │
│ LSP                                  │
│ Formatter                            │
└───────────────────┬──────────────────┘
                    │
            Stable Compiler ABI
                    │
           ┌────────┴────────┐
           │                 │
           ▼                 ▼
 Generated C#          PXB / PXPK
           │                 │
           ▼                 ▼
      Native AOT       Package Loader
           │                 │
           └────────┬────────┘
                    ▼
                UiBlueprint
                    │
                    ▼
             PCL.UI.Next
                    │
                    ▼
               Reactive ECS
                    │
                    ▼
                 Layout
                    │
                    ▼
               Animation
                    │
                    ▼
              Render Scene
                    │
                    ▼
            Platform Backend
```

---

# 139. PXML 1.0 冻结范围

PXML 1.0 冻结：

```text
XML-like Element / Attribute Syntax

Primitive
Component
Property
Slot
Template

bind
cmd
event
res
loc
theme
motion
feature
const

If
Switch
For
VirtualList

PXSS
Style Class
Theme Token

Build Condition

Strong Static Type Checking
Static Dependency Analysis

Native Binding CodeGen
Typed Binding VM

PXI
PXB
PXPK

Zstd Packaging
Source Map

Compiler SDK
CLI
Formatter
Language Server

ABI Versioning
Deterministic Build
Sandbox Validation
```

---

# 140. PXML 1.0 不冻结

以下能力明确延期：

```text
复杂 CSS Selector

Arbitrary Keyframe Language

General Script Language

Reflection

Runtime C# Expression

Complex Generic Type System

Runtime Component Definition

Remote Namespace

Runtime Package Dependency Resolver

Arbitrary Plugin Native Extension
```

后续只能通过独立 RFC 引入。

---

# 141. 最终硬性原则

PXML 1.0 最终冻结以下不变量：

1. **PXML 是编译型语言，不是运行时 XML 框架。**
2. **Element 不等于 Control，不等于 Runtime Class。**
3. **Component 在编译阶段展开。**
4. **Binding 强类型、静态依赖、无反射。**
5. **业务操作只能通过 UiCommand。**
6. **核心 UI 优先生成 Native AOT 友好的静态代码。**
7. **动态 Package 使用受限、可验证的 Typed VM。**
8. **Release 不携带 PXML Parser。**
9. **Binary Package 必须有严格 ABI 和完整性验证。**
10. **Compiler、CLI、LSP、Formatter 共享唯一 Parser/Semantic 实现。**
11. **IDE 不得实现 PXML 的第二套语义。**
12. **PXML Compiler 必须维护在独立仓库。**
13. **PCL-N 主仓库只消费 Compiler Artifact 和 Runtime ABI。**
14. **PCL.UI.Next 不依赖 PXML Compiler。**
15. **PXML 不得演化成 XAML/DependencyProperty/Control Tree 的复制品。**

---

# 142. 仓库归属最终决议

建议正式建立：

```text
github.com/PCL-N-Edition/PXML
```

该仓库为 PXML 的 Canonical Repository。

仓库拥有：

```text
Language Specification
Compiler
Binary Specification
CLI
MSBuild Tooling
Source Generator
Language Server
Formatter
Conformance Tests
```

`PCL-N-Edition/PCL-N` 只拥有：

```text
PCL.UI.Next Runtime
PXML Runtime ABI Adapter
PXPK Loader
Generated UI
```

以后涉及：

```text
PXML grammar
Compiler behavior
Diagnostics
Code generation
PXB/PXPK writer
Formatter
LSP
```

的修改必须首先进入 PXML 独立仓库。

涉及：

```text
ECS
Layout
Input
Animation
Rendering
Backend
Runtime Blueprint execution
```

的修改进入 `PCL-N`。

这条仓库边界作为 **PXML 1.0 架构的一部分冻结**。

---

# 143. 最终定位

PXML 最终定位不是：

> “另一种 XAML”。

而是：

> **面向 Reactive ECS UI Runtime 的静态、强类型、可独立编译、可 Native AOT、可安全打包的 UI 描述语言。**

其完整工程链为：

```text
PXML
↓
Independent Compiler Toolchain
↓
Typed Semantic IR
↓
Native Code / Verified Binary
↓
UiBlueprint
↓
PCL.UI.Next Reactive ECS
```

**PXML 源代码属于 Authoring Layer；PXML Compiler 属于独立 Toolchain；PCL.UI.Next 属于 Runtime。三者永久保持边界。**
