// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using PCL.Desktop.Theme;

namespace PCL.Desktop.Controls.Legacy;

public enum MyButtonColorType
{
    Normal,
    Highlight,
    Red,
    Gray
}

public partial class MyButton : Border
{
    public enum ColorState
    {
        Normal,
        Highlight,
        Red,
        Gray
    }

    private const int AnimationColorIn = 100;
    private const int AnimationColorOut = 200;

    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<MyButton, string>(nameof(Text), string.Empty);

    public static readonly StyledProperty<ColorState> ColorTypeProperty =
        AvaloniaProperty.Register<MyButton, ColorState>(nameof(ColorType));

    public static readonly StyledProperty<ICommand?> CommandProperty =
        AvaloniaProperty.Register<MyButton, ICommand?>(nameof(Command));

    public static readonly StyledProperty<object?> CommandParameterProperty =
        AvaloniaProperty.Register<MyButton, object?>(nameof(CommandParameter));

    public static readonly StyledProperty<object?> ToolTipProperty =
        AvaloniaProperty.Register<MyButton, object?>(nameof(ToolTip));

    public static readonly StyledProperty<bool> UseExperimentalStyleProperty =
        AvaloniaProperty.Register<MyButton, bool>(nameof(UseExperimentalStyle));

    public new static readonly StyledProperty<Thickness> PaddingProperty =
        AvaloniaProperty.Register<MyButton, Thickness>(nameof(Padding), new Thickness());

    public static readonly StyledProperty<Thickness> TextPaddingProperty =
        AvaloniaProperty.Register<MyButton, Thickness>(nameof(TextPadding), new Thickness());

    private readonly Border? _foregroundBorder;
    private readonly TextBlock? _label;
    private bool _isPressed;

    public MyButton()
    {
        AvaloniaXamlLoader.Load(this);
        _foregroundBorder = this.FindControl<Border>("PanFore");
        _label = this.FindControl<TextBlock>("LabText");
        Cursor = new Cursor(StandardCursorType.Hand);

        PointerEntered += (_, _) =>
        {
            RefreshColor();
            ButtonMouseEnter();
        };
        PointerExited += (_, _) =>
        {
            RefreshColor();
            ButtonMouseLeave();
        };
        PointerPressed += OnPointerPressed;
        PointerReleased += OnPointerReleased;
        this.GetObservable(TextProperty).Subscribe(text =>
        {
            if (_label is not null)
                _label.Text = text;
        });
        this.GetObservable(ColorTypeProperty).Subscribe(_ => RefreshColor());
        this.GetObservable(IsEnabledProperty).Subscribe(_ => RefreshColor());
        this.GetObservable(ToolTipProperty).Subscribe(tip => Avalonia.Controls.ToolTip.SetTip(this, tip));
        this.GetObservable(UseExperimentalStyleProperty).Subscribe(_ =>
        {
            ApplyVisualStyle();
            RefreshColor();
        });
        this.GetObservable(PaddingProperty).Subscribe(padding =>
        {
            if (_foregroundBorder is not null)
                _foregroundBorder.Padding = padding;
        });
        this.GetObservable(TextPaddingProperty).Subscribe(padding =>
        {
            if (_label is not null)
                _label.Padding = padding;
        });
        AttachedToVisualTree += (_, _) => RefreshColor();
        DetachedFromVisualTree += (_, _) => AvaloniaThemeManager.ThemeChanged -= OnThemeChanged;
        AvaloniaThemeManager.ThemeChanged += OnThemeChanged;
        RefreshColor();
    }

    private void OnThemeChanged()
    {
        // Theme brushes may be mutated in place; re-bind control state (hover/normal keys).
        if (Dispatcher.UIThread.CheckAccess())
            RefreshColor();
        else
            Dispatcher.UIThread.Post(RefreshColor, DispatcherPriority.Background);
    }

    public event EventHandler? Click;

    public event EventHandler<PointerReleasedEventArgs>? ClickReleased;

    public int Uuid { get; } = Random.Shared.Next();

    public InlineCollection Inlines =>
        _label?.Inlines ?? throw new InvalidOperationException("MyButton text block is not initialized.");

    public ITransform? RealRenderTransform
    {
        get => _foregroundBorder?.RenderTransform;
        set
        {
            if (_foregroundBorder is not null)
                _foregroundBorder.RenderTransform = value;
        }
    }

