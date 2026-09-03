using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using PCL.UI.Next;

namespace PCL.UI.Next.Backend.Avalonia;

/// <summary>
/// Native title-bar actions. These are platform window controls rather than PXML product
/// entities: their lifetime and effects stay at the backend edge, while their palette follows
/// the UI.Next scene. Feedback follows the fluid-interface rules: the hover circle fades in on
/// entry, the press scales down on pointer-down, and every change starts from the presented
/// value.
/// </summary>
internal sealed class AvaloniaNativeWindowActions : StackPanel, IDisposable
{
    private const double ButtonSize = 28;
    private const double IconSize = 16;

    private readonly AvaloniaUiSceneSurface _surface;
    private readonly AvaloniaUiSvgIcon _maximizeIcon;
    private readonly List<WindowActionButton> _buttons = [];
    private XsrUiColor _foreground = XsrUiColor.FromRgb(255, 255, 255);
    private bool _disposed;

    public AvaloniaNativeWindowActions(AvaloniaUiSceneSurface surface)
    {
        _surface = surface ?? throw new ArgumentNullException(nameof(surface));
        Orientation = Orientation.Horizontal;
        Height = XsrUiShell.TitleBarHeight;
        VerticalAlignment = VerticalAlignment.Top;
        Spacing = 4;
        Margin = new Thickness(0, (XsrUiShell.TitleBarHeight - ButtonSize) / 2, 12, 0);

        WindowActionButton minimize = CreateButton("lucide/minus", "最小化窗口");
        minimize.Click += (_, _) => MinimizeRequested?.Invoke(this, EventArgs.Empty);
        WindowActionButton maximize = CreateButton("lucide/square", "最大化窗口");
        maximize.Click += (_, _) => MaximizeRequested?.Invoke(this, EventArgs.Empty);
        WindowActionButton close = CreateButton("lucide/x", "关闭窗口");
        close.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        _maximizeIcon = maximize.Icon;
        Children.Add(minimize);
        Children.Add(maximize);
        Children.Add(close);
        _buttons.AddRange([minimize, maximize, close]);

        _surface.SceneCommitted += OnSceneCommitted;
    }

    public event EventHandler? MinimizeRequested;

    public event EventHandler? MaximizeRequested;

    public event EventHandler? CloseRequested;

    public void SetMaximized(bool maximized) => _maximizeIcon.Source = maximized ? "pcl/window-restore" : "lucide/square";

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _surface.SceneCommitted -= OnSceneCommitted;
        GC.SuppressFinalize(this);
    }

    private static WindowActionButton CreateButton(string icon, string automationName) =>
        new(icon, automationName);

    private void OnSceneCommitted(object? sender, AvaloniaUiSceneCommittedEventArgs e)
    {
        XsrUiVisualStyleSnapshot title = e.Scene.Nodes
            .FirstOrDefault(node => node.Role == XsrUiSemanticRole.TitleBar)
            .VisualStyle;
        if (title.Foreground == _foreground)
        {
            return;
        }

        _foreground = title.Foreground;
        foreach (WindowActionButton button in _buttons)
        {
            button.ApplyForeground(_foreground);
        }
    }

    /// <summary>
    /// One circular window action: a translucent circle fades in behind the vector icon on hover
    /// and the whole button scales down on press.
    /// </summary>
    private sealed class WindowActionButton : Button
    {
        internal WindowActionButton(string icon, string automationName)
        {
            Width = ButtonSize;
            Height = ButtonSize;
            Padding = new Thickness(0);
            Background = Brushes.Transparent;
            BorderBrush = Brushes.Transparent;
            BorderThickness = new Thickness(0);
            Focusable = false;

            AutomationProperties.SetName(this, automationName);

            _hoverCircle = new Border
            {
                Width = ButtonSize,
                Height = ButtonSize,
                CornerRadius = new CornerRadius(ButtonSize / 2),
                Opacity = 0,
            };
            _icon = new AvaloniaUiSvgIcon
            {
                Source = icon,
                Width = IconSize,
                Height = IconSize,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Content = new Panel { Children = { _hoverCircle, _icon } };

            _press = new ScaleTransform(1, 1);
            RenderTransform = _press;
            RenderTransformOrigin = RelativePoint.Center;

            PointerEntered += (_, _) => AnimateHover(1);
            PointerExited += (_, _) => AnimateHover(0);
            PointerPressed += (_, e) =>
            {
                if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                {
                    SetPressScale(AvaloniaMotionTokens.PressScale);
                }
            };
            PointerReleased += (_, e) =>
            {
                if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                {
                    SetPressScale(1);
                }
            };
        }

        private readonly Border _hoverCircle;
        private readonly AvaloniaUiSvgIcon _icon;
        private readonly ScaleTransform _press;
        private double _hoverTarget;

        internal AvaloniaUiSvgIcon Icon => _icon;

        internal void ApplyForeground(XsrUiColor foreground)
        {
            _icon.Tint = new SolidColorBrush(Color.FromArgb(foreground.Alpha, foreground.Red, foreground.Green, foreground.Blue));
            // The hover circle reads as a light catch on the title bar material, derived from
            // the title text color instead of a hard-coded palette entry.
            _hoverCircle.Background = new SolidColorBrush(Color.FromArgb(
                Math.Min(foreground.Alpha, (byte)50),
                foreground.Red,
                foreground.Green,
                foreground.Blue));
        }

        private void AnimateHover(double target)
        {
            if (_hoverTarget == target)
            {
                return;
            }

            _hoverTarget = target;
            AvaloniaUiMotion.Animate(
                this,
                "hover",
                () => _hoverCircle.Opacity,
                value => _hoverCircle.Opacity = value,
                target,
                target > 0
                    ? AvaloniaMotionTokens.HoverMilliseconds
                    : AvaloniaMotionTokens.HoverOutMilliseconds);
        }

        private void SetPressScale(double scale)
        {
            AvaloniaUiMotion.Animate(
                this, "scale-x", () => _press.ScaleX, value => _press.ScaleX = value,
                scale, AvaloniaMotionTokens.PressMilliseconds);
            AvaloniaUiMotion.Animate(
                this, "scale-y", () => _press.ScaleY, value => _press.ScaleY = value,
                scale, AvaloniaMotionTokens.PressMilliseconds);
        }
    }
}
