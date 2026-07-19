// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Avalonia;
using Avalonia.Fonts.Inter;
using Avalonia.Platform;
using PCL.Application.Settings;
using PCL.Desktop.Diagnostics;
using PCL.Desktop.Features.Launching;
using PCL.Desktop.Features.Settings.Views;
using PCL.Desktop.Hosting;
using PCL.Desktop.Platform;

namespace PCL.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Catch process-wide crashes as early as possible.
        UnhandledExceptionGuard.Install();

        try
        {
            // The experimental JVM host is deliberately handled before settings, single-instance,
            // plugins, or Avalonia are initialized. It is a game process, not another launcher UI.
            if (MinecraftJvmHostProcessLauncher.TryGetRequestPath(args, out string jvmHostRequestPath))
                return MinecraftJvmHostEntryPoint.Run(jvmHostRequestPath);

            // Apply CI-embedded secrets (MS client id, etc.) before any auth/UI code runs.
            PclEmbeddedSecrets.ApplyToEnvironment();
            LauncherSettings startupSettings = LauncherSettingsPageBinder.LoadSettings();
            DesktopFileLog.Initialize(DesktopFileLog.LevelFromSetting(startupSettings.GetIntegerOption(
                "SystemLogLevel",
                LauncherSettingDefaults.GetInteger("SystemLogLevel"))));
            DesktopTraceLogBridge.Install();
            DesktopFileLog.Info("Startup", $"进程入口已执行；参数数量={args.Length}；日志级别={DesktopFileLog.Level}。");
            DesktopFileLog.Debug("Startup", "命令行参数：" + string.Join(' ', args));

            if (args.Contains("--validate-environment", StringComparer.OrdinalIgnoreCase))
                return ValidateEnvironment();
            if (args.Contains("--validate-assets", StringComparer.OrdinalIgnoreCase))
                return ValidateAssets();
            if (args.Contains("--validate-secrets", StringComparer.OrdinalIgnoreCase))
                return PclEmbeddedSecrets.Count > 0 ? 0 : 2;
            if (args.Contains("--validate-plugin", StringComparer.OrdinalIgnoreCase))
            {
                DesktopHost.Initialize();
                return DesktopHost.Current.ModuleIds.Count > 0 && DesktopHost.Current.SettingsPages.Pages.Count > 0
                    ? 0
                    : 1;
            }

            EmbeddedRuntimeExtensionLoader.Load();
            DesktopFileLog.Debug("Startup", "嵌入式运行时扩展预加载完成。");

            using SingleInstanceCoordinator singleInstance = SingleInstanceCoordinator.Create();
            DesktopFileLog.Info("SingleInstance", $"单实例检查完成；Primary={singleInstance.IsPrimaryInstance}。");
            if (!singleInstance.IsPrimaryInstance)
                return singleInstance.SignalExistingInstance();

            App.SingleInstanceCoordinator = singleInstance;
            singleInstance.StartListening();
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return 0;
        }
        catch (Exception ex)
        {
            UnhandledExceptionGuard.Report(ex, "Program.Main", canContinue: false);
            return 1;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        AppBuilder builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont();

        if (DesktopDisplayBackendSelector.ShouldUseWaylandForCurrentProcess())
        {
            DesktopFileLog.Info("Startup", "检测到原生 Linux Wayland 会话，将使用 Wayland 显示后端。");
            builder = builder.UseWayland();
        }
        else
        {
            DesktopFileLog.Info("Startup", "未启用 Wayland 显示后端，将使用当前平台的默认显示后端。");
        }

        return builder.LogToTrace();
    }

    private static int ValidateEnvironment()
    {
        return DesktopPlatformApi.IsSupportedDesktopPlatform
            ? 0
            : 1;
    }

    private static int ValidateAssets()
    {
        var assetLoader = new StandardAssetLoader(typeof(Program).Assembly);
        return ValidateResource(assetLoader, "avares://PCL.Desktop/Assets/icon.png") &&
               ValidateResource(assetLoader, "avares://PCL.Desktop/Assets/icon.ico") &&
               ValidateResource(assetLoader, "avares://PCL.Desktop/Assets/Legacy/icon.png")
            ? 0
            : 1;
    }

    private static bool ValidateResource(StandardAssetLoader assetLoader, string resourceUri)
    {
        var uri = new Uri(resourceUri, UriKind.Absolute);
        if (assetLoader.Exists(uri))
            return true;

        Console.Error.WriteLine($"Missing Avalonia resource: {resourceUri}");
        return false;
    }
}
