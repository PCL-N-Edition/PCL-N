// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace PCL.Desktop.Platform;

/// <summary>
/// Custom traffic-light caption buttons for borderless macOS windows.
/// Close / minimize / zoom mirror system placement and hover glyph reveal.
/// </summary>
internal sealed class MacOsTrafficLights : StackPanel
{
    private static readonly IBrush CloseFill = SolidColorBrush.Parse("#FF5F57");
    private static readonly IBrush MinimizeFill = SolidColorBrush.Parse("#FEBC2E");
    private static readonly IBrush ZoomFill = SolidColorBrush.Parse("#28C840");
    private static readonly IBrush InactiveFill = SolidColorBrush.Parse("#D0D0D0");

    private readonly TrafficLightButton _close;
    private readonly TrafficLightButton _minimize;
    private readonly TrafficLightButton _zoom;
    private bool _hovered;
    private bool _active = true;

    public event EventHandler? CloseRequested;
    public event EventHandler? MinimizeRequested;
    public event EventHandler? ZoomRequested;
    public event EventHandler? FullScreenRequested;

    public MacOsTrafficLights()
    {
        Orientation = Orientation.Horizontal;
        Spacing = 8;
        VerticalAlignment = VerticalAlignment.Center;
        HorizontalAlignment = HorizontalAlignment.Left;
        Margin = new Thickness(14, 0, 0, 0);
        Height = 28;
        IsHitTestVisible = true;

        _close = CreateButton(CloseFill, "×", () => CloseRequested?.Invoke(this, EventArgs.Empty));
        _minimize = CreateButton(MinimizeFill, "−", () => MinimizeRequested?.Invoke(this, EventArgs.Empty));
        _zoom = CreateButton(ZoomFill, "+", OnZoomClicked);

        Children.Add(_close);
        Children.Add(_minimize);
        Children.Add(_zoom);

        PointerEntered += (_, _) => SetHovered(true);
        PointerExited += (_, _) => SetHovered(false);
    }

    public void SetWindowActive(bool active)
    {
        _active = active;
        ApplyVisualState();
    }

    private void OnZoomClicked() => ZoomRequested?.Invoke(this, EventArgs.Empty);

    private TrafficLightButton CreateButton(IBrush fill, string glyph, Action click)
    {
        TrafficLightButton button = new(fill, glyph);
        button.Click += (_, _) => click();
        // Green traffic light: Option/Alt+click requests full screen (classic macOS).
        button.FullScreenClick += (_, _) => FullScreenRequested?.Invoke(this, EventArgs.Empty);
        return button;
    }

    private void SetHovered(bool hovered)
    {
        _hovered = hovered;
        ApplyVisualState();
    }

    private void ApplyVisualState()
    {
        _close.SetState(_active, _hovered);
        _minimize.SetState(_active, _hovered);
        _zoom.SetState(_active, _hovered);
    }

    private sealed class TrafficLightButton : Panel
    {
        private readonly Ellipse _disc;
        private readonly TextBlock _glyph;
        private readonly IBrush _activeFill;
        private readonly string _glyphText;

        public event EventHandler? Click;
        public event EventHandler? FullScreenClick;

        public TrafficLightButton(IBrush activeFill, string glyphText)
        {
            _activeFill = activeFill;
            _glyphText = glyphText;
            Width = 14;
            Height = 14;
            Cursor = new Cursor(StandardCursorType.Arrow);

            _disc = new Ellipse
            {
                Width = 12,
                Height = 12,
                Fill = activeFill,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            _glyph = new TextBlock
            {
                Text = glyphText,
                FontSize = 9,
                FontWeight = FontWeight.Bold,
                Foreground = new SolidColorBrush(Color.FromArgb(180, 40, 40, 40)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0,
                IsHitTestVisible = false,
                Margin = new Thickness(0, -0.5, 0, 0)
            };

            Children.Add(_disc);
            Children.Add(_glyph);

            PointerPressed += OnPointerPressed;
        }

        public void SetState(bool windowActive, bool chromeHovered)
        {
            _disc.Fill = windowActive ? _activeFill : InactiveFill;
            _glyph.Opacity = chromeHovered && windowActive ? 1 : 0;
            // Slightly larger hit target while hovered.
            Width = chromeHovered ? 16 : 14;
            Height = chromeHovered ? 16 : 14;
            _disc.Width = chromeHovered ? 13 : 12;
            _disc.Height = chromeHovered ? 13 : 12;
        }

        private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                return;

            // Green button: Option/Alt requests full screen.
            if (_glyphText == "+" && e.KeyModifiers.HasFlag(KeyModifiers.Alt))
            {
                FullScreenClick?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
                return;
            }

            Click?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
    }
}
