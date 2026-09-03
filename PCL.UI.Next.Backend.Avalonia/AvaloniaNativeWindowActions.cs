using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using PCL.UI.Next;

namespace PCL.UI.Next.Backend.Avalonia;

/// <summary>
/// Native title-bar actions. These are platform window controls rather than PXML product
/// entities: their lifetime and effects stay at the backend edge, while their palette follows
/// the UI.Next scene style.
/// </summary>
internal sealed class AvaloniaNativeWindowActions : StackPanel, IDisposable
{
    private readonly AvaloniaUiSceneSurface _surface;
    private readonly Button _maximize;
    private XsrUiColor _hover;
    private bool _disposed;

    public AvaloniaNativeWindowActions(AvaloniaUiSceneSurface surface)
    {
        _surface = surface ?? throw new ArgumentNullException(nameof(surface));
        Orientation = Orientation.Horizontal;
        Height = 58;
        VerticalAlignment = VerticalAlignment.Top;
        Spacing = 2;
        Margin = new Thickness(0, 0, 10, 0);

        Button minimize = CreateButton("—", "最小化窗口");
        minimize.Click += (_, _) => MinimizeRequested?.Invoke(this, EventArgs.Empty);
        _maximize = CreateButton("□", "最大化窗口");
        _maximize.Click += (_, _) => MaximizeRequested?.Invoke(this, EventArgs.Empty);
        Button close = CreateButton("×", "关闭窗口");
        close.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        Children.Add(minimize);
        Children.Add(_maximize);
        Children.Add(close);
        foreach (Button button in Children.OfType<Button>())
        {
            button.PointerEntered += OnButtonPointerEntered;
            button.PointerExited += OnButtonPointerExited;
        }

        _surface.SceneCommitted += OnSceneCommitted;
    }

    public event EventHandler? MinimizeRequested;

    public event EventHandler? MaximizeRequested;

    public event EventHandler? CloseRequested;

    public void SetMaximized(bool maximized) => _maximize.Content = maximized ? "❐" : "□";

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

    private static Button CreateButton(string content, string automationName)
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
        return button;
    }

    private void OnSceneCommitted(object? sender, AvaloniaUiSceneCommittedEventArgs e)
    {
        XsrUiVisualStyleSnapshot title = e.Scene.Nodes
            .FirstOrDefault(node => node.Role == XsrUiSemanticRole.TitleBar)
            .VisualStyle;
        XsrUiVisualStyleSnapshot selected = e.Scene.Nodes
            .FirstOrDefault(node => node.IsSelected)
            .VisualStyle;
        ApplyPalette(title.Foreground, selected.Background.Alpha > 0 ? selected.Background : title.Background);
    }

    private void ApplyPalette(XsrUiColor foreground, XsrUiColor hover)
    {
        _hover = hover;
        foreach (Button button in Children.OfType<Button>())
        {
            button.Foreground = Brush(foreground);
        }
    }

    private void OnButtonPointerEntered(object? sender, global::Avalonia.Input.PointerEventArgs e)
    {
        if (sender is Button button)
        {
            button.Background = Brush(_hover);
        }
    }

    private static void OnButtonPointerExited(object? sender, global::Avalonia.Input.PointerEventArgs e)
    {
        if (sender is Button button)
        {
            button.Background = Brushes.Transparent;
        }
    }

    private static SolidColorBrush Brush(XsrUiColor color) =>
        new(Color.FromArgb(color.Alpha, color.Red, color.Green, color.Blue));
}
