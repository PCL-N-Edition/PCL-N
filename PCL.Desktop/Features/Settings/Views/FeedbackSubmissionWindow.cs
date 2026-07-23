// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using PCL.Application.Hosting.RuntimeExtensions;
using PCL.Desktop.Controls.Legacy;
using PCL.Desktop.Localization;

namespace PCL.Desktop.Features.Settings.Views;

internal sealed class FeedbackSubmissionWindow : Window
{
    private static readonly string[] Categories = ["bug", "game_crash", "feature", "improvement", "feedback"];
    private readonly MyComboBox _category;
    private readonly MyTextBox _title;
    private readonly MyTextBox _description;
    private readonly TextBlock _validation;

    public FeedbackSubmissionWindow()
    {
        Title = Text("Setup.Feedback.Compose.Title", "新建反馈");
        Width = 680d;
        Height = 560d;
        MinWidth = 520d;
        MinHeight = 460d;
        CanResize = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _category = new MyComboBox
        {
            Name = "ComboFeedbackType",
            Height = 34d,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = new MyComboBoxItem[]
            {
                new() { Content = Text("Setup.Feedback.Compose.Type.Bug", "启动器 Bug") },
                new() { Content = Text("Setup.Feedback.Compose.Type.Crash", "Minecraft 崩溃") },
                new() { Content = Text("Setup.Feedback.Compose.Type.Feature", "功能建议") },
                new() { Content = Text("Setup.Feedback.Compose.Type.Improvement", "改进建议") },
                new() { Content = Text("Setup.Feedback.Compose.Type.Other", "其他反馈") }
            },
            SelectedIndex = 0
        };
        _title = new MyTextBox
        {
            Name = "TextFeedbackTitle",
            HintText = Text("Setup.Feedback.Compose.TitleHint", "简要标题（8～160 个字符）"),
            MaxLength = 160,
            Height = 36d
        };
        _description = new MyTextBox
        {
            Name = "TextFeedbackDescription",
            HintText = Text(
                "Setup.Feedback.Compose.DescriptionHint",
                "请描述现象、复现步骤、预期行为和必要环境信息（至少 20 个字符）"),
            MaxLength = 10_000,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalContentAlignment = VerticalAlignment.Top,
            MinHeight = 240d,
            Padding = new Thickness(8d)
        };
        _validation = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(210, 65, 65)),
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false
        };

        MyButton submit = new()
        {
            Name = "BtnFeedbackSubmit",
            Text = Text("Setup.Feedback.Compose.Submit", "提交"),
            Width = 110d,
            Height = 34d,
            ColorType = MyButton.ColorState.Highlight
        };
        MyButton cancel = new()
        {
            Name = "BtnFeedbackCancel",
            Text = Text("Common.Action.Cancel", "取消"),
            Width = 90d,
            Height = 34d,
            Margin = new Thickness(8d, 0d, 0d, 0d)
        };
        submit.Click += (_, _) => Submit();
        cancel.Click += (_, _) => Close(null);

        Content = new MyScrollViewer
        {
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = new StackPanel
            {
                Margin = new Thickness(24d),
                Spacing = 10d,
                Children =
                {
                    new TextBlock
                    {
                        Text = Text("Setup.Feedback.Compose.Heading", "在启动器内提交 Issue"),
                        FontSize = 18d,
                        FontWeight = FontWeight.SemiBold
                    },
                    new TextBlock
                    {
                        Text = Text(
                            "Setup.Feedback.Compose.Privacy",
                            "PCL.Plugin 仅负责确认登录状态并提交认证请求。请勿填写令牌、密码或完整账户信息。"),
                        Opacity = 0.75d,
                        TextWrapping = TextWrapping.Wrap
                    },
                    new TextBlock { Text = Text("Setup.Feedback.Compose.Type", "类型") },
                    _category,
                    new TextBlock { Text = Text("Setup.Feedback.Compose.Subject", "标题") },
                    _title,
                    new TextBlock { Text = Text("Setup.Feedback.Compose.Description", "详细描述") },
                    _description,
                    _validation,
                    new WrapPanel
                    {
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { submit, cancel }
                    }
                }
            }
        };
    }

    private void Submit()
    {
        string title = _title.Text?.Trim() ?? string.Empty;
        string description = _description.Text?.Trim() ?? string.Empty;
        if (title.Length is < 8 or > 160)
        {
            ShowValidation(Text("Setup.Feedback.Compose.TitleValidation", "标题长度必须为 8～160 个字符。"));
            return;
        }
        if (description.Length is < 20 or > 10_000)
        {
            ShowValidation(Text(
                "Setup.Feedback.Compose.DescriptionValidation",
                "详细描述长度必须为 20～10000 个字符。"));
            return;
        }

        int categoryIndex = Math.Clamp(_category.SelectedIndex, 0, Categories.Length - 1);
        Close(new HostFeedbackDraft(Categories[categoryIndex], title, description));
    }

    private void ShowValidation(string message)
    {
        _validation.Text = message;
        _validation.IsVisible = true;
    }

    private static string Text(string key, string fallback) =>
        AvaloniaLocalizationManager.GetText(key, fallback);
}
