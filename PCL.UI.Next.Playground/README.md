# PCL.UI.Next Rendering Playground

从仓库根目录双击 `test-ui-next.bat`，或执行：

```powershell
dotnet run --project PCL.UI.Next.Playground/PCL.UI.Next.Playground.csproj --configuration Debug
```

该窗口用于人工验证当前已实现的新 UI Runtime（截至 Rendering）的完整链路：

- Blueprint 编译、实例化、绑定与结构化 `If` 重建；
- Scope / Entity / RenderNode generation 生命周期；
- Theme、Style、继承、Hover、Pressed、Focused 状态；
- Column、Row、Grid、Overlay/Container、Absolute 布局；
- 父约束文本换行与 Avalonia TextLayout shaping；
- Pointer hit test、capture、Click command、Tab/方向键焦点和 Enter 激活；
- scope-aware F5 shortcut；
- Tween、Spring 自然 Retarget、Reduced Motion、窗口缩放 FLIP；
- retained RenderScene、最小 RenderMutation、CommitBatch 与单 Avalonia Surface。

建议操作：

1. 缩放窗口，观察 Grid、换行文本与 FLIP 连续性；
2. 快速来回划过并点击按钮，观察 Hover/Pressed 自然打断；
3. 连续点击 `Retarget spring`，确认动画从当前值和速度继续；
4. 用 Tab/方向键移动焦点，用 Enter/Space 激活；
5. 切换结构和主题，观察局部 retained diff；
6. 开关 Reduced Motion 后再次触发动画；
7. 按 F5，通过 Shortcut → Command 边界复位。

尚未实现的后续 Runtime 能力（如 Scroll/Virtualization、Navigation、Accessibility）不在该窗口的覆盖范围内。
