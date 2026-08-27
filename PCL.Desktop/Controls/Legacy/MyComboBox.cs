// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PathShape = Avalonia.Controls.Shapes.Path;
using PCL.Desktop.Theme;

namespace PCL.Desktop.Controls.Legacy;

public class MyComboBox : ComboBox
{
    // Keep the WPF event shape for copied pages that bind TextChanged in XAML.
    #pragma warning disable CA1711
    public delegate void TextChangedEventHandler(object sender, TextChangedEventArgs? e);
    #pragma warning restore CA1711

    public static readonly StyledProperty<string> HintTextProperty =
        AvaloniaProperty.Register<MyComboBox, string>(nameof(HintText), string.Empty);

    public static readonly StyledProperty<string> SelectionTextProperty =
        AvaloniaProperty.Register<MyComboBox, string>(nameof(SelectionText), string.Empty);

    public static readonly StyledProperty<bool> UseExperimentalStyleProperty =
        AvaloniaProperty.Register<MyComboBox, bool>(nameof(UseExperimentalStyle));

    private bool _isMouseDown;
    private bool _isTextChanging;
    private bool _isEnsuringMarkedSelection;
    private int _dropDownCloseRevision;
    private double _realWidth = double.NaN;
    private string _text = string.Empty;
    private PathShape? _dropDownArrow;
    private ContentPresenter? _selectedContentPresenter;
    private TextBox? _editableTextBox;
    private Grid? _panPopup;
    private Border? _chromeBorder;
    private Border? _dropDownBorder;
    private Border? _dropDownSurface;
    private IDisposable? _selectedContentSubscription;

    public MyComboBox()
    {
        _text = SelectedItem?.ToString() ?? string.Empty;
        PointerPressed += MyComboBox_PointerPressed;
        PointerReleased += MyComboBox_PointerReleased;
        PointerExited += MyComboBox_PointerReleased;
        PointerEntered += (_, _) => RefreshColor();
        PointerExited += (_, _) => RefreshColor();
        GotFocus += (_, _) => RefreshColor();
        LostFocus += (_, _) => RefreshColor();
        DropDownOpened += MyComboBox_DropDownOpened;
        DropDownClosed += MyComboBox_DropDownClosed;
        SelectionChanged += MyComboBox_SelectionChanged;
        AvaloniaThemeManager.ThemeChanged += OnThemeChanged;
        DetachedFromVisualTree += (_, _) => AvaloniaThemeManager.ThemeChanged -= OnThemeChanged;
        this.GetObservable(IsEnabledProperty).Subscribe(_ => RefreshColor());
        this.GetObservable(IsDropDownOpenProperty).Subscribe(_ =>
        {
            RefreshColor();
            RefreshDropDownArrow(animate: true);
        });
        this.GetObservable(IsEditableProperty).Subscribe(_ => RefreshEditableVisibility());
        this.GetObservable(HintTextProperty).Subscribe(text => PlaceholderText = text);
        this.GetObservable(ComboBox.TextProperty).Subscribe(OnTextPropertyChanged);
        this.GetObservable(UseExperimentalStyleProperty).Subscribe(_ =>
        {
            ApplyVisualStyle();
            RefreshColor();
        });
        AttachedToVisualTree += (_, _) =>
        {
            EnsureWpfMarkedSelection();
            RefreshSelectionText();
            ApplyVisualStyle();
            Dispatcher.UIThread.Post(() =>
            {
                EnsureWpfMarkedSelection();
                RefreshSelectionText();
                ApplyVisualStyle();
            }, DispatcherPriority.Loaded);
        };
        RefreshColor();
    }

    private void OnThemeChanged()
    {
        RefreshColor();
    }

    public event TextChangedEventHandler? TextChanged;

    public int Uuid { get; } = Random.Shared.Next();

    public string HintText
    {
        get => GetValue(HintTextProperty);
        set => SetValue(HintTextProperty, value);
    }

    public string SelectionText
    {
        get => GetValue(SelectionTextProperty);
        private set => SetCurrentValue(SelectionTextProperty, value);
    }

    public bool UseExperimentalStyle
    {
        get => GetValue(UseExperimentalStyleProperty);
        set => SetValue(UseExperimentalStyleProperty, value);
    }

    public new string Text
    {
        get => IsEditable
            ? base.Text ?? _text
            : SelectedItem?.ToString() ?? string.Empty;
        set
        {
            if (!IsEditable)
                throw new NotSupportedException("该 ComboBox 不支持修改文本。");

            _text = value;
            base.Text = value;
        }
    }

    public bool DropDownWidthSync { get; set; } = true;

    public string SelectedValuePath { get; set; } = string.Empty;

