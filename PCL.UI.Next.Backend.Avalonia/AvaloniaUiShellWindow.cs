using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using PCL.UI.Next;

namespace PCL.UI.Next.Backend.Avalonia;

/// <summary>
/// Native window lifetime around one UI.Next scene surface. The only overlay is the platform
/// window-action affordance; PXML title, navigation, content, and all product geometry are
/// committed by <see cref="AvaloniaUiSceneSurface"/> from the immutable renderer scene.
/// </summary>
public sealed class AvaloniaUiShellWindow : Window
{
    private readonly AvaloniaUiSceneSurface _surface;
    private readonly AvaloniaNativeWindowActions _windowActions;

    public AvaloniaUiShellWindow(XsrUiShell shell)
    {
        ArgumentNullException.ThrowIfNull(shell);
        Title = shell.Title;
        Width = 1280;
        Height = 800;
        MinWidth = 960;
        MinHeight = 620;
        CanResize = true;
        ShowInTaskbar = true;
        WindowDecorations = WindowDecorations.None;
        ExtendClientAreaToDecorationsHint = true;

        _surface = new AvaloniaUiSceneSurface(shell);
        _surface.TitleBarDragRequested += OnTitleBarDragRequested;
        _surface.SceneCommitted += OnSceneCommitted;
        _windowActions = new AvaloniaNativeWindowActions(_surface)
        {
            HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right,
            VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Top,
        };
        _windowActions.MinimizeRequested += (_, _) => WindowState = WindowState.Minimized;
        _windowActions.MaximizeRequested += OnMaximizeRequested;
        _windowActions.CloseRequested += (_, _) => Close();

        // This overlay has no application layout. The scene surface below remains the sole
        // projection of PXML/UI.Next entities; these controls are native window affordances.
        Grid root = new();
        root.Children.Add(_surface);
        root.Children.Add(_windowActions);
        Content = root;

        TransparencyLevelHint = [WindowTransparencyLevel.None];
    }

    protected override void OnClosed(EventArgs e)
    {
        _surface.TitleBarDragRequested -= OnTitleBarDragRequested;
        _surface.SceneCommitted -= OnSceneCommitted;
        _surface.Dispose();
        _windowActions.Dispose();
        base.OnClosed(e);
    }

    private void ApplyTransparencyHint(XsrUiScene scene)
    {
        XsrUiSurfaceKind titleSurface = scene.Nodes
            .FirstOrDefault(node => node.Role == XsrUiSemanticRole.TitleBar)
            .VisualStyle.Surface;
        TransparencyLevelHint = titleSurface == XsrUiSurfaceKind.Glass
            ? [WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Transparent]
            : [WindowTransparencyLevel.None];
    }

    private void OnSceneCommitted(object? sender, AvaloniaUiSceneCommittedEventArgs e) =>
        ApplyTransparencyHint(e.Scene);

    private void OnMaximizeRequested(object? sender, EventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
        _windowActions.SetMaximized(WindowState == WindowState.Maximized);
    }

    private void OnTitleBarDragRequested(object? sender, PointerPressedEventArgs e)
    {
        BeginMoveDrag(e);
    }
}
