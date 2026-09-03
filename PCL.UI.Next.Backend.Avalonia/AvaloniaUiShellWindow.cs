using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using PCL.UI.Next;

namespace PCL.UI.Next.Backend.Avalonia;

/// <summary>
/// Native window lifetime around one UI.Next scene surface. The window owns the frameless
/// chrome: a transparent window whose rounded, shadowed surface hosts the scene, a drag-through
/// title bar with double-click maximize, eight invisible resize grips, and the sole native
/// overlay (window actions). Startup and close run the dedicated icon-circle animation: the
/// window reveals from the small circle behind the inherited splash icon, and closing collapses
/// it back into that circle. Product geometry is committed by
/// <see cref="AvaloniaUiSceneSurface"/> from the immutable renderer scene.
/// </summary>
public sealed class AvaloniaUiShellWindow : Window
{
    private const double ChromeMargin = 10;
    private const double ChromeCornerRadius = 8;
    private const double CloseIconSize = 112;

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
            // The same product icon closes the loop: taskbar icon at rest, and the image the
            // window collapses into on close.
            _closeIcon = new Bitmap(iconStream);
            Icon = new WindowIcon(_closeIcon);
        }

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
        chrome.Children.Add(_windowActions);
        _chromeSurface.Child = chrome;

        // Everything the circular mask may clip lives in this subtree; the product icon is a
        // sibling above it so the reveal can collapse to (or expand from) radius zero while
        // the icon stays fully visible.
        _maskedContent = new Grid();
        _maskedContent.Children.Add(_shadowSurface);
        _maskedContent.Children.Add(_chromeSurface);
        foreach (Border grip in CreateResizeGrips())
        {
            _maskedContent.Children.Add(grip);
        }

        _root = new Grid();
        _root.Children.Add(_maskedContent);

        Content = _root;
        PropertyChanged += OnWindowPropertyChanged;
    }

    /// <summary>
    /// Raised once the startup reveal has fully expanded (or was skipped under reduced motion).
    /// The host dismisses the splash at this point, so the icon never leaves the screen until
    /// the window has taken over.
    /// </summary>
    public event EventHandler? StartupRevealCompleted;

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (_shell.Renderer.ReducedMotion)
        {
            StartupRevealCompleted?.Invoke(this, EventArgs.Empty);
            return;
        }

        // The reveal must expand over rendered content, so it starts at the surface's first
        // committed scene rather than at Opened — otherwise the mask grows across a blank
        // window and the product UI simply pops in when the first frame lands.
        _awaitingFirstSceneCommit = true;
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
        double width = Bounds.Width;
        double height = Bounds.Height;
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

                // Mutating the clip geometry alone does not invalidate the visual tree; the
                // mask would otherwise apply only for the first frame and never redraw.
                _maskedContent.InvalidateVisual();
            },
            fullRadius,
            AvaloniaMotionTokens.StartupRevealMilliseconds,
            AvaloniaUiMotion.EaseOut,
            completed: OnStartupRevealCompleted);
    }

    private void OnStartupRevealCompleted()
    {
        if (_disposed)
        {
            return;
        }

        _maskedContent.Clip = null;
        _revealMask = null;
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
                },
                1.12,
                AvaloniaMotionTokens.IconBounceMilliseconds,
                AvaloniaUiMotion.EaseOut,
                completed: () => AvaloniaUiMotion.Animate(
                    this, ("startup-icon", "down"), () => scale.ScaleX, value =>
                    {
                        scale.ScaleX = value;
                        scale.ScaleY = value;
                    },
                    0,
                    AvaloniaMotionTokens.IconCollapseMilliseconds,
                    AvaloniaUiMotion.EaseIn,
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
        double width = Bounds.Width;
        double height = Bounds.Height;
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
            RadiusX = fullRadius,
            RadiusY = fullRadius,
        };
        _revealMask = mask;
        _maskedContent.Clip = mask;

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
                    }
                },
                0,
                _closeIconScale is null ? 1 : AvaloniaMotionTokens.IconCollapseMilliseconds,
                AvaloniaUiMotion.EaseIn,
                completed: () =>
                {
                    _maskedContent.Clip = null;
                    _revealMask = null;
                    Close();
                });
        }

        AvaloniaUiMotion.Animate(
            this,
            "close-collapse",
            () => mask.RadiusX,
            value =>
            {
                mask.RadiusX = value;
                mask.RadiusY = value;
                _maskedContent.InvalidateVisual();
            },
            0,
            AvaloniaMotionTokens.CloseCollapseMilliseconds,
            AvaloniaUiMotion.EaseIn,
            completed: OnCollapsePieceCompleted);

        if (_closeIcon is not null)
        {
            _closeIconScale = new ScaleTransform(0, 0);
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
                },
                1.12,
                AvaloniaMotionTokens.CloseCollapseMilliseconds / 2,
                AvaloniaUiMotion.EaseOut,
                completed: () => AvaloniaUiMotion.Animate(
                    this, ("close-icon", "settle"), () => scale.ScaleX, value =>
                    {
                        scale.ScaleX = value;
                        scale.ScaleY = value;
                    },
                    1,
                    AvaloniaMotionTokens.CloseCollapseMilliseconds / 2,
                    AvaloniaUiMotion.EaseIn,
                    completed: OnCollapsePieceCompleted));
        }
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

    private void OnSceneCommitted(object? sender, AvaloniaUiSceneCommittedEventArgs e)
    {
        ApplyTransparencyHint(e.Scene);
        if (_awaitingFirstSceneCommit && !_disposed)
        {
            _awaitingFirstSceneCommit = false;
            RunStartupReveal();
        }
    }

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
