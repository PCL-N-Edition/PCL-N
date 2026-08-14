# PCL.UI.Next Rendering Playground

从仓库根目录双击 `test-ui-next.bat`，或执行：

```powershell
dotnet run --project PCL.UI.Next.Playground/PCL.UI.Next.Playground.csproj --configuration Debug
```

该窗口用于人工验证当前已实现的新 UI Runtime 的完整链路：

- Blueprint 编译、实例化、绑定与结构化 `If` 重建；
- Scope / Entity / RenderNode generation 生命周期；
- Theme、Style、继承、Hover、Pressed、Focused 状态；
- Column、Row、Grid、Overlay/Container、Absolute 布局；
- 父约束文本换行与 Avalonia TextLayout shaping；
- Pointer hit test、capture、Click command、Tab/方向键焦点和 Enter 激活；
- scope-aware F5 shortcut；
- Tween、Spring 自然 Retarget、Reduced Motion、窗口缩放 FLIP；
- retained RenderScene、最小 RenderMutation、CommitBatch 与单 Avalonia Surface。
- Scroll viewport 裁剪、滚轮/拖拽、惯性、回弹与程序化滚动；
- 100,000 项变高 VirtualList、overscan、稳定 key、锚点修正与实体回收。
- Avalonia NativeHost TextBox、IME/selection/value 事件 journal；
- 独立 Semantic Tree、Avalonia AutomationPeer 与 Accessibility action → Command；
- Tooltip delay / pointer anchor / input pass-through；
- PopupScope、外部点击/Escape 关闭与焦点恢复；
- Modal barrier、背景 dim、输入阻断与 Tab focus trap；
- Navigation PageScope、Preparing/Entering/Active/Leaving/Dormant 状态、generation 中断与缓存复用。

建议操作：

1. 缩放窗口，观察 Grid、换行文本与 FLIP 连续性；
2. 快速来回划过并点击按钮，观察 Hover/Pressed 自然打断；
3. 连续点击 `Retarget spring`，确认动画从当前值和速度继续；
4. 用 Tab/方向键移动焦点，用 Enter/Space 激活；
5. 切换结构和主题，观察局部 retained diff；
6. 开关 Reduced Motion 后再次触发动画；
7. 按 F5，通过 Shortcut → Command 边界复位。
8. 在 100,000 项列表上滚轮或拖拽，再点 `Jump to 50,000`，观察窗口标题中的 retained node 数量保持稳定。
9. 在 Native TextBox 中输入和移动选区，观察底部 NativeHost journal 状态；
10. 悬停 `Hover tooltip`，确认延迟出现且不抢鼠标命中；
11. 打开 Popup，测试外部点击/Escape 与焦点恢复；
12. 打开 Modal，确认背景无法点击、Tab 不越过 Modal；
13. 快速点击 `Navigate A / B`，确认中断时无跳变且旧 generation 不覆盖新页面；
14. 用 Narrator/Accessibility Insights 检查按钮、文本、TextBox 与页面语义树；窗口标题会显示 semantic/native/overlay/page 实时计数。