    public ContentPresenter? ContentPresenter =>
        _selectedContentPresenter ?? this.FindDescendantOfType<ContentPresenter>();

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        if (_editableTextBox is not null)
            _editableTextBox.TextChanged -= EditableTextBox_TextChanged;
        base.OnApplyTemplate(e);
        _dropDownArrow = e.NameScope.Find<PathShape>("PART_DropDownArrow");
        _selectedContentPresenter = e.NameScope.Find<ContentPresenter>("PART_Content")
            ?? e.NameScope.Find<ContentPresenter>("PART_ContentPresenter");
        _editableTextBox = e.NameScope.Find<TextBox>("PART_EditableTextBox");
        _panPopup = e.NameScope.Find<Grid>("PanPopup");
        _chromeBorder = e.NameScope.Find<Border>("border");
        _dropDownBorder = e.NameScope.Find<Border>("dropDownBorder");
        _dropDownSurface = _panPopup?.Children.OfType<Border>().FirstOrDefault();
        if (_dropDownArrow is not null)
        {
            _dropDownArrow.RenderTransformOrigin = new RelativePoint(0.5d, 0.5d, RelativeUnit.Relative);
            if (_dropDownArrow.RenderTransform is not RotateTransform)
                _dropDownArrow.RenderTransform = new RotateTransform();
        }

        if (_editableTextBox is not null)
        {
            _editableTextBox.Tag = Tag;
            _editableTextBox.Text = base.Text ?? _text;
            _editableTextBox.TextChanged += EditableTextBox_TextChanged;
            _editableTextBox.GetObservable(IsFocusedProperty).Subscribe(_ => RefreshColor());
            // Editable slot is a transparent overlay inside the combo chrome — never
            // paint a second border/surface.
            _editableTextBox.BorderThickness = new Thickness(0d);
            _editableTextBox.Background = Brushes.Transparent;
            if (_editableTextBox is MyTextBox myTextBox)
            {
                myTextBox.HintText = HintText;
                myTextBox.HasBackground = false;
                myTextBox.UseExperimentalStyle = false;
                myTextBox.BorderThickness = new Thickness(0d);
            }
        }

        ApplyVisualStyle();

