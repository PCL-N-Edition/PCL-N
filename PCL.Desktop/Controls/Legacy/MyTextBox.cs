// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using FluentValidation;
using PCL.Desktop.Theme;

namespace PCL.Desktop.Controls.Legacy;

public class MyTextBox : TextBox
{
#pragma warning disable CA1711
    public delegate void ValidateChangedEventHandler(object sender, EventArgs e);
#pragma warning restore CA1711

    public static readonly StyledProperty<bool> HasBackgroundProperty =
        AvaloniaProperty.Register<MyTextBox, bool>(nameof(HasBackground), true);

    public static readonly StyledProperty<string> HintTextProperty =
        AvaloniaProperty.Register<MyTextBox, string>(nameof(HintText), string.Empty);

    public static readonly StyledProperty<string> ValidateResultProperty =
        AvaloniaProperty.Register<MyTextBox, string>(nameof(ValidateResult), string.Empty);

    public static readonly StyledProperty<bool> ShowValidateResultProperty =
        AvaloniaProperty.Register<MyTextBox, bool>(nameof(ShowValidateResult), true);

    public static readonly StyledProperty<bool> UseExperimentalStyleProperty =
        AvaloniaProperty.Register<MyTextBox, bool>(nameof(UseExperimentalStyle));

    private readonly List<EventHandler<TextChangedEventArgs>> _validatedTextChangedHandlers = [];
    private bool _isAttached;
    private bool _isTextChanged;
    private ValidateState _shownValidateResult = ValidateState.NotInited;
    private TextBlock? _hintTextBlock;
    private TextPresenter? _textPresenter;
    private TextBlock? _wrongTextBlock;

    public MyTextBox()
    {
        BorderThickness = new Thickness(1d);
        CornerRadius = new CornerRadius(3d);
        MinHeight = 28d;
        Padding = new Thickness(6d, 0d, 6d, 0d);
        VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center;
        HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left;
        MaxLength = 1000;
        Cursor = new Cursor(StandardCursorType.Ibeam);

        PointerPressed += OnPointerPressed;
        PointerEntered += (_, _) => RefreshVisual();
        PointerExited += (_, _) => RefreshVisual();
        GotFocus += (_, _) => RefreshVisual();
        LostFocus += (_, _) => RefreshVisual();
        TextChanged += MyTextBoxTextChanged;
        AttachedToVisualTree += (_, _) =>
        {
            _isAttached = true;
            ApplyVisualStyle();
            Validate();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _isAttached = false;
            AvaloniaThemeManager.ThemeChanged -= OnThemeChanged;
        };
        AvaloniaThemeManager.ThemeChanged += OnThemeChanged;
        this.GetObservable(IsEnabledProperty).Subscribe(_ =>
        {
            RefreshValidationVisual(ControlVisualHelpers.ShouldAnimate(this));
            RefreshVisual();
            RefreshTextColor();
        });
        this.GetObservable(HasBackgroundProperty).Subscribe(_ => RefreshVisual());
        this.GetObservable(ShowValidateResultProperty).Subscribe(_ =>
        {
            RefreshValidationVisual(ControlVisualHelpers.ShouldAnimate(this));
            RefreshVisual();
        });
        this.GetObservable(HintTextProperty).Subscribe(_ => RefreshHintText());
        this.GetObservable(ValidateResultProperty).Subscribe(_ =>
        {
            RefreshValidationVisual(ControlVisualHelpers.ShouldAnimate(this));
            RefreshVisual();
            ValidateChanged?.Invoke(this, EventArgs.Empty);
        });
        this.GetObservable(UseExperimentalStyleProperty).Subscribe(_ =>
        {
            ApplyVisualStyle();
            RefreshVisual();
            RefreshTextColor();
        });
        RefreshVisual();
        RefreshTextColor();
    }

    public event ValidateChangedEventHandler? ValidateChanged;

    public event EventHandler<TextChangedEventArgs> ValidatedTextChanged
    {
        add => _validatedTextChangedHandlers.Add(value);
        remove => _validatedTextChangedHandlers.Remove(value);
    }

