// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using PCL.Application.Settings;
using PCL.Desktop.Controls.Legacy;
using PCL.Desktop.Features.Launching;
using PCL.Desktop.Localization;
using PCL.Platform.Abstractions.Security;

#pragma warning disable CS0067

namespace PCL.Desktop.Features.Settings.Views;

public partial class PageSetupExperimental : MyPageRight, ISettingsPageInteractionSource
{
    public PageSetupExperimental()
    {
        AvaloniaXamlLoader.Load(this);
        PanScroll = PanBack;
        LauncherSettingsPageBinder.Attach(this);
    }

    public event EventHandler<SettingsPathRequestedEventArgs>? OpenPathRequested;

    public event EventHandler<SettingsUrlRequestedEventArgs>? OpenUrlRequested;

    public event EventHandler<SettingsMessageRequestedEventArgs>? MessageRequested;

    public event EventHandler<SettingsConfirmRequestedEventArgs>? ConfirmRequested;

    private void ExperimentalJvmHost_OnPreviewChange(object sender, RouteEventArgs e)
    {
        // Stop the state transition until the warning dialog has been answered. This also
        // prevents the generic settings binder from persisting an unconfirmed opt-in.
        e.Handled = true;
        if (sender is not MyCheckBox checkBox)
            return;

        string title = AvaloniaLocalizationManager.GetText(
            "Setup.Experimental.JvmHost.Confirm.Title",
            "启用 Jvm.NET 生命周期 Host");
        string message = AvaloniaLocalizationManager.GetText(
            "Setup.Experimental.JvmHost.Confirm.Message",
            "优势：可获得更完整的 JVM 与 Minecraft 生命周期日志；第三方认证不再加载 authlib-injector；离线档案可在游戏内使用本地皮肤；Host 崩溃不会拖垮启动器。\n\n" +
            "危害：该功能会在独立进程内嵌 JVM 并修改 authlib 字节码，可能与部分 Java、模组或认证服务器不兼容；会增加少量启动耗时与内存；认证和皮肤在本次游戏会话中经过 127.0.0.1 本地桥接；第三方纹理签名由 PCL N 验证后再转换，行为与 authlib-injector 并不完全相同。遇到问题请关闭此选项回到传统启动链路。\n\n" +
            "每个 Host 只运行一个 JVM，退出游戏后会一并结束。是否理解风险并继续？");

        void Complete(bool confirmed)
        {
            if (confirmed)
                checkBox.SetChecked(true, user: false);
        }

        SettingsConfirmRequestedEventArgs args = new(
            title,
            message,
            Complete,
            primaryButton: AvaloniaLocalizationManager.GetText(
                "Setup.Experimental.JvmHost.Confirm.Enable",
                "理解风险并启用"),
            secondaryButton: AvaloniaLocalizationManager.GetText("Common.Action.Cancel", "取消"),
            isWarn: true);
        if (ConfirmRequested is { } requested)
            requested.Invoke(this, args);
    }

    private void ExperimentalAiRepair_OnPreviewChange(object sender, RouteEventArgs e)
    {
        e.Handled = true;
        if (sender is not MyCheckBox checkBox)
            return;

        string title = AvaloniaLocalizationManager.GetText(
            "Setup.Experimental.AiRepair.Confirm.Title",
            "启用 Minecraft AI 错误修复");
        string message = AvaloniaLocalizationManager.GetText(
            "Setup.Experimental.AiRepair.Confirm.Message",
            "优势：游戏崩溃后仍由常规分析器先定位。模型可生成易读说明，并在错误处理器的白名单内给出最多四步的链式修复计划；本地模式会优先使用 GPU，也可连接用户配置的 OpenAI 兼容 API。\n\n" +
            "危害：内置模型首次使用需下载约 491 MB 模型和约 10–37 MB 运行时，通常额外占用约 0.8–1.5 GB 内存；模型可能误判。在线模式会把模型明确请求的、经过脱敏和限长的运行环境、实例 metadata、崩溃报告、运行日志、启动方式或登录方式发送给配置的服务商。账户名、UUID、令牌、密码、API Key、完整本地路径和服务器地址不会发送。\n\n" +
            "模型不能执行命令、任意读取文件或越过修复白名单。thinking 内容不会写入日志或展示；仅保留阶段、进度和可审计依据。是否理解下载、性能、隐私和误判风险并继续？");

        void Complete(bool confirmed)
        {
            if (confirmed)
                checkBox.SetChecked(true, user: false);
        }

        SettingsConfirmRequestedEventArgs args = new(
            title,
            message,
            Complete,
            primaryButton: AvaloniaLocalizationManager.GetText(
                "Setup.Experimental.AiRepair.Confirm.Enable",
                "理解风险并启用"),
            secondaryButton: AvaloniaLocalizationManager.GetText("Common.Action.Cancel", "取消"),
            isWarn: true);
        if (ConfirmRequested is { } requested)
            requested.Invoke(this, args);
    }

    private async void ApiKey_OnLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is not TextBox { Text: { } value } textBox || string.IsNullOrWhiteSpace(value))
            return;
        SecureStorageOperationResult result = await MinecraftAiApiCredentialStore.WriteAsync(value.Trim());
        textBox.Text = string.Empty;
        if (this.FindControl<TextBlock>("AiApiKeyStatus") is { } status)
        {
            status.Text = result.IsSuccess
                ? AvaloniaLocalizationManager.GetText(
                    "Setup.Experimental.AiRepair.Api.Key.Saved",
                    "API Key 已保存到系统安全存储，不会在页面中回显。")
                : AvaloniaLocalizationManager.GetText(
                    "Setup.Experimental.AiRepair.Api.Key.SaveFailed",
                    "API Key 保存失败：") + (result.Message ?? result.Status.ToString());
        }
    }

    private void PersistAiApiTextOption(object? sender, RoutedEventArgs e)
    {
        if (sender is not MyTextBox { Tag: { } tag } textBox)
            return;
        LauncherSettings settings = LauncherSettingsPageBinder.LoadSettings();
        settings.SetTextOption(tag.ToString()!, textBox.Text ?? string.Empty);
        LauncherSettingsPageBinder.SaveSettings(settings);
    }

    private async void ClearApiKey_OnClick(object? sender, EventArgs e)
    {
        SecureStorageOperationResult result = await MinecraftAiApiCredentialStore.DeleteAsync();
        if (this.FindControl<TextBox>("TextAiApiKey") is { } apiKey)
            apiKey.Text = string.Empty;
        if (this.FindControl<TextBlock>("AiApiKeyStatus") is { } status)
        {
            status.Text = result.IsSuccess
                ? AvaloniaLocalizationManager.GetText(
                    "Setup.Experimental.AiRepair.Api.Key.Cleared",
                    "已清除保存的 API Key。")
                : AvaloniaLocalizationManager.GetText(
                    "Setup.Experimental.AiRepair.Api.Key.ClearFailed",
                    "API Key 清除失败：") + (result.Message ?? result.Status.ToString());
        }
    }
}
