using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using PCL.UI.Next;

namespace PCL.UI.Next.Backend.Avalonia;

/// <summary>
/// Native window lifetime around one UI.Next scene surface. The window owns the frameless
/// chrome: a transparent window whose rounded, shadowed surface hosts the scene, a drag-through
/// title bar with double-click maximize, eight invisible resize grips, and the sole native
/// overlay (window actions). Product geometry is committed by
/// <see cref="AvaloniaUiSceneSurface"/> from the immutable renderer scene.
/// </summary>
public sealed class AvaloniaUiShellWindow : Window
{
    private const double ChromeMargin = 10;
    private const double ChromeCornerRadius = 8;

    private static readonly BoxShadows WindowShadow = new(new BoxShadow
    {
        Blur = 6,
        Spread = 0,
        Color = Color.FromArgb(0x48, 0, 0, 0),
    });

    private readonly XsrUiShell _shell;
    private readonly AvaloniaUiSceneSurface _surface;
    private readonly AvaloniaNativeWindowActions _windowActions;
    private readonly Border _shadowSurface;
    private readonly Border _chromeSurface;
    private readonly Grid _root;
    private bool _disposed;

    public AvaloniaUiShellWindow(XsrUiShell shell, Stream? iconStream = null)
    {
        ArgumentNullException.ThrowIfNull(shell);
        _shell = shell;
        Title = shell.Title;
        Width = 850;
        Height = 500;
        MinWidth = 810;
        MinHeight = 470;
        CanResize = true;
        ShowInTaskbar = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowDecorations = WindowDecorations.None;
        ExtendClientAreaToDecorationsHint = true;
        // Per-pixel alpha keeps the rounded corners and the outer shadow seam-free; the scene
        // paints the opaque application surface itself.
        Background = Brushes.Transparent;
        TransparencyBackgroundFallback = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent, WindowTransparencyLevel.None];
        if (iconStream is not null)
        {
            Icon = new WindowIcon(iconStream);
        }

        Opacity = shell.Renderer.ReducedMotion ? 1 : 0;

