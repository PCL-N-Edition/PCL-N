using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Nexa.UI.Next;

namespace Nexa.UI.Next.Backend.Avalonia;

/// <summary>
/// Composited window around an opaque UI.Next scene. Native decorations retain platform animation,
/// resize and fullscreen behavior. macOS keeps its system traffic lights; Windows uses DWM.
/// The host reserves shadow space outside the scene and clips its presentation to rounded corners.
/// </summary>
public sealed class AvaloniaUiShellWindow : Window
{
    // Shadow space outside the preserved scene viewport, expressed in DIPs.
    private const double ChromeMargin = 24;
    // The compositor owns the large radius; opaque fallback uses the same native region.
    private const double ChromeCornerRadius = 24;
    private const double CloseIconSize = 112;


    private readonly XsrUiShell _shell;
    private readonly AvaloniaUiSceneSurface _surface;
    private readonly AvaloniaNativeWindowActions _windowActions;
    private readonly Border _shadowSurface;
    private readonly Border _chromeSurface;
    private readonly Grid _maskedContent;
    private readonly Grid _root;
    private readonly Bitmap? _closeIcon;
    private EllipseGeometry? _revealMask;
    private Image? _startupIcon;
    private ScaleTransform? _startupIconScale;
    private ScaleTransform? _closeIconScale;
    private bool _awaitingFirstSceneCommit;
    private bool _closeAnimationStarted;
    private bool _disposed;
    private bool _updatingChrome;
    private bool _nativeFrameUpdateQueued;
    private double _restoredFrameInset;
    internal bool UsesCompositedEdges => ActualTransparencyLevel == WindowTransparencyLevel.Transparent
        && !AvaloniaWindowsFrame.IsLayered(this);

    public AvaloniaUiShellWindow(XsrUiShell shell, Stream? iconStream = null)
    {
        ArgumentNullException.ThrowIfNull(shell);
        _shell = shell;
        Title = shell.Title;
        _restoredFrameInset = OperatingSystem.IsMacOS() ? 0 : ChromeMargin;
        Width = 850 + _restoredFrameInset * 2;
        Height = 500 + _restoredFrameInset * 2;
        MinWidth = 810 + _restoredFrameInset * 2;
        MinHeight = 470 + _restoredFrameInset * 2;
        CanResize = true;
        ShowInTaskbar = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        // Keep WS_CAPTION / resizable-window styles on Windows so DWM owns native min/max
        // transitions. Only suppress Avalonia's drawn decorations, not the native capability.
        WindowDecorations = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? WindowDecorations.Full : WindowDecorations.None;
        ExtendClientAreaToDecorationsHint = true;
        if (OperatingSystem.IsWindows())
        {
            Resources[typeof(WindowDrawnDecorations)] = new ControlTheme
            {
                TargetType = typeof(WindowDrawnDecorations),
                Setters = { new Setter(WindowDrawnDecorations.TemplateProperty, new EmptyWindowDecorationsTemplate()) },
            };
        }
        // Transparent edge pixels are composited; the inner surface remains fully opaque.
        // A layered Win32 fallback is rejected after the platform handle becomes available.
        Background = Brushes.Transparent;
        TransparencyBackgroundFallback = new SolidColorBrush(Color.FromRgb(shell.Palette.WindowBackground.Red, shell.Palette.WindowBackground.Green, shell.Palette.WindowBackground.Blue));
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent, WindowTransparencyLevel.None];
        ExtendClientAreaTitleBarHeightHint = XsrUiShell.TitleBarHeight;
        if (iconStream is not null)
        {
            // The same product icon closes the loop: taskbar icon at rest, and the image the
            // window collapses into on close.
            _closeIcon = new Bitmap(iconStream);
            Icon = new WindowIcon(_closeIcon);
        }

        _shadowSurface = new Border
        {
            Margin = new Thickness(0),
            CornerRadius = new CornerRadius(ChromeCornerRadius),
            BoxShadow = default,
            Background = TransparencyBackgroundFallback,
            IsHitTestVisible = false,
        };
        _chromeSurface = new Border
        {
            Margin = new Thickness(0),
            CornerRadius = new CornerRadius(ChromeCornerRadius),
            ClipToBounds = true,
        };

