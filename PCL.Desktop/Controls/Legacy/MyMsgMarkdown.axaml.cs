// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using PCL.Desktop.Shell;

namespace PCL.Desktop.Controls.Legacy;

public sealed class MyMsgMarkdownClosedEventArgs(int result) : EventArgs
{
    public int Result { get; } = result;
}

public partial class MyMsgMarkdown : Grid
{
    private readonly int _uuid = Random.Shared.Next();
    private TranslateTransform? _transformPos;
    private RotateTransform? _transformRotate;
    private ScaleTransform? _transformScale;
    private bool _useExperimentalChrome;
    private int _pendingResult;
    private AnimationMode _animationMode;
    private MyMsgDialogModel? _model;

    public MyMsgMarkdown()
    {
        AvaloniaXamlLoader.Load(this);
        CaptureTransforms();
        AttachedToVisualTree += (_, _) => ApplyExperimentalChrome();
        Opacity = 0d;
    }

    public event EventHandler<MyMsgMarkdownClosedEventArgs>? Closed;

    public bool IsClosing => _animationMode == AnimationMode.Closing;

    public void Configure(
        string title,
        string markdown,
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
            markdown,
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
        if (this.FindControl<MyMarkdownViewer>("LabCaption") is { } caption)
            caption.Markdown = model.Content;

        ApplyExperimentalChrome();
    }

    public void BeginShowAnimation()
    {
        ApplyExperimentalChrome();
        _pendingResult = 0;
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
        ModAnimation.AniStart(
        new List<ModAnimation.AniData>
        {
            ModAnimation.AaOpacity(this, 1d, 120, 60),
            ModAnimation.AaDouble(AddTransformY, -GetTransformY(), 300, 60, new ModAnimation.AniEaseOutBack(ModAnimation.AniEasePower.Weak)),
            ModAnimation.AaDouble(AddTransformAngle, -GetTransformAngle(), 300, 60, new ModAnimation.AniEaseOutFluent(ModAnimation.AniEasePower.Weak)),
            ModAnimation.AaCode(() => _animationMode = AnimationMode.None, after: true)
        }, $"MyMsgBox {_uuid}");
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
        }, $"MyMsgBox {_uuid}");
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

    public void Btn1Click(object? sender, EventArgs e)
    {
        InvokeButton(this.FindControl<MyButton>("Btn1"), 1);
    }

    public void Btn2Click(object? sender, EventArgs e)
    {
        InvokeButton(this.FindControl<MyButton>("Btn2"), 2);
    }

    public void Btn3Click(object? sender, EventArgs e)
    {
        InvokeButton(this.FindControl<MyButton>("Btn3"), 3);
    }

    public void BtnLeftClick(object? sender, EventArgs e) =>
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
        _pendingResult = result;
        BeginCloseAnimation(() => Closed?.Invoke(this, new MyMsgMarkdownClosedEventArgs(_pendingResult)));
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

    private enum AnimationMode
    {
        None,
        Opening,
        Closing
    }
}
