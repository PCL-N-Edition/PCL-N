// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Avalonia.Controls;
using Avalonia.Media;

namespace PCL.Desktop.Controls.Legacy;

public sealed class MyMsgColorClosedEventArgs(Color? color) : EventArgs
{
    public Color? Color { get; } = color;
}

public partial class MyMsgColor : Grid
{
    private readonly int _uuid = Random.Shared.Next();
    private bool _isClosing;

    public MyMsgColor()
    {
        Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
        Opacity = 0d;
    }

    public event EventHandler<MyMsgColorClosedEventArgs>? Closed;

    public event EventHandler<Color>? PreviewChanged;

    public void Configure(string title, Color initialColor)
    {
        this.FindControl<TextBlock>("LabTitle")!.Text = title;
        ColorPicker picker = this.FindControl<ColorPicker>("Picker")!;
        picker.Color = initialColor;
        picker.ColorChanged += (_, args) => PreviewChanged?.Invoke(this, args.NewColor);
    }

    public void BeginShowAnimation()
    {
        TransformGroup transforms = (TransformGroup)RenderTransform!;
        RotateTransform rotate = transforms.Children.OfType<RotateTransform>().First();
        TranslateTransform translate = transforms.Children.OfType<TranslateTransform>().First();
        Opacity = 0d;
        rotate.Angle = -4d;
        translate.Y = 40d;
        ModAnimation.AniStart(
        new List<ModAnimation.AniData>
        {
            ModAnimation.AaOpacity(this, 1d, 120, 60),
            ModAnimation.AaDouble(value => translate.Y += value, -40d, 300, 60, new ModAnimation.AniEaseOutBack(ModAnimation.AniEasePower.Weak)),
            ModAnimation.AaDouble(value => rotate.Angle += value, 4d, 300, 60, new ModAnimation.AniEaseOutFluent(ModAnimation.AniEasePower.Weak))
        }, $"MyMsgBox {_uuid}");
    }

    private void ConfirmClick(object? sender, EventArgs e) =>
        Close(this.FindControl<ColorPicker>("Picker")!.Color);

    private void CancelClick(object? sender, EventArgs e) => Close(null);

    private void Close(Color? color)
    {
        if (_isClosing)
            return;
        _isClosing = true;
        ModAnimation.AniStart(
        new List<ModAnimation.AniData>
        {
            ModAnimation.AaOpacity(this, -Opacity, 80, 20),
            ModAnimation.AaCode(() => Closed?.Invoke(this, new MyMsgColorClosedEventArgs(color)), after: true)
        }, $"MyMsgBox {_uuid}");
    }
}