    public string Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public ColorState ColorType
    {
        get => GetValue(ColorTypeProperty);
        set => SetValue(ColorTypeProperty, value);
    }

    public ICommand? Command
    {
        get => GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    public object? CommandParameter
    {
        get => GetValue(CommandParameterProperty);
        set => SetValue(CommandParameterProperty, value);
    }

    public object? ToolTip
    {
        get => GetValue(ToolTipProperty);
        set => SetValue(ToolTipProperty, value);
    }

    /// <summary>Uses the experimental, Apple-inspired filled/tonal dialog button treatment.</summary>
    public bool UseExperimentalStyle
    {
        get => GetValue(UseExperimentalStyleProperty);
        set => SetValue(UseExperimentalStyleProperty, value);
    }

    public new Thickness Padding
    {
        get => GetValue(PaddingProperty);
        set => SetValue(PaddingProperty, value);
    }

    public Thickness TextPadding
    {
        get => GetValue(TextPaddingProperty);
        set => SetValue(TextPaddingProperty, value);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!IsEnabled || _foregroundBorder is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        _isPressed = true;
        Focus();
        // Instant press-down feedback; no long creep (Apple: respond on pointer-down).
        ModAnimation.AniStart(
            ModAnimation.AaScaleTransform(
                _foregroundBorder,
                MotionTokens.PressScale - GetForegroundScale(),
                MotionTokens.PressInMs,
                ease: new ModAnimation.AniEaseOutFluent(ModAnimation.AniEasePower.ExtraStrong)),
            "MyButton Scale " + Uuid);
        e.Handled = true;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isPressed || _foregroundBorder is null)
            return;

        _isPressed = false;
        var parameter = CommandParameter;
        if (Command?.CanExecute(parameter) == true)
            Command.Execute(parameter);
        ClickReleased?.Invoke(this, e);
        Click?.Invoke(this, EventArgs.Empty);
        ModAnimation.AniStart(
            ModAnimation.AaScaleTransform(
                _foregroundBorder,
                1d - GetForegroundScale(),
                MotionTokens.PressOutMs,
                ease: new ModAnimation.AniEaseOutFluent()),
            "MyButton Scale " + Uuid);
        e.Handled = true;
    }

    private void RefreshColor()
    {
        if (_foregroundBorder is null || _label is null)
            return;

        if (UseExperimentalStyle)
        {
            RefreshExperimentalColor();
            return;
        }

        string resourceKey = IsEnabled ? GetBorderBrushResourceKey() : "ColorBrushGray4";
        ControlVisualHelpers.AnimateColorOrSetResource(
            _foregroundBorder,
            Border.BorderBrushProperty,
            resourceKey,
            IsPointerOver ? AnimationColorIn : AnimationColorOut,
            "MyButton Color " + Uuid,
            ControlVisualHelpers.ShouldAnimate(this));
        ControlVisualHelpers.AnimateColorOrSetResource(
            _label,
            TextBlock.ForegroundProperty,
            resourceKey,
            IsPointerOver ? AnimationColorIn : AnimationColorOut,
            "MyButton TextColor " + Uuid,
            ControlVisualHelpers.ShouldAnimate(this));
        Cursor = IsEnabled ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
    }

    private string GetBorderBrushResourceKey()
    {
        if (ColorType == ColorState.Gray)
            return "ColorBrushGray2";

        return ColorType switch
        {
            ColorState.Normal => IsPointerOver ? "ColorBrush3" : "ColorBrush1",
            ColorState.Highlight => IsPointerOver ? "ColorBrush3" : "ColorBrush2",
            ColorState.Red => IsPointerOver ? "ColorBrushRedLight" : "ColorBrushRedDark",
            _ => "ColorBrush1"
        };
    }

    private void ButtonMouseEnter()
    {
        if (!IsEnabled || _foregroundBorder is null)
            return;

        if (UseExperimentalStyle)
        {
            RefreshExperimentalColor();
            return;
        }

        ControlVisualHelpers.AnimateColorOrSetResource(
            _foregroundBorder,
            Border.BackgroundProperty,
            ColorType == ColorState.Red ? "ColorBrushRedBack" : "ColorBrush7",
            AnimationColorIn,
            "MyButton Background " + Uuid,
            ControlVisualHelpers.ShouldAnimate(this));
    }

