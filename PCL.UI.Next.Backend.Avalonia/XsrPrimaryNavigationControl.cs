using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using PCL.UI.Next;
using PCL.Xsr;

namespace PCL.UI.Next.Backend.Avalonia;

/// <summary>
/// Native primary-navigation control backed by the UI.Next shell navigation entities. It keeps
/// selection and accessibility behavior in the shared shell model.
/// </summary>
public sealed class XsrPrimaryNavigationControl : Border, IDisposable
{
    private readonly XsrUiShell _shell;
    private readonly TextBlock _caption;
    private readonly Dictionary<XsrSemanticId, Button> _buttons = [];
    private bool _disposed;

    public XsrPrimaryNavigationControl(XsrUiShell shell)
    {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        Padding = new Thickness(12, 14, 12, 14);
        StackPanel content = new() { Spacing = 10 };
        _caption = new TextBlock
        {
            Text = "主导航",
            Margin = new Thickness(10, 0, 10, 2),
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
        };
        StackPanel navigation = new() { Spacing = 6 };
        content.Children.Add(_caption);
        content.Children.Add(navigation);
        Child = content;

        foreach (XsrUiShellNavigationItem item in shell.NavigationItems)
        {
            AddButton(item, navigation);
        }

        _shell.NavigationChanged += OnNavigationChanged;
        _shell.StyleChanged += OnShellStyleChanged;
        ApplyPalette();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _shell.NavigationChanged -= OnNavigationChanged;
        _shell.StyleChanged -= OnShellStyleChanged;
        GC.SuppressFinalize(this);
    }

    private void AddButton(XsrUiShellNavigationItem item, StackPanel navigation)
    {
        StackPanel content = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        content.Children.Add(new TextBlock
        {
            Text = item.Icon,
            FontSize = 17,
            Width = 20,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
        content.Children.Add(new TextBlock
        {
            Text = item.Label,
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
        });

        Button button = new()
        {
            Content = content,
            Height = 44,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(12, 0),
            Tag = item.Id,
        };
        AutomationProperties.SetName(button, item.Label);
        button.Click += (_, _) => _shell.Select(item.Id);
        button.PointerEntered += (_, _) =>
        {
            if (_shell.SelectedNavigationId != item.Id)
            {
                button.Background = Brush(_shell.Palette.ActiveNavigationBackground with
                {
                    Alpha = (byte)Math.Min(255, _shell.Palette.ActiveNavigationBackground.Alpha + 28),
                });
            }
        };
        button.PointerExited += (_, _) => ApplyButtonPalette(item.Id, button);
        _buttons.Add(item.Id, button);
        navigation.Children.Add(button);
    }

    private void ApplyPalette()
    {
        XsrUiShellPalette palette = _shell.Palette;
        Background = Brush(palette.NavigationBackground);
        BorderBrush = Brush(palette.SurfaceBorder);
        BorderThickness = new Thickness(0, 0, palette.BorderWidth, 0);
        _caption.Foreground = Brush(palette.SecondaryText);
        foreach (XsrUiShellNavigationItem item in _shell.NavigationItems)
        {
            ApplyButtonPalette(item.Id, _buttons[item.Id]);
        }
    }

    private void ApplyButtonPalette(XsrSemanticId id, Button button)
    {
        XsrUiShellPalette palette = _shell.Palette;
        bool selected = _shell.SelectedNavigationId == id;
        button.Background = Brush(selected ? palette.ActiveNavigationBackground : XsrUiColor.Transparent);
        button.Foreground = Brush(selected ? palette.ActiveNavigationText : palette.PrimaryText);
        button.BorderBrush = Brush(selected ? palette.Accent : XsrUiColor.Transparent);
        button.BorderThickness = new Thickness(0, 0, selected ? palette.BorderWidth : 0, 0);
        button.CornerRadius = new CornerRadius(selected ? palette.CornerRadius : 0);
    }

    private void OnNavigationChanged(object? sender, XsrUiShellNavigationChangedEventArgs e)
    {
        foreach (XsrUiShellNavigationItem item in _shell.NavigationItems)
        {
            ApplyButtonPalette(item.Id, _buttons[item.Id]);
        }
    }

    private void OnShellStyleChanged(object? sender, EventArgs e) => ApplyPalette();

    private static SolidColorBrush Brush(XsrUiColor color) =>
        new(Color.FromArgb(color.Alpha, color.Red, color.Green, color.Blue));
}
