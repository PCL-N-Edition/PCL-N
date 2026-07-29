// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using PCL.Desktop.Composition;
using PCL.Desktop.Hosting;
using PCL.Desktop.Localization;
using PCL.Desktop.Platform;
using PCL.Desktop.Theme;
using PCL.Desktop.Views;
using PCL.Desktop.Views.FirstRun;
using PCL.Desktop.Features.Settings.Views;
using PCL.Application.Settings;
using PCL.Desktop.Diagnostics;

namespace PCL.Desktop;

public sealed partial class App : Avalonia.Application
{
    /// <summary>Max time to wait on splash for plugin sidecar (missing/fail still proceeds).</summary>
    private static readonly TimeSpan PluginWarmupTimeout = TimeSpan.FromSeconds(25);

    private SplashWindow? _splashWindow;

    internal static SingleInstanceCoordinator? SingleInstanceCoordinator { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // UI-thread unhandled exceptions (Avalonia Dispatcher) → crash dialog + Issue prompt.
        UnhandledExceptionGuard.AttachUiDispatcher();

        try
        {
            LauncherSettings settings = LauncherSettingsPageBinder.LoadSettings();
            DesktopFileLog.Initialize(DesktopFileLog.LevelFromSetting(settings.GetIntegerOption(
                "SystemLogLevel",
                LauncherSettingDefaults.GetInteger("SystemLogLevel"))));
            DesktopFileLog.Info("Startup", $"启动器设置读取完成；日志级别={DesktopFileLog.Level}。");
            AvaloniaThemeManager.Apply(settings);
            AvaloniaLocalizationManager.InitializeFromSettings(settings);
            DesktopFileLog.Info("Startup", $"主题与语言初始化完成；语言={AvaloniaLocalizationManager.CurrentLanguageCode}。");
            DesktopHost.Initialize();
            DesktopFileLog.Info("DesktopHost", $"桌面宿主初始化完成；模块数={DesktopHost.Current.ModuleIds.Count}。");
            DesktopCompositionRoot.Initialize();
            DesktopFileLog.Info("Startup", "DesktopCompositionRoot 初始化完成（Shell/MVVM 组合根）。");

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                // Headless tests host their own windows. First-run/debug notice switches must not
                // suppress the actual launcher window: PCL_DISABLE_DEBUG_HINT is documented to users.
                bool skipShell = ShouldSkipDesktopShell(Environment.GetEnvironmentVariable);

                if (!skipShell)
                {
                    // Critical: while splash is the only window, closing it must NOT exit the process
                    // (Avalonia default OnLastWindowClose caused flash-quit after OOBE/main handoff).
                    desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

                    desktop.Exit += (_, _) =>
                    {
                        DesktopHost.ShutdownOptionalRuntime();
                        LauncherUpdateCoordinator.Current.Dispose();
                    };
                    bool showSplash = settings.GetBooleanOption(
                        "UiLauncherLogo",
                        LauncherSettingDefaults.GetBoolean("UiLauncherLogo"));
                    bool runOobe = FirstRunWizardWindow.NeedsWizard(settings);

                    // Always show splash (when enabled) while plugin platform is probed.
                    if (showSplash)
                    {
                        _splashWindow = new SplashWindow();
                        // Keep a MainWindow reference so the lifetime has an owner during warm-up.
                        desktop.MainWindow = _splashWindow;
                        _splashWindow.Show();
                        DesktopFileLog.Info("Startup", "启动图标已显示；等待插件功能就绪或确认不可用。");
                    }
                    else
                    {
                        DesktopFileLog.Info("Startup", "启动图标已关闭；仍等待插件功能就绪或确认不可用后再进入主页。");
                    }

                    if (runOobe)
                    {
                        OobeRunPlan plan = OobeConfiguration.CreateRunPlan(settings);
                        _ = EnterOobeAfterPluginReadyAsync(desktop, plan, showSplash);
                    }
                    else
                    {
                        _ = EnterMainShellAfterPluginReadyAsync(desktop, showSplash);
                    }
                }
            }

            base.OnFrameworkInitializationCompleted();
        }
        catch (Exception ex)
        {
            UnhandledExceptionGuard.Report(ex, "App.OnFrameworkInitializationCompleted", canContinue: false);
            throw;
        }
    }

    internal static bool ShouldSkipDesktopShell(Func<string, string?> getEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);
        return !string.IsNullOrWhiteSpace(getEnvironmentVariable("PCL_DISABLE_DESKTOP_SHELL"));
    }

    /// <summary>
    /// Splash-time gate: load plugin sidecar until ready / not present / failed / timeout, then open main shell.
    /// </summary>
    private async Task EnterMainShellAfterPluginReadyAsync(
        IClassicDesktopStyleApplicationLifetime desktop,
        bool fadeSplash)
    {
        await WaitForPluginOptionalRuntimeAsync("main").ConfigureAwait(false);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            ShowMainWindow(desktop, fadeSplash);
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
        });
    }

    /// <summary>
    /// Splash-time gate before OOBE so online/plugin pages can use a warmed sidecar.
    /// </summary>
    private async Task EnterOobeAfterPluginReadyAsync(
        IClassicDesktopStyleApplicationLifetime desktop,
        OobeRunPlan plan,
        bool hadSplash)
    {
        await WaitForPluginOptionalRuntimeAsync("oobe").ConfigureAwait(false);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            FirstRunWizardWindow wizard = new(plan);
            wizard.PrepareCentered();

            // Capture splash geometry BEFORE showing/closing so we never end up with zero windows.
            PixelRect? splashBounds = null;
            if (hadSplash && _splashWindow is { } splashMeasure)
            {
                try
                {
                    PixelPoint pos = splashMeasure.Position;
                    double scale = splashMeasure.RenderScaling > 0 ? splashMeasure.RenderScaling : 1;
                    int w = (int)Math.Round(Math.Max(splashMeasure.Bounds.Width, 136) * scale);
                    int h = (int)Math.Round(Math.Max(splashMeasure.Bounds.Height, 136) * scale);
                    splashBounds = new PixelRect(pos.X, pos.Y, w, h);
                }
                catch (Exception ex)
                {
                    DesktopFileLog.Warn("Startup", "读取 Splash 位置失败。", ex);
                }
            }

            desktop.MainWindow = wizard;
            if (splashBounds is { } bounds)
                wizard.PrepareFromSplash(bounds);

            // Show the next window first, then close splash — order matters.
            if (!wizard.IsVisible)
                wizard.Show();

            if (_splashWindow is { } splash)
            {
                try
                {
                    splash.Hide();
                    splash.Close();
                }
                catch (Exception ex)
                {
                    DesktopFileLog.Warn("Startup", "关闭 Splash 失败。", ex);
                }

                _splashWindow = null;
            }

            if (splashBounds is null)
                Dispatcher.UIThread.Post(wizard.StartIntroAnimation, DispatcherPriority.Loaded);

            wizard.Completed += (_, _) =>
            {
                bool restart = wizard.ShouldRestartAfterComplete;
                try
                {
                    if (restart)
                        RestartLauncherProcess();
                }
                finally
                {
                    try { wizard.Close(); } catch { /* ignore */ }
                    if (restart)
                    {
                        desktop.Shutdown(0);
                    }
                    else
                    {
                        ShowMainWindow(desktop, fadeSplash: false);
                        desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
                    }
                }
            };

            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;

            DesktopFileLog.Info(
                "Startup",
                $"OOBE 已创建并显示；Kind={plan.Kind}；Reason={plan.Reason}；Steps={string.Join('>', plan.Steps)}；Restart={plan.RestartAfterComplete}。");
        });
    }

    private static async Task WaitForPluginOptionalRuntimeAsync(string phase)
    {
        DesktopFileLog.Info("Startup", $"[{phase}] 插件侧车探测开始（超时 {PluginWarmupTimeout.TotalSeconds:0}s）。");
        try
        {
            PluginOptionalRuntimeResult result = await DesktopHost
                .EnsureOptionalRuntimeReadyAsync()
                .WaitAsync(PluginWarmupTimeout)
                .ConfigureAwait(false);

            DesktopFileLog.Info(
                "Startup",
                $"[{phase}] 插件侧车探测结束：Status={result.Status}；{result.Message}");
        }
        catch (TimeoutException)
        {
            DesktopFileLog.Warn(
                "Startup",
                $"[{phase}] 插件侧车在 {PluginWarmupTimeout.TotalSeconds:0}s 内未完成，继续进入后续界面。");
        }
        catch (Exception ex)
        {
            DesktopFileLog.Warn("Startup", $"[{phase}] 插件侧车等待异常，继续进入后续界面。", ex);
        }
    }

    private void ShowMainWindow(IClassicDesktopStyleApplicationLifetime desktop, bool fadeSplash)
    {
        MainWindow mainWindow = new();
        DesktopFileLog.Info("Startup", "主窗口创建完成。");

        SingleInstanceCoordinator?.ActivationRequested += (_, _) =>
            Dispatcher.UIThread.Post(mainWindow.ActivateExistingInstance);
        if (SingleInstanceCoordinator?.ConsumePendingActivation() == true)
            Dispatcher.UIThread.Post(mainWindow.ActivateExistingInstance);

        // Assign + show main BEFORE closing splash so the process never has zero windows.
        desktop.MainWindow = mainWindow;
        if (!mainWindow.IsVisible)
            mainWindow.Show();

        if (fadeSplash && _splashWindow is { } splashFade)
        {
            mainWindow.Opened += (_, _) =>
            {
                try { splashFade.CloseWithFade(TimeSpan.FromMilliseconds(400)); }
                catch { try { splashFade.Close(); } catch { /* ignore */ } }
                if (ReferenceEquals(_splashWindow, splashFade))
                    _splashWindow = null;
            };
            // If already opened, fade immediately.
            if (mainWindow.IsLoaded)
            {
                try { splashFade.CloseWithFade(TimeSpan.FromMilliseconds(400)); }
                catch { try { splashFade.Close(); } catch { /* ignore */ } }
                _splashWindow = null;
            }
        }
        else if (_splashWindow is { } splash)
        {
            try { splash.Close(); } catch { /* ignore */ }
            _splashWindow = null;
        }

        desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
    }

    /// <summary>Relaunch this host after OOBE so path overrides and migrated settings take effect.</summary>
    private static void RestartLauncherProcess()
    {
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
        {
            try
            {
                exe = Process.GetCurrentProcess().MainModule?.FileName;
            }
            catch
            {
                exe = null;
            }
        }

        if (string.IsNullOrWhiteSpace(exe))
        {
            DesktopFileLog.Warn("OOBE", "无法解析可执行文件路径，完成配置后未自动重启。");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? Environment.CurrentDirectory
            });
            DesktopFileLog.Info("OOBE", "OOBE 完成，已请求重启启动器：" + exe);
        }
        catch (Exception ex)
        {
            DesktopFileLog.Warn("OOBE", "重启启动器失败：" + ex.Message);
        }
    }
}
