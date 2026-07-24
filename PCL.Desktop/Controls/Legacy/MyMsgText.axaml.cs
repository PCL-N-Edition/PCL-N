// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using PCL.Desktop.Shell;

namespace PCL.Desktop.Controls.Legacy;

public sealed class MyMsgTextClosedEventArgs(int result) : EventArgs
{
    public int Result { get; } = result;

    public bool IsPrimary => Result == 1;
}

public partial class MyMsgText : Grid
{
    private readonly int _uuid = Random.Shared.Next();
    private TranslateTransform? _transformPos;
    private RotateTransform? _transformRotate;
    private ScaleTransform? _transformScale;
    private bool _useExperimentalChrome;
    private int _pendingResult = 1;
    private AnimationMode _animationMode;
    private MyMsgDialogModel? _model;

    public MyMsgText()
    {
        AvaloniaXamlLoader.Load(this);
        CaptureTransforms();
        AttachedToVisualTree += (_, _) =>
        {
            ApplyExperimentalChrome();
            UpdateCaptionMaxHeight();
        };
        EffectiveViewportChanged += (_, _) => UpdateCaptionMaxHeight();
        Opacity = 0d;
    }

    public event EventHandler<MyMsgTextClosedEventArgs>? Closed;

    public bool IsClosing => _animationMode == AnimationMode.Closing;

    public void Configure(
        string title,
        string caption,
        string primaryButton = "确定",
        string secondaryButton = "",
        string thirdButton = "",
        bool isWarn = false,
        Action? primaryAction = null,
        Action? secondaryAction = null,
        Action? thirdAction = null)
    {
        Configure(MyMsgDialogModel.CreateLegacy(
            title,
            caption,
            primaryButton,
            secondaryButton,
            thirdButton,
            isWarn,
            primaryAction,
            secondaryAction,
            thirdAction));
    }

    public void Configure(MyMsgDialogModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        _model = model;
        ApplyModel();
        if (this.FindControl<TextBlock>("LabCaption") is { } captionBlock)
        {
            // Long Windows paths rarely contain spaces; force soft break opportunities so Wrap
            // actually multi-lines instead of clipping mid-path in compact dialogs (#49).
            captionBlock.Text = SoftBreakLongTokens(model.Content);
            captionBlock.TextWrapping = TextWrapping.Wrap;
            captionBlock.TextTrimming = TextTrimming.None;
            // Constrain width before first measure so the first layout wraps correctly.
            double maxWidth = Math.Clamp((TopLevel.GetTopLevel(this)?.ClientSize.Width ?? 720d) - 120d, 280d, 640d);
            captionBlock.MaxWidth = maxWidth;
            if (this.FindControl<MyScrollViewer>("PanCaption") is { } captionScroll)
                captionScroll.MaxWidth = maxWidth + 24d;
        }

        ApplyExperimentalChrome();
    }

    public void BeginShowAnimation()
    {
        ApplyExperimentalChrome();
        _animationMode = AnimationMode.Opening;
        if (_useExperimentalChrome && _transformScale is not null && _transformPos is not null)
        {
            ExperimentalMsgChrome.RunShowAnimation(
                this,
                _transformScale,
                _transformPos,
                _uuid,
                () => _animationMode = AnimationMode.None);
            return;
        }

        CaptureTransforms();
        Opacity = 0d;
        SetTransform(40d, -4d);
        string name = $"MyMsgBox {_uuid}";
        ModAnimation.AniStart(
        new List<ModAnimation.AniData>
        {
            ModAnimation.AaOpacity(this, 1d, 120, 60),
            ModAnimation.AaDouble(AddTransformY, -GetTransformY(), 300, 60, new ModAnimation.AniEaseOutBack(ModAnimation.AniEasePower.Weak)),
            ModAnimation.AaDouble(AddTransformAngle, -GetTransformAngle(), 300, 60, new ModAnimation.AniEaseOutFluent(ModAnimation.AniEasePower.Weak)),
            ModAnimation.AaCode(() => _animationMode = AnimationMode.None, after: true)
        }, name);
    }

    public void BeginCloseAnimation(Action? completed = null)
    {
        if (_animationMode == AnimationMode.Closing)
            return;

        _animationMode = AnimationMode.Closing;
        if (_useExperimentalChrome && _transformScale is not null && _transformPos is not null)
        {
            ExperimentalMsgChrome.RunCloseAnimation(
                this,
                _transformScale,
                _transformPos,
                _uuid,
                () =>
                {
                    _animationMode = AnimationMode.None;
                    completed?.Invoke();
                });
            return;
        }

        CaptureTransforms();
        string name = $"MyMsgBox {_uuid}";
        ModAnimation.AniStart(
        new List<ModAnimation.AniData>
        {
            ModAnimation.AaOpacity(this, -Opacity, 80, 20),
            ModAnimation.AaDouble(AddTransformY, 20d - GetTransformY(), 150, 0, new ModAnimation.AniEaseOutFluent()),
            ModAnimation.AaDouble(AddTransformAngle, 6d - GetTransformAngle(), 150, 0, new ModAnimation.AniEaseInFluent(ModAnimation.AniEasePower.Weak)),
            ModAnimation.AaCode(() =>
            {
                _animationMode = AnimationMode.None;
                completed?.Invoke();
            }, after: true)
        }, name);
    }

