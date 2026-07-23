// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using PCL.Desktop.Controls.Legacy;

namespace PCL.Desktop.Features.Settings.Views;

internal static partial class SetupPageRegistry
{
    public static partial bool IsDefined(SetupPageSubType page);

    public static partial MyPageRight CreatePage(SetupPageSubType page);

    public static partial string GetTitle(SetupPageSubType page);

    [SetupPage(SetupPageSubType.Launch, "启动选项")]
    private static PageSetupLaunch CreateLaunchPage() => new();

    [SetupPage(SetupPageSubType.Ui, "外观")]
    private static PageSetupUI CreateUiPage() => new();

    [SetupPage(SetupPageSubType.GameManage, "游戏数据")]
    private static PageSetupGameManage CreateGameManagePage() => new();

    [SetupPage(SetupPageSubType.About, "关于")]
    private static PageSetupAbout CreateAboutPage() => new();

    [SetupPage(SetupPageSubType.Log, "诊断日志")]
    private static PageSetupLog CreateLogPage() => new();

    [SetupPage(SetupPageSubType.Update, "软件更新")]
    private static PageSetupUpdate CreateUpdatePage() => new();

    [SetupPage(SetupPageSubType.Java, "Java")]
    private static PageSetupJava CreateJavaPage() => new();

    [SetupPage(SetupPageSubType.LauncherMisc, "通用")]
    private static PageSetupLauncherMisc CreateLauncherMiscPage() => new();

    [SetupPage(SetupPageSubType.LauncherLanguage, "语言")]
    private static PageSetupLauncherLanguage CreateLauncherLanguagePage() => new();

    [SetupPage(SetupPageSubType.Experimental, "实验性功能")]
    private static PageSetupExperimental CreateExperimentalPage() => new();

}
