using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using PCL.UI.Next;

namespace PCL.UI.Next.Backend.Avalonia;

/// <summary>
/// Native title-bar control for the XSR shell. It owns visual chrome and raises window actions;
/// the containing Window decides how those actions affect its platform lifetime.
/// </summary>
public sealed class XsrTitleBarControl : Border, IDisposable
{
    private readonly XsrUiShell _shell;
    private readonly TextBlock _title;
    private readonly TextBlock _version;
    private readonly Button _styleButton;
    private readonly Button _maximizeButton;
    private bool _disposed;

    public XsrTitleBarControl(XsrUiShell shell)
    {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        Padding = new Thickness(20, 0, 14, 0);
        PointerPressed += OnPointerPressed;

        Grid layout = new()
        {
            ColumnDefinitions = new ColumnDefinitions
            {
                new ColumnDefinition(1, GridUnitType.Star),
                new ColumnDefinition(GridLength.Auto),
            },
            VerticalAlignment = VerticalAlignment.Stretch,
        };

        StackPanel identity = new()
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 10,
        };
        _title = new TextBlock
        {
            Text = shell.Title,
            FontSize = 17,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _version = new TextBlock
        {
            Text = shell.Version,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
        };
        identity.Children.Add(_title);
        identity.Children.Add(_version);
        Grid.SetColumn(identity, 0);
        layout.Children.Add(identity);

        StackPanel actions = new()
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 4,
        };
        _styleButton = CreateButton("实验", "切换 UI 风格");
        _styleButton.Click += OnStyleButtonClick;
        Button minimizeButton = CreateButton("—", "最小化窗口");
        minimizeButton.Click += (_, _) => MinimizeRequested?.Invoke(this, EventArgs.Empty);
        _maximizeButton = CreateButton("□", "最大化窗口");
        _maximizeButton.Click += (_, _) => MaximizeRequested?.Invoke(this, EventArgs.Empty);
        Button closeButton = CreateButton("×", "关闭窗口");
        closeButton.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        actions.Children.Add(_styleButton);
        actions.Children.Add(minimizeButton);
        actions.Children.Add(_maximizeButton);
        actions.Children.Add(closeButton);
        Grid.SetColumn(actions, 1);
        layout.Children.Add(actions);
        Child = layout;

        _shell.StyleChanged += OnShellStyleChanged;
        ApplyPalette();
    }

    public event EventHandler? MinimizeRequested;

    public event EventHandler? MaximizeRequested;

    public event EventHandler? CloseRequested;

    public event EventHandler<PointerPressedEventArgs>? DragRequested;

    public void SetMaximized(bool maximized) => _maximizeButton.Content = maximized ? "❐" : "□";

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _shell.StyleChanged -= OnShellStyleChanged;
        GC.SuppressFinalize(this);
    }

    private Button CreateButton(string content, string automationName)
    {
        Button button = new()
        {
            Content = content,
            Width = 34,
            Height = 32,
            Padding = new Thickness(0),
            FontSize = 14,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
        };
        AutomationProperties.SetName(button, automationName);
        button.PointerEntered += (_, _) => button.Background = Brush(_shell.Palette.ActiveNavigationBackground);
        button.PointerExited += (_, _) => button.Background = Brushes.Transparent;
        return button;
    }

    private void ApplyPalette()
    {
        XsrUiShellPalette palette = _shell.Palette;
        Background = Brush(palette.TitleBarBackground);
        BorderBrush = Brush(palette.SurfaceBorder);
        BorderThickness = new Thickness(0, 0, 0, palette.BorderWidth);
        CornerRadius = new CornerRadius(palette.CornerRadius, palette.CornerRadius, 0, 0);
        _title.Foreground = Brush(palette.PrimaryText);
        _version.Foreground = Brush(palette.SecondaryText);
        _styleButton.Content = _shell.Style == XsrUiShellStyle.LiquidGlass ? "实验" : "玻璃";
        _styleButton.Foreground = Brush(palette.PrimaryText);
        _maximizeButton.Foreground = Brush(palette.PrimaryText);
    }

    private void OnShellStyleChanged(object? sender, EventArgs e) => ApplyPalette();

    private void OnStyleButtonClick(object? sender, RoutedEventArgs e)
    {
        _shell.SetStyle(
            _shell.Style == XsrUiShellStyle.Experimental
                ? XsrUiShellStyle.LiquidGlass
                : XsrUiShellStyle.Experimental);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Button)
        {
            return;
        }

        if (e.GetCurrentPoint(this).Properties.PointerUpdateKind == PointerUpdateKind.LeftButtonPressed)
        {
            DragRequested?.Invoke(this, e);
            e.Handled = true;
        }
    }

    private static SolidColorBrush Brush(XsrUiColor color) =>
        new(Color.FromArgb(color.Alpha, color.Red, color.Green, color.Blue));
}
