// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Fonts.Inter;
using Avalonia.Platform;
using PCL.Application.Settings;
using PCL.Core.Logging;
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
        bool completedNormally = false;
        UnhandledExceptionGuard.Install();
        try
        {
            if (LauncherUpdateBootstrap.TryRunUpdateHelper(args, out int updateExitCode))
            {
                completedNormally = true;
                return updateExitCode;
            }

            // NativeAOT cannot bundle dynamic Skia/LibVLC libraries into the
            // operating-system image. Install the signed, RID-specific payload
            // before Avalonia or any native-backed feature is initialized.
            PclEmbeddedNativeRuntime.EnsureInstalled();
            args = LauncherUpdateBootstrap.ProcessStartupCleanup(args);

            // The experimental JVM host is deliberately handled before settings, single-instance,
            // or Avalonia are initialized. It is a game process, not another launcher UI.
            if (MinecraftJvmHostProcessLauncher.TryGetRequestPath(args, out string jvmHostRequestPath))
            {
                int jvmHostExitCode = MinecraftJvmHostEntryPoint.Run(jvmHostRequestPath);
                completedNormally = true;
                return jvmHostExitCode;
            }

            SetLauncherWorkingDirectory();

            // Apply CI-embedded secrets (MS client id, etc.) before any auth/UI code runs.
            PclEmbeddedSecrets.ApplyToEnvironment();

            // pcln-paths.json is the durable marker that OOBE has selected the launcher's
            // data layout. If it is absent, start a clean OOBE process instead of silently
            // creating default data and accidentally reusing a partially initialized tree.
            // Explicit OOBE/resume and command-line validation modes must never relaunch.
            _ = PCL.Desktop.Paths.LauncherPathLayout.Load(); // also migrates a legacy mapping
            if (ShouldRestartIntoOobe(
                    File.Exists(PCL.Desktop.Paths.LauncherPathLayout.OverrideFilePath),
                    args))
            {
                if (TryRestartLauncher([OobeConfiguration.ForceArgument]))
                {
                    completedNormally = true;
                    return 0;
                }

                // A locked-down platform may reject process creation. Preserve first-run
                // correctness by continuing in this process with the same explicit mode.
                args = [.. args, OobeConfiguration.ForceArgument];
            }

            LauncherSettings startupSettings;
            try
            {
                startupSettings = LauncherSettingsPageBinder.LoadSettings();
            }
            catch (Exception settingsEx)
            {
                try
                {
                    DesktopFileLog.Initialize(PortableLogLevel.Info);
                    DesktopFileLog.Warn("Startup", "读取设置失败，使用空设置继续：" + settingsEx.Message, settingsEx);
                }
                catch
                {
                    // ignore
                }

                startupSettings = new LauncherSettings();
            }

            try
            {
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
            }
            catch (Exception logEx)
            {
                // Logging must never prevent launch.
                System.Diagnostics.Debug.WriteLine("[Startup] log init failed: " + logEx);
            }

            if (args.Contains("--validate-environment", StringComparer.OrdinalIgnoreCase))
            {
                int validationExitCode = ValidateEnvironment();
                completedNormally = true;
                return validationExitCode;
            }
            if (args.Contains("--validate-assets", StringComparer.OrdinalIgnoreCase))
            {
                int validationExitCode = ValidateAssets();
                completedNormally = true;
                return validationExitCode;
            }
            if (args.Contains("--validate-native-runtime", StringComparer.OrdinalIgnoreCase))
            {
                int validationExitCode = ValidateNativeRuntime();
                completedNormally = true;
                return validationExitCode;
            }
            if (args.Contains("--validate-secrets", StringComparer.OrdinalIgnoreCase))
            {
                int validationExitCode = PclEmbeddedSecrets.Count > 0 ? 0 : 2;
                completedNormally = true;
                return validationExitCode;
            }

            // OOBE: --oobe forces full flow; --oobe-resume after config-dir restart;
            // content version bumps drive short update flow.
            OobeConfiguration.ApplyCommandLine(args);
            if (OobeConfiguration.ResumeFromCommandLine)
                DesktopFileLog.Info("OOBE", "检测到 --oobe-resume，将进入 Welcome → 在线配置。");
            else if (OobeConfiguration.ForceFullFromCommandLine)
                DesktopFileLog.Info("OOBE", "检测到 --oobe，将强制完整 OOBE。");

            using SingleInstanceCoordinator singleInstance = SingleInstanceCoordinator.Create();
            DesktopFileLog.Info("SingleInstance", $"单实例检查完成；Primary={singleInstance.IsPrimaryInstance}。");
            if (!singleInstance.IsPrimaryInstance)
            {
                // Secondary exits immediately (no splash). If primary is a headless zombie holding
                // the mutex, the user only sees a flash — surface a recoverable hint.
                int code = singleInstance.SignalExistingInstance();
                try
                {
                    ShowSecondaryInstanceHint();
                }
                catch
                {
                    // ignore UI failures on secondary path
                }

                completedNormally = true;
                return code;
            }

            App.SingleInstanceCoordinator = singleInstance;
            singleInstance.StartListening();
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            completedNormally = true;
            return 0;
        }
        catch (Exception ex)
        {
            try
            {
                UnhandledExceptionGuard.Report(ex, "Program.Main", canContinue: false);
            }
            catch
            {
                // ignore
            }

            // Last resort: visible message when Avalonia never starts (looks like a silent flash-quit).
            ShowEarlyFailureMessage(ex);
            return 1;
        }
        finally
        {
            UnhandledExceptionGuard.CompleteSession(completedNormally);
        }
    }

    private static void ShowEarlyFailureMessage(Exception ex)
    {
        try
        {
            string text =
                "PCL N 启动失败，但已尽量避免静默闪退。\n\n" +
                ex.GetType().Name + ": " + ex.Message + "\n\n" +
                "若你删除了 pcln-paths.json，程序应回退到默认配置目录，而不是退出。\n" +
                "路径映射位置：%LocalAppData%\\PCL-N\\pcln-paths.json\n" +
                "日志：数据目录\\Logs 或 %LocalAppData%\\PCL-N\\Logs";
            if (OperatingSystem.IsWindows())
                ShowWindowsMessageBox(text, "PCL N 启动失败");
            else
                Console.Error.WriteLine(text);
        }
        catch
        {
            // ignore
        }
    }

    private static void ShowSecondaryInstanceHint()
    {
        string text =
            "已有 PCL N 实例在运行（或残留进程占用了单实例锁）。\n\n" +
            "• 若主窗口未出现：请打开任务管理器，结束 PCL-N-Edition 与 PCL.Plugin.Sidecar 后重试。\n" +
            "• 正常多开会被拒绝；请使用已打开的窗口。";
        if (OperatingSystem.IsWindows())
            ShowWindowsMessageBox(text, "PCL N 已在运行");
        else
            Console.Error.WriteLine(text);
    }

    [SupportedOSPlatform("windows")]
    private static void ShowWindowsMessageBox(string text, string caption) =>
        _ = MessageBoxW(IntPtr.Zero, text, caption, 0x00000040);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [SupportedOSPlatform("windows")]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

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

    internal static bool ShouldRestartIntoOobe(bool pathMappingExists, IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (pathMappingExists ||
            args.Contains(OobeConfiguration.ForceArgument, StringComparer.OrdinalIgnoreCase) ||
            args.Contains(OobeConfiguration.ResumeArgument, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        return !args.Any(static argument =>
            argument.StartsWith("--validate-", StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryRestartLauncher(IReadOnlyList<string> arguments)
    {
        string? processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath) || !File.Exists(processPath))
            return false;

        try
        {
            ProcessStartInfo start = new()
            {
                FileName = processPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = GetLauncherDirectory()
            };

            // Framework-dependent debug runs execute through dotnet; keep the managed entry
            // assembly before launcher arguments. Published/apphost builds need only arguments.
            if (string.Equals(
                    Path.GetFileNameWithoutExtension(processPath),
                    "dotnet",
                    StringComparison.OrdinalIgnoreCase))
            {
#pragma warning disable IL3000 // Framework-dependent debug path only; NativeAOT never executes this branch.
                string entryAssembly = typeof(Program).Assembly.Location;
#pragma warning restore IL3000
                if (!string.IsNullOrWhiteSpace(entryAssembly))
                    start.ArgumentList.Add(entryAssembly);
            }

            foreach (string argument in arguments)
                start.ArgumentList.Add(argument);

            if (Process.Start(start) is null)
                return false;

            try
            {
                DesktopFileLog.Info(
                    "OOBE",
                    $"未找到路径映射文件，已请求以 {OobeConfiguration.ForceArgument} 重启启动器。");
            }
            catch
            {
                // Logging is not initialized yet on the clean first-run path.
            }

            return true;
        }
        catch (Exception ex)
        {
            try
            {
                DesktopFileLog.Warn("OOBE", "全新运行重启失败，将在当前进程进入 OOBE。", ex);
            }
            catch
            {
                // Logging is best effort at this stage.
            }

            return false;
        }
    }

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

    private static int ValidateNativeRuntime()
    {
        try
        {
            // This is the first SkiaSharp call made by Avalonia's render bootstrap and
            // reproduces the Linux startup failure when libSkiaSharp cannot be resolved.
            _ = SkiaSharp.SKImageInfo.PlatformColorType;
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Native rendering runtime validation failed: " + ex);
            return 2;
        }
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