    private void ButtonMouseLeave()
    {
        if (_foregroundBorder is null)
            return;

        if (UseExperimentalStyle)
        {
            RefreshExperimentalColor();
            RestorePressedScaleIfNeeded();
            return;
        }

        ControlVisualHelpers.AnimateColorOrSetResource(
            _foregroundBorder,
            Border.BackgroundProperty,
            "ColorBrushHalfWhite",
            AnimationColorOut,
            "MyButton Background " + Uuid,
            ControlVisualHelpers.ShouldAnimate(this));
        RestorePressedScaleIfNeeded();
    }

    private double GetForegroundScale() =>
        _foregroundBorder?.RenderTransform is ScaleTransform scale ? scale.ScaleX : 1d;

    private void ApplyVisualStyle()
    {
        if (_foregroundBorder is null || _label is null)
            return;

        if (UseExperimentalStyle)
        {
            _foregroundBorder.CornerRadius = new CornerRadius(10d);
            _foregroundBorder.BorderThickness = new Thickness(1d);
            _foregroundBorder.MinHeight = 32d;
            _label.FontWeight = FontWeight.SemiBold;
            _label.FontSize = 13d;
            _label.LetterSpacing = 0.05d;
            return;
        }

        _foregroundBorder.CornerRadius = new CornerRadius(4d);
        _foregroundBorder.BorderThickness = new Thickness(1d);
        _foregroundBorder.MinHeight = 32d;
        _foregroundBorder.Background = LegacyResourceResolver.Brush(this, "ColorBrushHalfWhite", "#80ffffff");
        _foregroundBorder.BorderBrush = LegacyResourceResolver.Brush(this, "ColorBrush1", "#3a3a3a");
        _label.Foreground = LegacyResourceResolver.Brush(this, "ColorBrush1", "#3a3a3a");
        _label.FontWeight = FontWeight.Normal;
        _label.FontSize = 13d;
        _label.LetterSpacing = 0d;
    }

    private void RefreshExperimentalColor()
    {
        if (_foregroundBorder is null || _label is null)
            return;

        bool dark = AvaloniaThemeManager.IsDarkMode;
        bool hover = IsEnabled && IsPointerOver;
        Color surface;
        Color stroke;
        Color text;

        if (!IsEnabled)
        {
            surface = dark ? Color.Parse("#663A3A3E") : Color.Parse("#99E5E5EA");
            stroke = dark ? Color.Parse("#24FFFFFF") : Color.Parse("#14000000");
            text = dark ? Color.Parse("#829999A1") : Color.Parse("#7A6E6E73");
        }
        else if (ColorType == ColorState.Highlight)
        {
            IBrush accent = LegacyResourceResolver.Brush(this, hover ? "ColorBrush3" : "ColorBrush2", "#1370f3");
            surface = accent is SolidColorBrush accentBrush ? accentBrush.Color : Color.Parse("#1370f3");
            stroke = dark ? Color.Parse("#38FFFFFF") : Color.Parse("#1FFFFFFF");
            text = Color.Parse("#FFFFFFFF");
        }
        else if (ColorType == ColorState.Red)
        {
            surface = dark
                ? Color.Parse(hover ? "#B85C252B" : "#9952252A")
                : Color.Parse(hover ? "#FFF0D9DC" : "#FFF8E9EB");
            stroke = dark ? Color.Parse("#66FF6961") : Color.Parse("#40D70015");
            text = dark ? Color.Parse("#FFFF8A83") : Color.Parse("#FFD70015");
        }
        else
        {
            surface = dark
                ? Color.Parse(hover ? "#F04A4A50" : "#D83A3A3E")
                : Color.Parse(hover ? "#FFFFFFFF" : "#EEF2F2F7");
            stroke = dark ? Color.Parse("#32FFFFFF") : Color.Parse("#18000000");
            text = dark ? Color.Parse("#FFF2F2F7") : Color.Parse("#FF1C1C1E");
        }

        _foregroundBorder.Background = new SolidColorBrush(surface);
        _foregroundBorder.BorderBrush = new SolidColorBrush(stroke);
        _label.Foreground = new SolidColorBrush(text);
        Cursor = IsEnabled ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
    }

    private void RestorePressedScaleIfNeeded()
    {
        if (!_isPressed || _foregroundBorder is null)
            return;

        _isPressed = false;
        ModAnimation.AniStart(
            ModAnimation.AaScaleTransform(
                _foregroundBorder,
                1d - GetForegroundScale(),
                800,
                ease: new ModAnimation.AniEaseOutFluent(ModAnimation.AniEasePower.Strong)),
            "MyButton Scale " + Uuid);
    }
}
