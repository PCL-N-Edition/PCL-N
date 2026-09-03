using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace PCL.UI.Next.Backend.Avalonia;

/// <summary>
/// Draws one embedded registry icon (see <see cref="AvaloniaUiIcons"/>) scaled uniformly into
/// its bounds and stroked with the tint color. Used by native window chrome; the scene surface
/// draws scene icons with the same registry.
/// </summary>
internal sealed class AvaloniaUiSvgIcon : Control
{
    public static readonly StyledProperty<string?> SourceProperty =
        AvaloniaProperty.Register<AvaloniaUiSvgIcon, string?>(nameof(Source));

    private IBrush _tint = Brushes.White;

    public string? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    internal IBrush Tint
    {
        get => _tint;
        set
        {
            _tint = value;
            InvalidateVisual();
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == SourceProperty)
        {
            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Source is not { Length: > 0 } source
            || !AvaloniaUiIcons.TryGetGeometry(source, out IReadOnlyList<Geometry> paths)
            || Bounds.Width <= 0
            || Bounds.Height <= 0)
        {
            return;
        }

        double scale = Math.Min(Bounds.Width, Bounds.Height) / AvaloniaUiIcons.ViewBoxSize;
        double offsetX = (Bounds.Width - (AvaloniaUiIcons.ViewBoxSize * scale)) / 2;
        double offsetY = (Bounds.Height - (AvaloniaUiIcons.ViewBoxSize * scale)) / 2;
        Pen pen = new(_tint, AvaloniaUiIcons.StrokeWidth * scale)
        {
            LineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        using (context.PushTransform(Matrix.CreateTranslation(offsetX, offsetY) * Matrix.CreateScale(scale, scale)))
        {
            foreach (Geometry path in paths)
            {
                context.DrawGeometry(null, pen, path);
            }
        }
    }
}