        _surface = new AvaloniaUiSceneSurface(shell);
        _surface.TitleBarDragRequested += OnTitleBarDragRequested;
        _surface.SceneCommitted += OnSceneCommitted;
        _windowActions = new AvaloniaNativeWindowActions(_surface, () => _shell.Renderer.ReducedMotion)
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
        };
        _windowActions.MinimizeRequested += (_, _) => WindowState = WindowState.Minimized;
        _windowActions.MaximizeRequested += OnMaximizeRequested;
        _windowActions.CloseRequested += (_, _) => RequestClose();

        // This overlay has no application layout. The scene surface below remains the sole
        // projection of PXML/UI.Next entities; these controls are native window affordances.
        Grid chrome = new();
        chrome.Children.Add(_surface);
        if (OperatingSystem.IsMacOS()) chrome.Children.Add(new NativeTitleBarHitRegion(_surface));
        if (!OperatingSystem.IsMacOS()) chrome.Children.Add(_windowActions);
        _chromeSurface.Child = chrome;

        // Everything the circular mask may clip lives in this subtree; the product icon is a
        // sibling above it so the reveal can collapse to (or expand from) radius zero while
        // the icon stays fully visible.
        _maskedContent = new Grid();
        _maskedContent.Children.Add(_shadowSurface);
        _maskedContent.Children.Add(_chromeSurface);
        foreach (Border grip in OperatingSystem.IsMacOS() ? [] : CreateResizeGrips())
        {
            _maskedContent.Children.Add(grip);
        }

        _root = new Grid();
        _root.Children.Add(_maskedContent);

        Content = _root;
        _root.SizeChanged += (_, _) => UpdateChromeForState(WindowState is WindowState.Maximized or WindowState.FullScreen);
        ScalingChanged += (_, _) => UpdateChromeForState(WindowState is WindowState.Maximized or WindowState.FullScreen);
        PropertyChanged += OnWindowPropertyChanged;
    }

    /// <summary>
    /// Raised once the startup reveal has fully expanded (or was skipped under reduced motion).
    /// The host dismisses the splash at this point, so the icon never leaves the screen until
    /// the window has taken over.
    /// </summary>
    public event EventHandler? StartupRevealCompleted;

    internal AvaloniaUiSceneSurface Surface => _surface;

    // Native hit testing owns title-bar double-click preferences, including macOS zoom/minimize.
    // Interactive scene nodes remain above this behavior logically: the hit region excludes them.
    private sealed class NativeTitleBarHitRegion : Control, global::Avalonia.Rendering.ICustomHitTest
    {
        private readonly AvaloniaUiSceneSurface _surface;
        internal NativeTitleBarHitRegion(AvaloniaUiSceneSurface surface)
        {
            _surface = surface;
            WindowDecorationProperties.SetElementRole(this, WindowDecorationsElementRole.TitleBar);
        }
        public bool HitTest(Point point) => _surface.IsNativeTitleBarPoint(new(point.X, point.Y));
    }

    private sealed class EmptyWindowDecorationsTemplate : IWindowDrawnDecorationsTemplate
    {
        public TemplateResult<WindowDrawnDecorationsContent> Build() =>
            new(new WindowDrawnDecorationsContent(), new NameScope());

        object ITemplate.Build() => Build().Result;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _ = AvaloniaWindowsFrame.SuppressBorder(this);
        UpdateChromeForState(WindowState is WindowState.Maximized or WindowState.FullScreen);
        if (_shell.Renderer.ReducedMotion)
        {
            StartupRevealCompleted?.Invoke(this, EventArgs.Empty);
            return;
        }

        // The reveal must expand over rendered content, so it starts at the surface's first
        // committed scene rather than at Opened — otherwise the mask grows across a blank
        // window and the product UI simply pops in when the first frame lands.
        if (_surface.Scene is not null)
        {
            RunStartupReveal();
        }
        else
        {
            _awaitingFirstSceneCommit = true;
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_closeAnimationStarted || _shell.Renderer.ReducedMotion)
        {
            return;
        }

        // System-initiated closes (Alt+F4, taskbar) arrive here; the window-action close button
        // routes through RequestClose because a programmatic Close bypasses this override on
        // some platforms. Either way the collapse plays once before the real close.
        e.Cancel = true;
        _closeAnimationStarted = true;
        PlayCloseCollapse();
    }

    /// <summary>
    /// Plays the close collapse and then closes for real. The collapse runs exactly once; a
    /// close request that arrives while it plays falls through to the plain close.
    /// </summary>
    private void RequestClose()
    {
        if (_closeAnimationStarted || _shell.Renderer.ReducedMotion)
        {
            Close();
            return;
        }

        _closeAnimationStarted = true;
        PlayCloseCollapse();
    }

    protected override void OnClosed(EventArgs e)
    {
        _disposed = true;
        AvaloniaUiMotion.CancelAll(this);
        PropertyChanged -= OnWindowPropertyChanged;
        _surface.TitleBarDragRequested -= OnTitleBarDragRequested;
        _surface.SceneCommitted -= OnSceneCommitted;
        _surface.Dispose();
        _windowActions.Dispose();
        base.OnClosed(e);
    }

    /// <summary>
    /// Expands a smooth circular mask from radius zero out to the full window. The product icon
    /// is deliberately outside the masked subtree, so the reveal never clips it: the splash
    /// shows the icon, the mask grows behind it, and the window's own icon copy takes over when
    /// the splash closes. Reduced motion skips the mask entirely.
    /// </summary>
    private void RunStartupReveal()
    {
        double width = _root.Bounds.Width;
        double height = _root.Bounds.Height;
        if (width <= 0 || height <= 0)
        {
            OnStartupRevealCompleted();
            return;
        }

        Point center = new(width / 2, height / 2);
        double fullRadius = Math.Sqrt((width * width) + (height * height)) / 2;
        EllipseGeometry mask = new()
        {
            Center = center,
            RadiusX = 0,
            RadiusY = 0,
        };
        _revealMask = mask;
        _maskedContent.Clip = mask;
        ApplyNativeShape();
        if (_closeIcon is not null)
        {
            // The icon the window inherits from the splash: identical pixels at the identical
            // position, layered above the mask so it never disappears with the reveal.
            _startupIconScale = new ScaleTransform(1, 1);
            _startupIcon = new Image
            {
                Source = _closeIcon,
                Width = CloseIconSize,
                Height = CloseIconSize,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
                RenderTransform = _startupIconScale,
                RenderTransformOrigin = RelativePoint.Center,
            };
            _root.Children.Add(_startupIcon);
        }

        AvaloniaUiMotion.Animate(
            this,
            "startup-reveal",
            () => mask.RadiusX,
            value =>
            {
                mask.RadiusX = value;
                mask.RadiusY = value;
                ApplyNativeShape();

                // Mutating the clip geometry alone does not invalidate the visual tree; the
                // mask would otherwise apply only for the first frame and never redraw.
                _maskedContent.InvalidateVisual();
            },
            fullRadius,
            AvaloniaMotionTokens.StartupRevealMilliseconds,
            AvaloniaUiMotion.EaseOut,
            completed: OnStartupRevealCompleted,
            reducedMotion: () => _shell.Renderer.ReducedMotion);
    }

    private void OnStartupRevealCompleted()
    {
        if (_disposed)
        {
            return;
        }

        _maskedContent.Clip = null;
        _revealMask = null;
        ApplyNativeShape();
        StartupRevealCompleted?.Invoke(this, EventArgs.Empty);
        if (_startupIcon is not null && _startupIconScale is not null)
        {
            // The icon continues where the splash left off: a small bounce upward, then it
            // shrinks into the content and is removed.
            ScaleTransform scale = _startupIconScale;
            Image icon = _startupIcon;
            AvaloniaUiMotion.Animate(
                this, ("startup-icon", "up"), () => scale.ScaleX, value =>
                {
                    scale.ScaleX = value;
                    scale.ScaleY = value;
                    ApplyNativeShape();
                },
                1.12,
                AvaloniaMotionTokens.IconBounceMilliseconds,
                AvaloniaUiMotion.EaseOut,
                completed: () => AvaloniaUiMotion.Animate(
                    this, ("startup-icon", "down"), () => scale.ScaleX, value =>
                    {
                        scale.ScaleX = value;
                        scale.ScaleY = value;
                        ApplyNativeShape();
                    },
                    0,
                    AvaloniaMotionTokens.IconCollapseMilliseconds,
                    AvaloniaUiMotion.EaseIn,
                    reducedMotion: () => _shell.Renderer.ReducedMotion,
                    completed: () =>
                    {
                        if (!_disposed)
                        {
                            _root.Children.Remove(icon);
                        }
                    }));
        }
    }

    private void PlayCloseCollapse()
    {
        // The circular reveal clips scene content, not the system's outside shadow.
        _awaitingFirstSceneCommit = false;
        double? presentedRadius = _revealMask?.RadiusX;
        double presentedIconScale = _startupIcon is not null ? _startupIconScale?.ScaleX ?? 0 : 0;
        AvaloniaUiMotion.Cancel(this, "startup-reveal");
        AvaloniaUiMotion.Cancel(this, ("startup-icon", "up"));
        AvaloniaUiMotion.Cancel(this, ("startup-icon", "down"));
        if (_startupIcon is not null)
        {
            _root.Children.Remove(_startupIcon);
            _startupIcon = null;
        }
        _closeIconScale = new ScaleTransform(presentedIconScale, presentedIconScale);
        _shadowSurface.IsVisible = false;
        _shadowSurface.BoxShadow = default;
        double width = _root.Bounds.Width;
        double height = _root.Bounds.Height;
        if (width <= 0 || height <= 0)
        {
            Close();
            return;
        }

        // Close reverses the startup sequence: the window content contracts back to radius
        // zero while the icon bounces back in above it, then the icon folds away and the
        // window closes for real.
        Point center = new(width / 2, height / 2);
        double fullRadius = Math.Sqrt((width * width) + (height * height)) / 2;
        EllipseGeometry mask = new()
        {
            Center = center,
            RadiusX = presentedRadius ?? fullRadius,
            RadiusY = presentedRadius ?? fullRadius,
        };
        _revealMask = mask;
        _maskedContent.Clip = mask;
        ApplyNativeShape();

        int piecesRemaining = _closeIcon is null ? 1 : 2;
        void OnCollapsePieceCompleted()
        {
            piecesRemaining--;
            if (piecesRemaining != 0 || _disposed)
            {
                return;
            }

            AvaloniaUiMotion.Animate(
                this,
                "close-icon-fold",
                () => _closeIconScale?.ScaleX ?? 0,
                value =>
                {
                    if (_closeIconScale is not null)
                    {
                        _closeIconScale.ScaleX = value;
                        _closeIconScale.ScaleY = value;
                        ApplyNativeShape();
                    }
                },
                0,
                _closeIconScale is null ? 1 : AvaloniaMotionTokens.IconCollapseMilliseconds,
                AvaloniaUiMotion.EaseIn,
                completed: () =>
                {
                    // Keep the zero-radius mask until native destruction; restoring the
                    // full region here flashes the window for one final compositor frame.
                    Close();
                },
                reducedMotion: () => _shell.Renderer.ReducedMotion);
        }

        AvaloniaUiMotion.Animate(
            this,
            "close-collapse",
            () => mask.RadiusX,
            value =>
            {
                mask.RadiusX = value;
                mask.RadiusY = value;
                ApplyNativeShape();
                _maskedContent.InvalidateVisual();
            },
            0,
            AvaloniaMotionTokens.CloseCollapseMilliseconds,
            AvaloniaUiMotion.EaseIn,
            completed: OnCollapsePieceCompleted,
            reducedMotion: () => _shell.Renderer.ReducedMotion);

        if (_closeIcon is not null)
        {
            Image iconOverlay = new()
            {
                Source = _closeIcon,
                Width = CloseIconSize,
                Height = CloseIconSize,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
                RenderTransform = _closeIconScale,
                RenderTransformOrigin = RelativePoint.Center,
            };
            _root.Children.Add(iconOverlay);
            ScaleTransform scale = _closeIconScale;
            AvaloniaUiMotion.Animate(
                this, ("close-icon", "up"), () => scale.ScaleX, value =>
                {
                    scale.ScaleX = value;
                    scale.ScaleY = value;
                    ApplyNativeShape();
                },
                1.12,
                AvaloniaMotionTokens.CloseCollapseMilliseconds / 2,
                AvaloniaUiMotion.EaseOut,
                completed: () => AvaloniaUiMotion.Animate(
                    this, ("close-icon", "settle"), () => scale.ScaleX, value =>
                    {
                        scale.ScaleX = value;
                        scale.ScaleY = value;
                        ApplyNativeShape();
                    },
                    1,
                    AvaloniaMotionTokens.CloseCollapseMilliseconds / 2,
                    AvaloniaUiMotion.EaseIn,
                    completed: OnCollapsePieceCompleted,
                    reducedMotion: () => _shell.Renderer.ReducedMotion));
        }
    }

    private IEnumerable<Border> CreateResizeGrips()
    {
        // Invisible edge and corner grips hand the press to the native resize loop, which keeps
        // Aero Snap and system resize behaviour intact.
        (HorizontalAlignment Horizontal, VerticalAlignment Vertical, double Width, double Height, Thickness Margin, StandardCursorType Cursor, WindowEdge Edge)[] grips =
        [
            (HorizontalAlignment.Stretch, VerticalAlignment.Top, double.NaN, 8, new Thickness(8, 0, 8, 0), StandardCursorType.SizeNorthSouth, WindowEdge.North),
            (HorizontalAlignment.Stretch, VerticalAlignment.Bottom, double.NaN, 8, new Thickness(8, 0, 8, 0), StandardCursorType.SizeNorthSouth, WindowEdge.South),
            (HorizontalAlignment.Left, VerticalAlignment.Stretch, 8, double.NaN, new Thickness(0, 8, 0, 8), StandardCursorType.SizeWestEast, WindowEdge.West),
            (HorizontalAlignment.Right, VerticalAlignment.Stretch, 8, double.NaN, new Thickness(0, 8, 0, 8), StandardCursorType.SizeWestEast, WindowEdge.East),
            (HorizontalAlignment.Left, VerticalAlignment.Top, 8, 8, new Thickness(0), StandardCursorType.TopLeftCorner, WindowEdge.NorthWest),
            (HorizontalAlignment.Right, VerticalAlignment.Top, 8, 8, new Thickness(0), StandardCursorType.TopRightCorner, WindowEdge.NorthEast),
            (HorizontalAlignment.Left, VerticalAlignment.Bottom, 8, 8, new Thickness(0), StandardCursorType.BottomLeftCorner, WindowEdge.SouthWest),
            (HorizontalAlignment.Right, VerticalAlignment.Bottom, 8, 8, new Thickness(0), StandardCursorType.BottomRightCorner, WindowEdge.SouthEast),
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
        // Avalonia 12 has no public Visual.RenderScalingProperty; the scaling fact lives on
        // TopLevel as a plain property, so subscribe to the TypedVisualTreeMutation/Bounds
        // signals that accompany a DPI change instead — Bounds covers the common reflow, and
        // UpdateChromeForState re-reads RenderScaling at call time anyway.
        if (e.Property == BoundsProperty || e.Property == Window.WindowStateProperty || e.Property == ActualTransparencyLevelProperty)
            UpdateChromeForState(WindowState is WindowState.Maximized or WindowState.FullScreen);
        if (e.Property == Window.WindowStateProperty)
        {
            bool maximized = e.NewValue is WindowState state && state is WindowState.Maximized or WindowState.FullScreen;
            _windowActions.SetMaximized(maximized);
            UpdateChromeForState(maximized);
            _ = AvaloniaWindowsFrame.SuppressBorder(this);
        }
    }

    /// <summary>
    /// A maximized frameless window is flush with the screen edges: no shadow margin and no
    /// rounding, exactly like the legacy chrome.
    /// </summary>
    private void UpdateChromeForState(bool maximized)
    {
        if (_updatingChrome) return;
        _updatingChrome = true;
        try
        {
            if (AvaloniaWindowsFrame.IsLayered(this)) TransparencyLevelHint = [WindowTransparencyLevel.None];
            bool composited = UsesCompositedEdges;
            double restoredInset = composited && !OperatingSystem.IsMacOS() ? ChromeMargin : 0;
            double delta = restoredInset - _restoredFrameInset;
            _restoredFrameInset = restoredInset;
            MinWidth = 810 + restoredInset * 2;
            MinHeight = 470 + restoredInset * 2;
            if (delta != 0 && WindowState == WindowState.Normal)
            {
                Width += delta * 2;
                Height += delta * 2;
            }
            double inset = maximized ? 0 : restoredInset;
            _shell.PublishWindowMetrics(AvaloniaMacWindow.ConfigureAndMeasure(this), WindowState == WindowState.FullScreen, 0);
            // An opaque native window still paints behind the rounded scene clip. Match the
            // title region as well as the body so DWM's smaller corner mask reveals no white rim.
            var title = _shell.Palette.TitleBarBackground;
            var body = _shell.Palette.WindowBackground;
            double split = Math.Clamp(XsrUiShell.TitleBarHeight / Math.Max(1, Bounds.Height), 0, 1);
            var opaqueBackground = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = new GradientStops
            {
                new(Color.FromRgb(title.Red, title.Green, title.Blue), 0),
                new(Color.FromRgb(title.Red, title.Green, title.Blue), split),
                new(Color.FromRgb(body.Red, body.Green, body.Blue), split),
                new(Color.FromRgb(body.Red, body.Green, body.Blue), 1),
            },
            };
            Background = composited ? Brushes.Transparent : opaqueBackground;
            TransparencyBackgroundFallback = opaqueBackground;
            _chromeSurface.Background = opaqueBackground;
            _shadowSurface.Background = opaqueBackground;
            CornerRadius radius = maximized ? new CornerRadius(0) : new CornerRadius(ChromeCornerRadius);
            _shadowSurface.Margin = new Thickness(inset);
            _shadowSurface.CornerRadius = radius;
            _shadowSurface.BoxShadow = composited && inset > 0 && !_closeAnimationStarted
                ? new BoxShadows(new BoxShadow { Blur = 16, OffsetY = 4, Color = Color.FromArgb(64, 0, 0, 0) }) : default;
            _chromeSurface.Margin = new Thickness(inset);
            _chromeSurface.CornerRadius = radius;
            _chromeSurface.Clip = new RectangleGeometry
            {
                Rect = new Rect(0, 0, Math.Max(0, _root.Bounds.Width - inset * 2), Math.Max(0, _root.Bounds.Height - inset * 2)),
                RadiusX = maximized ? 0 : ChromeCornerRadius,
                RadiusY = maximized ? 0 : ChromeCornerRadius,
            };
            _windowActions.Margin = new Thickness(0, 0, 12, 0);
            _ = AvaloniaWindowsFrame.SuppressBorder(this);
            ApplyNativeShape();
            // Avalonia reapplies its native frame after WindowState notifications return.
            // Restore our decoration policy after that platform transition has completed.
            if (!_nativeFrameUpdateQueued)
            {
                _nativeFrameUpdateQueued = true;
                Dispatcher.UIThread.Post(() =>
                {
                    _nativeFrameUpdateQueued = false;
                    if (_disposed) return;
                    _ = AvaloniaWindowsFrame.SuppressBorder(this);
                    ApplyNativeShape();
                }, DispatcherPriority.Render);
            }
        }
        finally { _updatingChrome = false; }
    }

    private void ApplyNativeShape()
    {
        if (UsesCompositedEdges) { AvaloniaWindowsFrame.ClearShape(this); return; }
        AvaloniaWindowsFrame.ApplyCornerRadius(
        this,
        ChromeCornerRadius,
        _revealMask?.RadiusX,
        _revealMask is null ? null : _closeIcon is null ? 0 : CloseIconSize * .56
            * (_closeAnimationStarted ? _closeIconScale?.ScaleX ?? 0 : _startupIconScale?.ScaleX ?? 1));
    }

    private void OnMaximizeRequested(object? sender, EventArgs e) => ToggleMaximized();

    private void ToggleMaximized() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnSceneCommitted(object? sender, AvaloniaUiSceneCommittedEventArgs e)
    {

        if (_awaitingFirstSceneCommit && !_disposed)
        {
            _awaitingFirstSceneCommit = false;
            RunStartupReveal();
        }
    }

    private void OnTitleBarDragRequested(object? sender, PointerPressedEventArgs e)
    {
        if (OperatingSystem.IsMacOS())
        {
            if (e.ClickCount == 1) BeginMoveDrag(e);
            return; // The native title-bar role owns the system double-click preference.
        }
        if (e.ClickCount == 2)
        {
            ToggleMaximized();
            e.Handled = true;
            return;
        }

        BeginMoveDrag(e);
    }
}
