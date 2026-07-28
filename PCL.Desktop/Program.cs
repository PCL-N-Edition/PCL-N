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
using PCL.Desktop.Views.FirstRun;

namespace PCL.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (LauncherUpdateBootstrap.TryRunUpdateHelper(args, out int updateExitCode))
            return updateExitCode;
        args = LauncherUpdateBootstrap.ProcessStartupCleanup(args);

        // Catch process-wide crashes as early as possible.
        UnhandledExceptionGuard.Install();

        try
        {
            // The experimental JVM host is deliberately handled before settings, single-instance,
            // or Avalonia are initialized. It is a game process, not another launcher UI.
            if (MinecraftJvmHostProcessLauncher.TryGetRequestPath(args, out string jvmHostRequestPath))
                return MinecraftJvmHostEntryPoint.Run(jvmHostRequestPath);

            SetLauncherWorkingDirectory();

            // Apply CI-embedded secrets (MS client id, etc.) before any auth/UI code runs.
            PclEmbeddedSecrets.ApplyToEnvironment();
            // Path layout must resolve next to the real executable before any settings I/O.
            LauncherSettings startupSettings = LauncherSettingsPageBinder.LoadSettings();
            DesktopFileLog.Initialize(DesktopFileLog.LevelFromSetting(startupSettings.GetIntegerOption(
                "SystemLogLevel",
                LauncherSettingDefaults.GetInteger("SystemLogLevel"))));
            DesktopTraceLogBridge.Install();
            DesktopFileLog.Info(
                "Startup",
                $"进程入口已执行；参数数量={args.Length}；日志级别={DesktopFileLog.Level}；" +
                $"启动器目录={GetLauncherDirectory()}；BaseDirectory={AppContext.BaseDirectory}；" +
                $"路径覆盖={PCL.Desktop.Paths.LauncherPathLayout.OverrideFilePath}；" +
                $"数据目录={PCL.Desktop.Paths.LauncherPathLayout.ResolveDataDirectory()}；" +
                $"设置文件={LauncherSettingsPageBinder.CreateSettingsPath()}；" +
                $"工作目录={Environment.CurrentDirectory}。");
            DesktopFileLog.Debug("Startup", "命令行参数：" + string.Join(' ', args));

            if (args.Contains("--validate-environment", StringComparer.OrdinalIgnoreCase))
                return ValidateEnvironment();
            if (args.Contains("--validate-assets", StringComparer.OrdinalIgnoreCase))
                return ValidateAssets();
            if (args.Contains("--validate-secrets", StringComparer.OrdinalIgnoreCase))
                return PclEmbeddedSecrets.Count > 0 ? 0 : 2;

            // OOBE: --oobe forces full flow; content version bumps drive short update flow.
            OobeConfiguration.ApplyCommandLine(args);
            if (OobeConfiguration.ForceFullFromCommandLine)
                DesktopFileLog.Info("OOBE", "检测到 --oobe，将强制完整 OOBE。");

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
        LauncherSettings settings;
        try
        {
            settings = LauncherSettingsPageBinder.LoadSettings();
        }
        catch
        {
            settings = new LauncherSettings();
        }

        AppBuilder builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont();

        // GPU / Skia path (ANGLE·Vulkan·GL first; software fallback). Honors SystemDisableHardwareAcceleration.
        builder = DesktopRenderBootstrap.Configure(builder, settings);

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

    internal static string GetLauncherDirectory() =>
        PCL.Desktop.Paths.LauncherPathLayout.GetHostDirectory();

    private static void SetLauncherWorkingDirectory()
    {
        string launcherDirectory = GetLauncherDirectory();
        Environment.CurrentDirectory = launcherDirectory;
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
