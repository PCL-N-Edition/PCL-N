// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using PCL.Application.Settings;
using PCL.Desktop.Controls.Legacy;
using PCL.Desktop.Diagnostics;
using PCL.Desktop.Localization;

namespace PCL.Desktop.Features.Settings.Views;

public partial class PageSetupCompatibility : MyPageRight, ISettingsPageInteractionSource
{
    private StackPanel? _panProbeItems;
    private TextBlock? _labProbeSummary;

    public PageSetupCompatibility()
    {
        AvaloniaXamlLoader.Load(this);
        PanScroll = PanBack;
        _panProbeItems = this.FindControl<StackPanel>("PanProbeItems");
        _labProbeSummary = this.FindControl<TextBlock>("LabProbeSummary");
        LauncherSettingsPageBinder.Attach(this);
        Loaded += (_, _) => RefreshProbeUi(LauncherCompatibilityProbe.LastReport ?? LauncherCompatibilityProbe.Run());
    }

#pragma warning disable CS0067 // Required by ISettingsPageInteractionSource; host may wire later.
    public event EventHandler<SettingsPathRequestedEventArgs>? OpenPathRequested;
    public event EventHandler<SettingsUrlRequestedEventArgs>? OpenUrlRequested;
    public event EventHandler<SettingsConfirmRequestedEventArgs>? ConfirmRequested;
#pragma warning restore CS0067
    public event EventHandler<SettingsMessageRequestedEventArgs>? MessageRequested;

    private void BtnRunProbe_Click(object? sender, EventArgs e) =>
        RefreshProbeUi(LauncherCompatibilityProbe.Run());

    private void CheckDisableGpu_OnChange(object? sender, bool user)
    {
        if (!user)
            return;
        MessageRequested?.Invoke(
            this,
            new SettingsMessageRequestedEventArgs(
                AvaloniaLocalizationManager.GetText("Setup.Left.Item.Compatibility", "兼容性"),
                AvaloniaLocalizationManager.GetText(
                    "Setup.Compat.Options.Gpu.Restart",
                    "硬件加速设置将在下次启动时生效。")));
    }

    private static void CheckDisableAnimations_OnChange(object? sender, bool user)
    {
        if (!user || sender is not MyCheckBox box)
            return;
        try
        {
            ModAnimation.AniControlEnabled = box.Checked == true ? 1 : 0;
        }
        catch
        {
            // ignore
        }
    }

    private void RefreshProbeUi(CompatibilityReport report)
    {
        if (_labProbeSummary is not null)
        {
            if (report.CanRun)
            {
                string template = AvaloniaLocalizationManager.GetText(
                    "Setup.Compat.Probe.Summary.Ok",
                    "最近检测：{0} 项正常，{1} 项需注意。启动器可以运行。");
                try
                {
                    _labProbeSummary.Text = string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        template,
                        report.OkCount,
                        report.IssueCount);
                }
                catch (FormatException)
                {
                    _labProbeSummary.Text =
                        $"最近检测：{report.OkCount} 项正常，{report.IssueCount} 项需注意。启动器可以运行。";
                }
            }
            else
            {
                _labProbeSummary.Text = AvaloniaLocalizationManager.GetText(
                    "Setup.Compat.Probe.Summary.Blocked",
                    "最近检测：存在致命依赖故障。本软件在此环境下不可用。");
            }
        }

        if (_panProbeItems is null)
            return;
        _panProbeItems.Children.Clear();
        foreach (CompatibilityCheckItem item in report.Items)
        {
            string badge = LauncherCompatibilityProbe.StatusLabel(item.Status);
            string color = item.Status switch
            {
                CompatibilityStatus.Ok => "#FF2E7D32",
                CompatibilityStatus.Degraded => "#FFF9A825",
                CompatibilityStatus.Unavailable => "#FFEF6C00",
                _ => "#FFC62828"
            };
            _panProbeItems.Children.Add(new Border
            {
                Padding = new Thickness(12, 10),
                CornerRadius = new CornerRadius(10),
                Background = new SolidColorBrush(Color.Parse("#10000000")),
                Child = new StackPanel
                {
                    Spacing = 4,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = $"[{badge}] {item.Title}",
                            FontWeight = FontWeight.SemiBold,
                            Foreground = new SolidColorBrush(Color.Parse(color))
                        },
                        new TextBlock
                        {
                            Text = item.Detail,
                            TextWrapping = TextWrapping.Wrap,
                            Opacity = 0.82
                        }
                    }
                }
            });
        }
    }
}