        RefreshEditableVisibility();
        RefreshDropDownArrow(animate: false);
        EnsureWpfMarkedSelection();
        RefreshSelectionText();
    }

    protected override bool NeedsContainerOverride(object? item, int index, out object? recycleKey)
    {
        if (item is MyComboBoxItem)
        {
            recycleKey = null;
            return false;
        }

        recycleKey = typeof(MyComboBoxItem);
        return true;
    }

    protected override Control CreateContainerForItemOverride(object? item, int index, object? recycleKey) =>
        new MyComboBoxItem();

    protected override void PrepareContainerForItemOverride(Control container, object? item, int index)
    {
        base.PrepareContainerForItemOverride(container, item, index);
        if (container is MyComboBoxItem comboBoxItem && item is not MyComboBoxItem)
            comboBoxItem.Content = item;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ItemsSourceProperty ||
            change.Property == SelectedItemProperty ||
            change.Property == SelectedIndexProperty)
        {
            _text = SelectedItem?.ToString() ?? string.Empty;
            RefreshSelectionText();
        }
    }

    public void RefreshColor()
    {
        if (UseExperimentalStyle)
        {
            RefreshExperimentalColor();
            return;
        }

        string foreColorName;
        string backColorName;
        int time;
        if (IsEnabled)
        {
            if (_isMouseDown || IsDropDownOpen || IsFocused || (IsEditable && _editableTextBox?.IsFocused == true))
            {
                foreColorName = "ColorBrush3";
                backColorName = "ColorBrush7";
                time = 10;
            }
            else if (IsPointerOver)
            {
                foreColorName = "ColorBrush4";
                backColorName = "ColorBrush7";
                time = 100;
            }
            else
            {
                foreColorName = "ColorBrushBg0";
                backColorName = "ColorBrushHalfWhite";
                time = 100;
            }
        }
        else
        {
            foreColorName = "ColorBrushGray5";
            backColorName = "ColorBrushGray6";
            time = 200;
        }

        if (ControlVisualHelpers.ShouldAnimate(this))
        {
            ModAnimation.AniStart(
                new[]
                {
                    ModAnimation.AaColor(this, ForegroundProperty, foreColorName, time),
                    ModAnimation.AaColor(this, BackgroundProperty, backColorName, time)
                },
                "MyComboBox Color " + Uuid);
            return;
        }

        ModAnimation.AniStop("MyComboBox Color " + Uuid);
        Foreground = FindBrush(foreColorName, "#96c0f9");
        Background = FindBrush(backColorName, "#55ffffff");
    }

    private void ApplyVisualStyle()
    {
        if (UseExperimentalStyle)
        {
            // Match classic row spacing; avoid 36px controls overflowing 28px rows.
            MinHeight = 32d;
            FontSize = 13d;
            if (_chromeBorder is not null)
                _chromeBorder.CornerRadius = new CornerRadius(9d);
            if (_dropDownBorder is not null)
                _dropDownBorder.CornerRadius = new CornerRadius(12d);
            if (_dropDownSurface is not null)
                _dropDownSurface.CornerRadius = new CornerRadius(12d);
            return;
        }

        MinHeight = 28d;
        FontSize = 14d;
        if (_chromeBorder is not null)
            _chromeBorder.CornerRadius = new CornerRadius(4d);
        if (_dropDownBorder is not null)
            _dropDownBorder.CornerRadius = new CornerRadius(4d);
        if (_dropDownSurface is not null)
            _dropDownSurface.CornerRadius = new CornerRadius(4d);
    }

    private void RefreshExperimentalColor()
    {
        bool dark = AvaloniaThemeManager.IsDarkMode;
        bool focused = IsEnabled &&
                       (_isMouseDown || IsDropDownOpen || IsFocused ||
                        (IsEditable && _editableTextBox?.IsFocused == true));
        bool hover = IsEnabled && IsPointerOver;

        Color surface;
        Color stroke;
        Color text;
        if (!IsEnabled)
        {
            surface = ExperimentalControlChrome.Palette.DisabledSurface(dark);
            stroke = ExperimentalControlChrome.Palette.DisabledStroke(dark);
            text = ExperimentalControlChrome.Palette.Text(dark, enabled: false);
        }
        else
        {
            surface = ExperimentalControlChrome.Palette.Surface(dark, hover, focused);
            stroke = ExperimentalControlChrome.Palette.Stroke(dark, focused);
            text = ExperimentalControlChrome.Palette.Text(dark, enabled: true);
        }

        ModAnimation.AniStop("MyComboBox Color " + Uuid);
        Background = new SolidColorBrush(surface);
        Foreground = new SolidColorBrush(text);
        BorderBrush = new SolidColorBrush(stroke);
        if (_chromeBorder is not null)
        {
            _chromeBorder.Background = Background;
            _chromeBorder.BorderBrush = new SolidColorBrush(stroke);
        }

        if (_dropDownArrow is not null)
            _dropDownArrow.Stroke = new SolidColorBrush(text);
    }

    private void RefreshEditableVisibility()
    {
        if (_selectedContentPresenter is not null)
            _selectedContentPresenter.IsVisible = !IsEditable;
        if (_editableTextBox is not null)
            _editableTextBox.IsVisible = IsEditable;

        IsTabStop = !IsEditable;
    }

    private void RefreshDropDownArrow(bool animate)
    {
        if (_dropDownArrow?.RenderTransform is not RotateTransform rotate)
            return;

        double targetAngle = IsDropDownOpen ? 180d : 0d;
        if (animate && ControlVisualHelpers.ShouldAnimate(this))
        {
            ModAnimation.AniStart(
                ModAnimation.AaRotateTransform(
                    _dropDownArrow,
                    targetAngle - rotate.Angle,
                    200,
                    ease: new ModAnimation.AniEaseOutFluent()),
                "MyComboBox Arrow " + Uuid);
            return;
        }

        ModAnimation.AniStop("MyComboBox Arrow " + Uuid);
        rotate.Angle = targetAngle;
    }

    private void MyComboBox_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _isMouseDown = true;
            RefreshColor();
            // Chrome (and middle text, which is not independently hit-testable) toggles
            // the menu via PART_DropDownButton; keep arrow/color in sync immediately.
            RefreshDropDownArrow(animate: true);
        }
    }

    private void MyComboBox_PointerReleased(object? sender, PointerEventArgs e)
    {
        _isMouseDown = false;
        RefreshColor();
        RefreshDropDownArrow(animate: true);
    }

    private void MyComboBox_DropDownOpened(object? sender, EventArgs e)
    {
        RefreshSelectionText();
        EnsureWpfMarkedSelection();
        // Never mutate control Width on open — that collapses selection captions and
        // forces parent ScrollViewer offsets to jump. Size the popup surface instead.
        if (_dropDownBorder is not null && Bounds.Width > 0d)
            _dropDownBorder.MinWidth = Bounds.Width;

        if (_panPopup is not null)
        {
            TopLevel? topLevel = TopLevel.GetTopLevel(this);
            _panPopup.Opacity = topLevel?.Opacity ?? 1d;
        }

        RefreshDropDownArrow(animate: true);
        RefreshColor();
    }

    private void MyComboBox_DropDownClosed(object? sender, EventArgs e)
    {
        _dropDownCloseRevision++;
        // Restore any legacy Width mutation from older builds; keep NaN (auto) otherwise.
        if (!double.IsNaN(_realWidth))
            Width = _realWidth;
        _realWidth = double.NaN;
        // Outside click / light-dismiss and item selection both land here — reset arrow.
        RefreshDropDownArrow(animate: true);
        RefreshSelectionText();
        RefreshColor();
    }

    private void MyComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        _text = SelectedItem?.ToString() ?? string.Empty;
        RefreshSelectionText();
        if (!IsDropDownOpen || _isEnsuringMarkedSelection)
            return;

        // Popup.Open performs a synchronous layout pass while PopupOverlayLayer is
        // enumerating its children. Closing here used to remove the popup from that
        // collection mid-enumeration (issues #69/#73). Finish the current input/layout
        // transaction first, then preserve the WPF behavior of closing after selection.
        int revision = ++_dropDownCloseRevision;
        Dispatcher.UIThread.Post(
            () =>
            {
                if (revision == _dropDownCloseRevision && IsDropDownOpen)
                    IsDropDownOpen = false;
            },
            DispatcherPriority.Background);
    }

    private void EnsureWpfMarkedSelection()
    {
        if (SelectedIndex >= 0 || SelectedItem is not null)
            return;

        foreach (object? item in Items)
        {
            if (item is not MyComboBoxItem { IsSelected: true } comboBoxItem)
                continue;

            _isEnsuringMarkedSelection = true;
            try
            {
                SelectedItem = comboBoxItem;
                _text = comboBoxItem.ToString();
                RefreshSelectionText();
            }
            finally
            {
                _isEnsuringMarkedSelection = false;
            }
            return;
        }
    }

    /// <summary>Recompute closed-state selection caption (used after programmatic Items rebuild).</summary>
    public void RefreshSelectionDisplay() => RefreshSelectionText();

    private void RefreshSelectionText()
    {
        _selectedContentSubscription?.Dispose();
        _selectedContentSubscription = null;

        if (SelectedItem is MyComboBoxItem item)
        {
            SelectionText = FormatItemContent(item.Content);
            // DynamicResource content often resolves only after attach; keep caption in sync.
            _selectedContentSubscription = item.GetObservable(ContentControl.ContentProperty)
                .Subscribe(content => SelectionText = FormatItemContent(content));
            return;
        }

        SelectionText = SelectedItem switch
        {
            null => string.Empty,
            _ => SelectedItem.ToString() ?? string.Empty
        };
    }

    private static string FormatItemContent(object? content)
    {
        if (content is null)
            return string.Empty;
        if (content is string text)
            return text;
        string? rendered = content.ToString();
        if (string.IsNullOrWhiteSpace(rendered) ||
            string.Equals(rendered, content.GetType().FullName, StringComparison.Ordinal))
            return string.Empty;
        return rendered;
    }

    private void OnTextPropertyChanged(string? text)
    {
        if (_isTextChanging || !IsEditable)
            return;

        _text = text ?? string.Empty;
        if (_editableTextBox is not null && !string.Equals(_editableTextBox.Text, _text, StringComparison.Ordinal))
        {
            int currentCaret = _editableTextBox.CaretIndex;
            _editableTextBox.Text = _text;
            _editableTextBox.CaretIndex = Math.Clamp(currentCaret, 0, _text.Length);
        }
        TextChanged?.Invoke(this, new TextChangedEventArgs(TextBox.TextChangedEvent, this));
        if (SelectedItem is null || Text == SelectedItem.ToString())
            return;

        string rawText = Text;
        int? rawCaretIndex = _editableTextBox?.CaretIndex;
        _isTextChanging = true;
        SelectedItem = null;
        base.Text = rawText;
        if (_editableTextBox is not null && rawCaretIndex is int caretIndex)
            _editableTextBox.CaretIndex = Math.Clamp(caretIndex, 0, _editableTextBox.Text?.Length ?? 0);
        _isTextChanging = false;
    }

    private void EditableTextBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_isTextChanging || _editableTextBox is null)
            return;

        _isTextChanging = true;
        string text = _editableTextBox.Text ?? string.Empty;
        _text = text;
        base.Text = text;
        if (SelectedItem is not null && !string.Equals(text, SelectedItem.ToString(), StringComparison.Ordinal))
            SelectedItem = null;
        _isTextChanging = false;
        TextChanged?.Invoke(this, e);
    }

    private IBrush FindBrush(string key, string fallback)
    {
        return LegacyResourceResolver.Brush(this, key, fallback);
    }
}
