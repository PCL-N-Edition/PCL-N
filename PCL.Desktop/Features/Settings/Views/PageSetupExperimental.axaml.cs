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
}
