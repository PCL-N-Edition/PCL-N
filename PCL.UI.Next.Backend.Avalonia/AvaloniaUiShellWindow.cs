using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using PCL.UI.Next;
using PCL.Xsr;

namespace PCL.UI.Next.Backend.Avalonia;

/// <summary>
/// Avalonia window for the Wave 7 product shell. Structure comes from PXML/UI.Next; the custom
/// title-bar and navigation controls below are the platform presentation edge.
/// </summary>
public sealed class AvaloniaUiShellWindow : Window
{
    private readonly XsrUiShell _shell;
    private readonly Grid _rootGrid;
    private readonly XsrTitleBarControl _titleBar;
    private readonly XsrPrimaryNavigationControl _navigation;
    private readonly Border _contentSurface;
    private readonly TextBlock _contentTitle;
    private readonly TextBlock _contentDescription;

    public AvaloniaUiShellWindow(XsrUiShell shell)
    {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        Title = shell.Title;
        Width = 1280;
        Height = 800;
        MinWidth = 960;
        MinHeight = 620;
        CanResize = true;
        ShowInTaskbar = true;
        WindowDecorations = WindowDecorations.None;
        ExtendClientAreaToDecorationsHint = true;

        _rootGrid = new Grid
        {
            RowDefinitions = new RowDefinitions
            {
                new RowDefinition(58, GridUnitType.Pixel),
                new RowDefinition(1, GridUnitType.Star),
            },
            ColumnDefinitions = new ColumnDefinitions
            {
                new ColumnDefinition(236, GridUnitType.Pixel),
                new ColumnDefinition(1, GridUnitType.Star),
            },
            ClipToBounds = true,
        };

        _titleBar = new XsrTitleBarControl(shell);
        _titleBar.DragRequested += OnTitleBarDragRequested;
        _titleBar.MinimizeRequested += (_, _) => WindowState = WindowState.Minimized;
        _titleBar.MaximizeRequested += OnMaximizeRequested;
        _titleBar.CloseRequested += (_, _) => Close();
        _navigation = new XsrPrimaryNavigationControl(shell);
        _contentSurface = BuildContent(out _contentTitle, out _contentDescription);

        Grid.SetRow(_titleBar, 0);
        Grid.SetColumn(_titleBar, 0);
        Grid.SetColumnSpan(_titleBar, 2);
        Grid.SetRow(_navigation, 1);
        Grid.SetColumn(_navigation, 0);
        Grid.SetRow(_contentSurface, 1);
        Grid.SetColumn(_contentSurface, 1);
        _rootGrid.Children.Add(_titleBar);
        _rootGrid.Children.Add(_navigation);
        _rootGrid.Children.Add(_contentSurface);
        Content = _rootGrid;

        _shell.NavigationChanged += OnNavigationChanged;
        _shell.StyleChanged += OnStyleChanged;
        ApplyPalette();
        UpdateContent(_shell.SelectedNavigationId);
    }

    protected override void OnClosed(EventArgs e)
    {
        _shell.NavigationChanged -= OnNavigationChanged;
        _shell.StyleChanged -= OnStyleChanged;
        _titleBar.DragRequested -= OnTitleBarDragRequested;
        _titleBar.Dispose();
        _navigation.Dispose();
        base.OnClosed(e);
    }

    private static Border BuildContent(out TextBlock title, out TextBlock description)
    {
        Border border = new() { Padding = new Thickness(28, 24, 28, 24) };
        StackPanel content = new() { Spacing = 12 };
        title = new TextBlock { FontSize = 28, FontWeight = FontWeight.SemiBold };
        description = new TextBlock
        {
            Text = "UI.Next / PXML 内容宿主已就绪。",
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
        };
        Border placeholder = new()
        {
            Padding = new Thickness(18),
            Child = new TextBlock
            {
                Text = "产品页面将在后续 Wave 7 vertical slice 中挂载到这里。",
                TextWrapping = TextWrapping.Wrap,
            },
        };
        content.Children.Add(title);
        content.Children.Add(description);
        content.Children.Add(placeholder);
        border.Child = content;
        return border;
    }

    private void ApplyPalette()
    {
        XsrUiShellPalette palette = _shell.Palette;
        _rootGrid.Background = Brush(palette.WindowBackground);
        _contentSurface.Background = Brush(palette.ContentBackground);
        _contentTitle.Foreground = Brush(palette.PrimaryText);
        _contentDescription.Foreground = Brush(palette.SecondaryText);
        ApplyTransparencyHint();
    }

    private void UpdateContent(XsrSemanticId id)
    {
        XsrUiShellNavigationItem? item = _shell.NavigationItems.FirstOrDefault(candidate => candidate.Id == id);
        if (item is null)
        {
            return;
        }

        _contentTitle.Text = item.Label;
        _contentDescription.Text =
            $"{item.Label} vertical slice · {(_shell.Style == XsrUiShellStyle.LiquidGlass ? "LiquidGlass" : "Experimental")} shell";
    }

    private void ApplyTransparencyHint()
    {
        TransparencyLevelHint = _shell.Style == XsrUiShellStyle.LiquidGlass
            ? [WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Transparent]
            : [WindowTransparencyLevel.None];
    }

    private void OnNavigationChanged(object? sender, XsrUiShellNavigationChangedEventArgs e) => UpdateContent(e.Current);

    private void OnStyleChanged(object? sender, EventArgs e)
    {
        ApplyPalette();
        UpdateContent(_shell.SelectedNavigationId);
    }

    private void OnMaximizeRequested(object? sender, EventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
        _titleBar.SetMaximized(WindowState == WindowState.Maximized);
    }

    private void OnTitleBarDragRequested(object? sender, PointerPressedEventArgs e)
    {
        BeginMoveDrag(e);
    }

    private static SolidColorBrush Brush(XsrUiColor color) =>
        new(Color.FromArgb(color.Alpha, color.Red, color.Green, color.Blue));
}