        _shadowSurface = new Border
        {
            Margin = new Thickness(ChromeMargin),
            CornerRadius = new CornerRadius(ChromeCornerRadius),
            BoxShadow = WindowShadow,
            // A 1/255 hit-test dummy keeps the shadow region inside the transparent window
            // instead of leaving a 1-pixel seam on compositorless setups.
            Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)),
        };
        _chromeSurface = new Border
        {
            Margin = new Thickness(ChromeMargin),
            CornerRadius = new CornerRadius(ChromeCornerRadius),
            ClipToBounds = true,
        };

        _surface = new AvaloniaUiSceneSurface(shell);
        _surface.TitleBarDragRequested += OnTitleBarDragRequested;
        _surface.SceneCommitted += OnSceneCommitted;
        _windowActions = new AvaloniaNativeWindowActions(_surface)
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
        };
        _windowActions.MinimizeRequested += (_, _) => WindowState = WindowState.Minimized;
        _windowActions.MaximizeRequested += OnMaximizeRequested;
        _windowActions.CloseRequested += (_, _) => Close();

        // This overlay has no application layout. The scene surface below remains the sole
        // projection of PXML/UI.Next entities; these controls are native window affordances.
        Grid chrome = new();
        chrome.Children.Add(_surface);
        chrome.Children.Add(_windowActions);
        _chromeSurface.Child = chrome;

        _root = new Grid();
        _root.Children.Add(_shadowSurface);
        _root.Children.Add(_chromeSurface);
        foreach (Border grip in CreateResizeGrips())
        {
            _root.Children.Add(grip);
        }

        Content = _root;
        PropertyChanged += OnWindowPropertyChanged;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        RunEntranceAnimation();
    }

    protected override void OnClosed(EventArgs e)
    {
        _disposed = true;
        PropertyChanged -= OnWindowPropertyChanged;
        _surface.TitleBarDragRequested -= OnTitleBarDragRequested;
        _surface.SceneCommitted -= OnSceneCommitted;
        _surface.Dispose();
        _windowActions.Dispose();
        base.OnClosed(e);
    }

    /// <summary>
    /// Fades the window in and lets it rise and straighten into place. The settle is critically
    /// damped (no overshoot) and starts from the presented value; reduced motion keeps only the
    /// fade, and full reduced-motion runs skip the entrance entirely.
    /// </summary>
    private void RunEntranceAnimation()
    {
        if (Opacity >= 1)
        {
            return;
        }

        TranslateTransform translate = new(0, AvaloniaMotionTokens.WindowEntranceRisePixels);
        RotateTransform rotate = new(AvaloniaMotionTokens.WindowEntranceAngleDegrees);
        _root.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
        _root.RenderTransform = new TransformGroup { Children = { translate, rotate } };

        int piecesRemaining = 2;
        void OnPieceCompleted()
        {
            piecesRemaining--;
            if (piecesRemaining == 0 && !_disposed)
            {
                _root.RenderTransform = null;
            }
        }

        double delay = AvaloniaMotionTokens.WindowEntranceDelayMilliseconds;
        AvaloniaUiMotion.Animate(
            this, "opacity", () => Opacity, value => Opacity = value, 1,
            AvaloniaMotionTokens.WindowFadeMilliseconds, delayMilliseconds: delay);
        AvaloniaUiMotion.Animate(
            this, "rise", () => translate.Y, value => translate.Y = value, 0,
            AvaloniaMotionTokens.WindowRiseMilliseconds, delayMilliseconds: delay,
            completed: OnPieceCompleted);
        AvaloniaUiMotion.Animate(
            this, "straighten", () => rotate.Angle, value => rotate.Angle = value, 0,
            AvaloniaMotionTokens.WindowRotateMilliseconds, delayMilliseconds: delay,
            completed: OnPieceCompleted);
    }

    private IEnumerable<Border> CreateResizeGrips()
    {
        // Invisible edge and corner grips hand the press to the native resize loop, which keeps
        // Aero Snap and system resize behaviour intact.
        (HorizontalAlignment Horizontal, VerticalAlignment Vertical, double Width, double Height, Thickness Margin, StandardCursorType Cursor, WindowEdge Edge)[] grips =
        [
            (HorizontalAlignment.Stretch, VerticalAlignment.Top, double.NaN, 4, new Thickness(22, ChromeMargin, 22, 0), StandardCursorType.SizeNorthSouth, WindowEdge.North),
            (HorizontalAlignment.Stretch, VerticalAlignment.Bottom, double.NaN, 4, new Thickness(22, 0, 22, ChromeMargin), StandardCursorType.SizeNorthSouth, WindowEdge.South),
            (HorizontalAlignment.Left, VerticalAlignment.Stretch, 4, double.NaN, new Thickness(ChromeMargin, 22, 0, 22), StandardCursorType.SizeWestEast, WindowEdge.West),
            (HorizontalAlignment.Right, VerticalAlignment.Stretch, 4, double.NaN, new Thickness(0, 22, ChromeMargin, 22), StandardCursorType.SizeWestEast, WindowEdge.East),
            (HorizontalAlignment.Left, VerticalAlignment.Top, 14, 14, new Thickness(8), StandardCursorType.TopLeftCorner, WindowEdge.NorthWest),
            (HorizontalAlignment.Right, VerticalAlignment.Top, 14, 14, new Thickness(0, 8, 8, 0), StandardCursorType.TopRightCorner, WindowEdge.NorthEast),
            (HorizontalAlignment.Left, VerticalAlignment.Bottom, 14, 14, new Thickness(8, 0, 0, 8), StandardCursorType.BottomLeftCorner, WindowEdge.SouthWest),
            (HorizontalAlignment.Right, VerticalAlignment.Bottom, 14, 14, new Thickness(0, 0, 8, 8), StandardCursorType.BottomRightCorner, WindowEdge.SouthEast),
        ];

        foreach ((HorizontalAlignment horizontal, VerticalAlignment vertical, double width, double height,
                     Thickness margin, StandardCursorType cursor, WindowEdge edge) in grips)
        {
            Border grip = new()
            {
                Background = Brushes.Transparent,
                HorizontalAlignment = horizontal,
                VerticalAlignment = vertical,
                Width = width,
                Height = height,
                Margin = margin,
                Cursor = new Cursor(cursor),
                Tag = edge,
                ZIndex = 1000,
            };
            grip.PointerPressed += OnResizeGripPressed;
            yield return grip;
        }
    }

    private void OnResizeGripPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border { Tag: WindowEdge edge }
            && CanResize
            && WindowState == WindowState.Normal
            && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginResizeDrag(edge, e);
            e.Handled = true;
        }
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty)
        {
            bool maximized = e.NewValue is WindowState state && state == WindowState.Maximized;
            _windowActions.SetMaximized(maximized);
            UpdateChromeForState(maximized);
        }
    }

    /// <summary>
    /// A maximized frameless window is flush with the screen edges: no shadow margin and no
    /// rounding, exactly like the legacy chrome.
    /// </summary>
    private void UpdateChromeForState(bool maximized)
    {
        Thickness margin = maximized ? new Thickness(0) : new Thickness(ChromeMargin);
        CornerRadius radius = maximized ? new CornerRadius(0) : new CornerRadius(ChromeCornerRadius);
        _shadowSurface.Margin = margin;
        _shadowSurface.CornerRadius = radius;
        _shadowSurface.BoxShadow = maximized ? default : WindowShadow;
        _chromeSurface.Margin = margin;
        _chromeSurface.CornerRadius = radius;
    }

    private void OnMaximizeRequested(object? sender, EventArgs e) => ToggleMaximized();

    private void ToggleMaximized() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void ApplyTransparencyHint(XsrUiScene scene)
    {
        XsrUiSurfaceKind titleSurface = scene.Nodes
            .FirstOrDefault(node => node.Role == XsrUiSemanticRole.TitleBar)
            .VisualStyle.Surface;
        TransparencyLevelHint = titleSurface == XsrUiSurfaceKind.Glass
            ? [WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Transparent]
            : [WindowTransparencyLevel.Transparent, WindowTransparencyLevel.None];
    }

    private void OnSceneCommitted(object? sender, AvaloniaUiSceneCommittedEventArgs e) =>
        ApplyTransparencyHint(e.Scene);

    private void OnTitleBarDragRequested(object? sender, PointerPressedEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximized();
            e.Handled = true;
            return;
        }

        BeginMoveDrag(e);
    }
}