    public int Uuid { get; } = Random.Shared.Next();

    public bool HasBackground
    {
        get => GetValue(HasBackgroundProperty);
        set => SetValue(HasBackgroundProperty, value);
    }

    public bool ShowValidateResult
    {
        get => GetValue(ShowValidateResultProperty);
        set => SetValue(ShowValidateResultProperty, value);
    }

    public string HintText
    {
        get => GetValue(HintTextProperty);
        set => SetValue(HintTextProperty, value);
    }

    public string ValidateResult
    {
        get => GetValue(ValidateResultProperty);
        set => SetValue(ValidateResultProperty, value);
    }

    public bool UseExperimentalStyle
    {
        get => GetValue(UseExperimentalStyleProperty);
        set => SetValue(UseExperimentalStyleProperty, value);
    }

    public bool IsValidated => string.IsNullOrEmpty(ValidateResult);

    public Collection<IValidator<string>> ValidateRules
    {
        get;
        set
        {
            field = value;
            Validate();
        }
    } = [];

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        _hintTextBlock = e.NameScope.Find<TextBlock>("labHint");
        _textPresenter = e.NameScope.Find<TextPresenter>("PART_TextPresenter");
        _wrongTextBlock = e.NameScope.Find<TextBlock>("labWrong");
        RefreshHintText();
        RefreshTextColor();
        RefreshTextPresenterStyle();
        RefreshValidationVisual(animate: false);
    }

    public void Validate()
    {
        string newResult = string.Empty;
        string value = Text ?? string.Empty;
        foreach (IValidator<string> rule in ValidateRules)
        {
            FluentValidation.Results.ValidationResult result = rule.Validate(value);
            if (!result.IsValid)
            {
                newResult = result.Errors.FirstOrDefault()?.ErrorMessage ?? "输入内容不符合要求";
                break;
            }
        }

        string oldResult = ValidateResult;
        ValidateResult = newResult;
        if (oldResult == newResult && RefreshValidationVisual(ControlVisualHelpers.ShouldAnimate(this)))
        {
            RefreshVisual();
            ValidateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void ForceShowAsSuccess()
    {
        _isTextChanged = false;
        RefreshValidationVisual(ControlVisualHelpers.ShouldAnimate(this));
        RefreshVisual();
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!IsEnabled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        Focus(NavigationMethod.Pointer, e.KeyModifiers);
    }

    private void MyTextBoxTextChanged(object? sender, TextChangedEventArgs e)
    {
        _isTextChanged = _isAttached;
        RefreshHintText();
        Validate();
        if (!IsValidated)
            return;

        foreach (EventHandler<TextChangedEventArgs> handler in _validatedTextChangedHandlers.ToArray())
            handler.Invoke(this, e);
    }

    private void ApplyVisualStyle()
    {
        if (TemplatedParent is MyComboBox)
            return;

        if (UseExperimentalStyle)
        {
            // Keep height compatible with classic form row spacing (28–32).
            CornerRadius = new CornerRadius(9d);
            MinHeight = 32d;
            Padding = new Thickness(10d, 0d, 10d, 0d);
            FontSize = 13d;
            return;
        }

        CornerRadius = new CornerRadius(3d);
        MinHeight = 28d;
        Padding = new Thickness(6d, 0d, 6d, 0d);
        FontSize = 14d;
    }

    private void RefreshVisual()
    {
        if (TemplatedParent is MyComboBox)
            return;

        if (UseExperimentalStyle)
        {
            RefreshExperimentalVisual();
            return;
        }

        bool showInvalid = IsEnabled && ShowValidateResult && !IsValidated && _isTextChanged;
        string foreColorName;
        string backColorName;
        int animationTime;
        if (IsEnabled)
        {
            if (showInvalid)
            {
                foreColorName = "ColorBrushRedLight";
                backColorName = "ColorBrushRedBack";
                animationTime = 200;
            }
            else if (IsKeyboardFocusWithin)
            {
                foreColorName = "ColorBrush3";
                backColorName = "ColorBrush7";
                animationTime = 10;
            }
            else if (IsPointerOver)
            {
                foreColorName = "ColorBrush4";
                backColorName = "ColorBrush7";
                animationTime = 100;
            }
            else
            {
                foreColorName = "ColorBrushBg0";
                backColorName = "ColorBrushHalfWhite";
                animationTime = 100;
            }

            SelectionBrush = FindBrush("ColorBrush3", "#1370f3");
            Cursor = new Cursor(StandardCursorType.Ibeam);
        }
        else
        {
            foreColorName = "ColorBrushGray5";
            backColorName = "ColorBrushGray6";
            animationTime = 200;
            Cursor = Cursor.Default;
        }
        RefreshTextPresenterStyle();

        if (!HasBackground)
            backColorName = "ColorBrushTransparent";

        if (ControlVisualHelpers.ShouldAnimate(this))
        {
            ModAnimation.AniStart(
                new[]
                {
                    ModAnimation.AaColor(this, BorderBrushProperty, foreColorName, animationTime),
                    ModAnimation.AaColor(this, BackgroundProperty, backColorName, animationTime)
                },
                "MyTextBox Color " + Uuid);
            return;
        }

        ModAnimation.AniStop("MyTextBox Color " + Uuid);
        BorderBrush = FindBrush(foreColorName, "#96c0f9");
        Background = HasBackground ? FindBrush(backColorName, "#55ffffff") : Brushes.Transparent;
    }

    private void RefreshExperimentalVisual()
    {
        bool dark = AvaloniaThemeManager.IsDarkMode;
        bool showInvalid = IsEnabled && ShowValidateResult && !IsValidated && _isTextChanged;
        bool focused = IsEnabled && IsKeyboardFocusWithin;
        bool hover = IsEnabled && IsPointerOver;

        Color surface;
        Color stroke;
        if (!IsEnabled)
        {
            surface = ExperimentalControlChrome.Palette.DisabledSurface(dark);
            stroke = ExperimentalControlChrome.Palette.DisabledStroke(dark);
            Cursor = Cursor.Default;
        }
        else if (showInvalid)
        {
            surface = dark ? Color.Parse("#9952252A") : Color.Parse("#FFF8E9EB");
            stroke = dark ? Color.Parse("#66FF6961") : Color.Parse("#40D70015");
            Cursor = new Cursor(StandardCursorType.Ibeam);
        }
        else
        {
            surface = HasBackground
                ? ExperimentalControlChrome.Palette.Surface(dark, hover, focused)
                : Colors.Transparent;
            stroke = ExperimentalControlChrome.Palette.Stroke(dark, focused);
            Cursor = new Cursor(StandardCursorType.Ibeam);
        }

        ModAnimation.AniStop("MyTextBox Color " + Uuid);
        BorderBrush = new SolidColorBrush(stroke);
        Background = new SolidColorBrush(surface);
        SelectionBrush = FindBrush("ColorBrush3", "#1370f3");
        RefreshTextPresenterStyle();
    }

    private void OnThemeChanged()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(OnThemeChanged, DispatcherPriority.Background);
            return;
        }

        RefreshVisual();
        RefreshTextColor();
    }

    private void RefreshHintText()
    {
        PlaceholderText = HintText;
        if (_hintTextBlock is not null)
            _hintTextBlock.Text = string.IsNullOrEmpty(Text) ? HintText : string.Empty;
    }

    private void RefreshTextPresenterStyle()
    {
        if (_textPresenter is null)
            return;

        _textPresenter.FontFamily = FontFamily;
        _textPresenter.FontSize = FontSize;
        _textPresenter.FontStyle = FontStyle;
        _textPresenter.FontWeight = FontWeight;
        _textPresenter.FontStretch = FontStretch;
        _textPresenter.Foreground = Foreground;
    }

    private void RefreshTextColor()
    {
        if (UseExperimentalStyle && TemplatedParent is not MyComboBox)
        {
            ModAnimation.AniStop("MyTextBox TextColor " + Uuid);
            Color text = ExperimentalControlChrome.Palette.Text(
                AvaloniaThemeManager.IsDarkMode,
                IsEnabled);
            Foreground = new SolidColorBrush(text);
            RefreshTextPresenterStyle();
            return;
        }

        string targetBrush = IsEnabled ? "ColorBrush1" : "ColorBrushGray4";
        if (ControlVisualHelpers.ShouldAnimate(this) && !string.IsNullOrEmpty(Text))
        {
            List<ModAnimation.AniData> animations =
            [
                ModAnimation.AaColor(this, ForegroundProperty, targetBrush, 200)
            ];
            if (_textPresenter is not null)
                animations.Add(ModAnimation.AaColor(_textPresenter, TextBlock.ForegroundProperty, targetBrush, 200));

            ModAnimation.AniStart(
                animations,
                "MyTextBox TextColor " + Uuid);
            return;
        }

        ModAnimation.AniStop("MyTextBox TextColor " + Uuid);
        Foreground = FindBrush(targetBrush, IsEnabled ? "#343d4a" : "#a6a6a6");
        RefreshTextPresenterStyle();
    }

    private bool RefreshValidationVisual(bool animate)
    {
        if (_wrongTextBlock is null)
        {
            _shownValidateResult = ValidateState.NotLoaded;
            return false;
        }

        bool isSuccessful = IsValidated;
        bool showInvalid = IsEnabled && ShowValidateResult && !isSuccessful && _isTextChanged;
        ValidateState nextState = isSuccessful
            ? ValidateState.Success
            : showInvalid
                ? ValidateState.FailedAndShowDetail
                : ShowValidateResult ? ValidateState.FailedButTextNotChanged : ValidateState.FailedAndHideDetail;

        if (_shownValidateResult == nextState && _wrongTextBlock.Text == (showInvalid ? ValidateResult : string.Empty))
            return false;

        _shownValidateResult = nextState;
        string animationKey = "MyTextBox Validate " + Uuid;
        if (showInvalid)
        {
            _wrongTextBlock.Text = ValidateResult;
            _wrongTextBlock.IsVisible = true;
            if (animate)
            {
                ModAnimation.AniStart(
                    new[]
                    {
                        ModAnimation.AaOpacity(_wrongTextBlock, 1d - _wrongTextBlock.Opacity, 150),
                        ModAnimation.AaHeight(_wrongTextBlock, 21d - _wrongTextBlock.Height, 150, ease: new ModAnimation.AniEaseOutFluent())
                    },
                    animationKey);
                return true;
            }

            ModAnimation.AniStop(animationKey);
            _wrongTextBlock.Height = 21d;
            _wrongTextBlock.Opacity = 1d;
            return true;
        }

        if (animate)
        {
            ModAnimation.AniStart(
                new[]
                {
                    ModAnimation.AaOpacity(_wrongTextBlock, -_wrongTextBlock.Opacity, 150),
                    ModAnimation.AaHeight(_wrongTextBlock, -_wrongTextBlock.Height, 150, ease: new ModAnimation.AniEaseOutFluent()),
                    ModAnimation.AaCode(() =>
                    {
                        _wrongTextBlock.IsVisible = false;
                        _wrongTextBlock.Text = string.Empty;
                    }, after: true)
                },
                animationKey);
            return true;
        }

        ModAnimation.AniStop(animationKey);
        _wrongTextBlock.Text = string.Empty;
        _wrongTextBlock.IsVisible = false;
        _wrongTextBlock.Height = 0d;
        _wrongTextBlock.Opacity = 0d;
        return true;
    }

    private IBrush FindBrush(string key, string fallback)
    {
        return LegacyResourceResolver.Brush(this, key, fallback);
    }

    private enum ValidateState
    {
        NotInited,
        Success,
        FailedButTextNotChanged,
        FailedAndShowDetail,
        FailedAndHideDetail,
        NotLoaded
    }
}