    private void ApplyExperimentalChrome()
    {
        if (!ExperimentalMsgChrome.IsEnabled)
        {
            ExperimentalMsgChrome.RestoreShell(
                this,
                this.FindControl<Border>("PanBorder"),
                this.FindControl<TextBlock>("LabTitle"),
                this.FindControl<Rectangle>("ShapeLine"),
                this.FindControl<Panel>("PanActions"));
            _useExperimentalChrome = false;
            _transformScale = null;
            _transformPos = null;
            _transformRotate = null;
            CaptureTransforms();
            ApplyModel();
            return;
        }

        _useExperimentalChrome = true;
        ExperimentalMsgChrome.ApplyShell(
            this,
            this.FindControl<Border>("PanBorder"),
            this.FindControl<TextBlock>("LabTitle"),
            this.FindControl<Rectangle>("ShapeLine"),
            this.FindControl<Panel>("PanActions"));
        (ScaleTransform scale, TranslateTransform translate) = ExperimentalMsgChrome.PrepareOpenTransforms(this);
        _transformScale = scale;
        _transformPos = translate;
        _transformRotate = null;
    }

    private void Btn1Click(object? sender, EventArgs e)
    {
        InvokeButton(this.FindControl<MyButton>("Btn1"), 1);
    }

    private void Btn2Click(object? sender, EventArgs e)
    {
        InvokeButton(this.FindControl<MyButton>("Btn2"), 2);
    }

    private void Btn3Click(object? sender, EventArgs e)
    {
        InvokeButton(this.FindControl<MyButton>("Btn3"), 3);
    }

    private void BtnLeftClick(object? sender, EventArgs e) =>
        InvokeButton(this.FindControl<MyButton>("BtnLeft"), 0);

    private void InvokeButton(MyButton? button, int fallbackResult)
    {
        if (_animationMode == AnimationMode.Closing)
            return;

        MyMsgDialogButton? action = MyMsgDialogPresenter.GetAction(button);
        if (action?.Action is { } callback)
        {
            callback();
            return;
        }

        CloseWithResult(action?.Result ?? fallbackResult);
    }

    private void CloseWithResult(int result)
    {
        if (_animationMode == AnimationMode.Closing)
            return;

        _pendingResult = result;
        BeginCloseAnimation(() => Closed?.Invoke(this, new MyMsgTextClosedEventArgs(_pendingResult)));
    }

    private void CaptureTransforms()
    {
        if (RenderTransform is not TransformGroup group)
            return;

        foreach (ITransform transform in group.Children)
        {
            _transformRotate ??= transform as RotateTransform;
            _transformPos ??= transform as TranslateTransform;
        }
    }

    private void SetTransform(double y, double angle)
    {
        if (_transformPos is not null)
            _transformPos.Y = y;
        if (_transformRotate is not null)
            _transformRotate.Angle = angle;
    }

    private double GetTransformY() =>
        _transformPos?.Y ?? 0d;

    private double GetTransformAngle() =>
        _transformRotate?.Angle ?? 0d;

    private void AddTransformY(double value)
    {
        if (_transformPos is not null)
            _transformPos.Y += value;
    }

    private void AddTransformAngle(double value)
    {
        if (_transformRotate is not null)
            _transformRotate.Angle += value;
    }

    private void UpdateCaptionMaxHeight()
    {
        if (this.FindControl<MyScrollViewer>("PanCaption") is not { } caption)
            return;

        double availableHeight = TopLevel.GetTopLevel(this)?.ClientSize.Height ?? Bounds.Height;
        if (availableHeight <= 0d)
            return;

        const double nonCaptionHeight = 142d;
        caption.MaxHeight = Math.Max(72d, availableHeight - Margin.Top - Margin.Bottom - nonCaptionHeight);
    }

    private void ApplyModel()
    {
        if (_model is null)
            return;

        MyMsgDialogPresenter.Apply(
            this,
            _model,
            this.FindControl<TextBlock>("LabTitle"),
            this.FindControl<Rectangle>("ShapeLine"),
            this.FindControl<MyButton>("BtnLeft"),
            this.FindControl<MyButton>("Btn1"),
            this.FindControl<MyButton>("Btn2"),
            this.FindControl<MyButton>("Btn3"));
    }

    /// <summary>
    /// Insert zero-width spaces after path separators / long-token breaks so TextWrapping can
    /// multi-line without changing the visual glyphs users copy from the dialog.
    /// </summary>
    internal static string SoftBreakLongTokens(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        // ZWSP is invisible and is stripped by most path parsers when users retype, but keeps
        // the full path selectable. Prefer breaks after path separators and hyphens in filenames.
        return text
            .Replace("\\", "\\\u200b", StringComparison.Ordinal)
            .Replace("/", "/\u200b", StringComparison.Ordinal)
            .Replace("-", "-\u200b", StringComparison.Ordinal)
            .Replace("_", "_\u200b", StringComparison.Ordinal);
    }

    private enum AnimationMode
    {
        None,
        Opening,
        Closing
    }
}
