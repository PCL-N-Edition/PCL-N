// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using PCL.Application.Hosting.RuntimeExtensions;
using PCL.Desktop.Controls.Legacy;
using PCL.Desktop.Hosting.PluginSidecar;
using PCL.Desktop.Localization;

namespace PCL.Desktop.Features.Settings.Views;

internal sealed class FeedbackSubmissionWindow : Window
{
    private readonly MyComboBox _category;
    private readonly MyTextBox _title;
    private readonly MyTextBox _description;
    private readonly TextBlock _typeHint;
    private readonly TextBlock _validation;
    private readonly IReadOnlyList<PluginSidecarIssueCategoryDto> _categories;
    private string? _lastAppliedTemplate;

    public FeedbackSubmissionWindow(IReadOnlyList<PluginSidecarIssueCategoryDto> categories)
    {
        ArgumentNullException.ThrowIfNull(categories);
        _categories = categories.Count > 0
            ? categories
            : new[]
            {
                new PluginSidecarIssueCategoryDto
                {
                    Id = "bug",
                    Title = "Bug 反馈",
                    Description = "启动器功能相关 Bug",
                    IssueType = "Bug",
                    Labels = ["bug"],
                    BodyTemplate = "### 描述\n\n"
                }
            };

        Title = Text("Setup.Feedback.Compose.Title", "新建反馈");
        Width = 720d;
        Height = 620d;
        MinWidth = 540d;
        MinHeight = 500d;
        CanResize = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _category = new MyComboBox
        {
            Name = "ComboFeedbackType",
            Height = 34d,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = _categories.Select(static c => new MyComboBoxItem
            {
                Content = c.Title,
                Tag = c.Id
            }).ToArray(),
            SelectedIndex = 0
        };
        _category.SelectionChanged += (_, _) => ApplyTemplateForSelection(force: false);

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
                "请按模板填写（至少 20 个字符）"),
            MaxLength = 10_000,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalContentAlignment = VerticalAlignment.Top,
            MinHeight = 280d,
            Padding = new Thickness(8d)
        };
        _typeHint = new TextBlock
        {
            Opacity = 0.75d,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12.5d
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
                            "在线服务仅用于确认登录状态并提交认证请求。请勿填写令牌、密码或完整账户信息。提交后会按类型自动打上仓库标签（如 Bug、崩溃、新功能、优化、等待处理）。"),
                        Opacity = 0.75d,
                        TextWrapping = TextWrapping.Wrap
                    },
                    new TextBlock { Text = Text("Setup.Feedback.Compose.Type", "类型") },
                    _category,
                    _typeHint,
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

        ApplyTemplateForSelection(force: true);
    }

    private PluginSidecarIssueCategoryDto CurrentCategory()
    {
        int index = Math.Clamp(_category.SelectedIndex, 0, _categories.Count - 1);
        return _categories[index];
    }

    private void ApplyTemplateForSelection(bool force)
    {
        PluginSidecarIssueCategoryDto category = CurrentCategory();
        string labels = category.Labels is { Length: > 0 }
            ? string.Join(", ", category.Labels)
            : "—";
        _typeHint.Text =
            $"{category.Description}\nIssue Type: {category.IssueType} · Labels: {labels}";

        string template = (category.BodyTemplate ?? "").Trim();
        if (template.Length == 0)
            return;

        string current = _description.Text?.Trim() ?? "";
        if (force ||
            string.IsNullOrWhiteSpace(current) ||
            string.Equals(current, _lastAppliedTemplate, StringComparison.Ordinal))
        {
            _description.Text = template;
            _lastAppliedTemplate = template;
        }
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

        PluginSidecarIssueCategoryDto category = CurrentCategory();
        Close(new HostFeedbackDraft(category.Id, title, description));
    }

    private void ShowValidation(string message)
    {
        _validation.Text = message;
        _validation.IsVisible = true;
    }

    private static string Text(string key, string fallback) =>
        AvaloniaLocalizationManager.GetText(key, fallback);
}
