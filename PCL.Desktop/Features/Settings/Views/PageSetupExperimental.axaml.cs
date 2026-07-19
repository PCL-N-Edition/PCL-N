// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Avalonia.Markup.Xaml;
using PCL.Desktop.Controls.Legacy;
using PCL.Desktop.Localization;

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
            "启用 0.5B 本地错误修复模型");
        string message = AvaloniaLocalizationManager.GetText(
            "Setup.Experimental.AiRepair.Confirm.Message",
            "优势：游戏崩溃后会先由常规分析器定位；分析结果由本地小模型整理为更易读的说明。当常规分析器无法决定安全修复时，模型可从错误处理器提供的白名单动作中选择建议。所有推理均在本机完成，不会上传日志。\n\n" +
            "危害：首次分析会下载约 491 MB 的 Qwen2.5-Coder 0.5B Q4_K_M 模型和约 10–17 MB 的运行时；推理通常额外占用约 0.8–1.5 GB 内存并消耗 CPU，低配置设备可能明显变慢；模型可能判断错误。它不能执行命令，也不能越过常规错误处理器的修复白名单。Windows、Linux 与 macOS 的 x64/arm64 均可自动安装经过 SHA-256 校验的运行时，并优先使用大陆可用镜像。\n\n" +
            "可在此页指定自定义 GGUF、可选 SHA-256 和 llama.cpp 路径。模型文件会保存到 PCL N 的应用数据目录。是否理解下载、性能和误判风险并继续？");

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
}
