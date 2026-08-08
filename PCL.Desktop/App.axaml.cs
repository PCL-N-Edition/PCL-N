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
using PCL.Desktop.Telemetry;

namespace PCL.Desktop;

public sealed partial class App : Avalonia.Application
{
    /// <summary>Max time to observe background plugin warmup before logging a timeout.</summary>
    private static readonly TimeSpan PluginWarmupTimeout = TimeSpan.FromSeconds(25);

    private SplashWindow? _splashWindow;
    private int _splashDismissed;
    private int _startupShutdownRequested;

    internal static SingleInstanceCoordinator? SingleInstanceCoordinator { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // UI-thread unhandled exceptions (Avalonia Dispatcher) → crash dialog + Issue prompt.
        UnhandledExceptionGuard.AttachUiDispatcher();

        try
        {
            LauncherSettings settings = LauncherSettingsPageBinder.LoadSettings();
            bool runOobe = FirstRunWizardWindow.NeedsWizard(settings);
            if (!runOobe)
                LauncherTelemetry.Initialize(settings);
            DesktopFileLog.Initialize(DesktopFileLog.LevelFromSetting(settings.GetIntegerOption(
                "SystemLogLevel",
                LauncherSettingDefaults.GetInteger("SystemLogLevel"))));
            DesktopFileLog.Info("Startup", $"启动器设置读取完成；日志级别={DesktopFileLog.Level}。");
            AvaloniaThemeManager.Apply(settings);
            AvaloniaLocalizationManager.InitializeFromSettings(settings);
            DesktopFileLog.Info("Startup", $"主题与语言初始化完成；语言={AvaloniaLocalizationManager.CurrentLanguageCode}。");

            // Apply compatibility animation kill-switch before any animated shell mounts.
            try
            {
                bool disableAnim = settings.GetBooleanOption(
                    "SystemDisableUiAnimations",
                    LauncherSettingDefaults.GetBoolean("SystemDisableUiAnimations"));
                PCL.Desktop.Controls.Legacy.ModAnimation.AniControlEnabled = disableAnim ? 1 : 0;
            }
            catch
            {
                // ignore
            }

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
                    // While splash is the only window, closing it must not exit the process.
                    desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

                    desktop.Exit += (_, args) =>
                    {
                        DesktopProcessExitGuard.Arm(args.ApplicationExitCode);
                        ReleaseSingleInstanceLock();
                        PCL.Desktop.Controls.Legacy.ModAnimation.ShutdownForApplicationExit();
                        DesktopFileLog.Info("Startup", "桌面生命周期正在退出；开始释放后台运行时。");
                        try
                        {
                            DesktopHost.ShutdownOptionalRuntime();
                        }
                        catch (Exception ex)
                        {
                            DesktopFileLog.Warn("Startup", "退出时关闭插件侧车失败。", ex);
                        }

                        try
                        {
                            LauncherUpdateCoordinator.Current.Dispose();
                        }
                        catch
                        {
                            // ignore
                        }

                        LauncherTelemetry.Shutdown();

                        DesktopFileLog.Info("Startup", "桌面生命周期退出清理完成。");
                    };
                    bool showSplash = settings.GetBooleanOption(
                        "UiLauncherLogo",
                        LauncherSettingDefaults.GetBoolean("UiLauncherLogo"));
                    if (showSplash)
                    {
                        _splashWindow = new SplashWindow();
                        _splashWindow.Closing += (_, _) => HandleSplashClosing(desktop);
                        desktop.MainWindow = _splashWindow;
                        _splashWindow.Show();
                        DesktopFileLog.Info("Startup", "启动图标已显示；插件功能在后台加载，首窗不会等待。");
                    }
                    else
                    {
                        DesktopFileLog.Info("Startup", "启动图标已关闭；插件功能在后台加载，立即进入首窗。");
                    }

                    if (runOobe)
                    {
                        OobeRunPlan plan = OobeConfiguration.CreateRunPlan(settings);
                        // Full self-check as soon as we know this session is OOBE-bound.
                        try
                        {
                            CompatibilityReport probe = LauncherCompatibilityProbe.Run(settings);
                            DesktopFileLog.Info(
                                "Compat",
                                $"OOBE 启动前自检：canRun={probe.CanRun}；ok={probe.OkCount}；issues={probe.IssueCount}。");
                        }
                        catch (Exception probeEx)
                        {
                            DesktopFileLog.Warn("Compat", "OOBE 启动前自检失败。", probeEx);
                        }

                        UnhandledExceptionGuard.Observe(
                            EnterOobeAsync(desktop, plan, showSplash),
                            "App.EnterOobeAsync");
                    }
                    else
                    {
                        UnhandledExceptionGuard.Observe(
                            EnterMainShellAsync(desktop, showSplash),
                            "App.EnterMainShellAsync");
                    }
                }
            }

            base.OnFrameworkInitializationCompleted();
            UnhandledExceptionGuard.NotifyUiReady();
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

    private async Task EnterMainShellAsync(
        IClassicDesktopStyleApplicationLifetime desktop,
        bool fadeSplash)
    {
        if (Volatile.Read(ref _startupShutdownRequested) != 0)
            return;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (Volatile.Read(ref _startupShutdownRequested) != 0)
                return;
            ShowMainWindow(desktop, fadeSplash);
        });

        UnhandledExceptionGuard.Observe(
            ObservePluginOptionalRuntimeAsync("main"),
            "App.ObservePluginOptionalRuntimeAsync(main)");
    }

    private async Task EnterOobeAsync(
        IClassicDesktopStyleApplicationLifetime desktop,
        OobeRunPlan plan,
        bool hadSplash)
    {
        if (Volatile.Read(ref _startupShutdownRequested) != 0)
            return;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (Volatile.Read(ref _startupShutdownRequested) != 0)
                return;

            FirstRunWizardWindow wizard = new(plan);
            wizard.PrepareCentered();

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

            // Dismiss splash only after OOBE intro is ready (avoids icon flicker).
            void OnIntroStarted(object? sender, EventArgs e)
            {
                wizard.IntroStarted -= OnIntroStarted;
                // Next frame: wizard has painted the logo at splash size.
                Dispatcher.UIThread.Post(
                    () => DismissSplash(fade: false),
                    DispatcherPriority.Render);
            }

            wizard.IntroStarted += OnIntroStarted;

            // Mid-OOBE: config directory applied → relaunch with --oobe-resume (Welcome → Online).
            wizard.PathRestartRequested += (_, _) =>
            {
                DismissSplash(fade: false);
                try
                {
                    // Drop the single-instance mutex before spawn so the new process can become primary.
                    ReleaseSingleInstanceLock();
                    RestartLauncherProcess(wizard.RestartArguments);
                }
                finally
                {
                    try { wizard.Close(); } catch { /* ignore */ }
                    desktop.Shutdown(0);
                }
            };

            wizard.Completed += (_, _) =>
            {
                // Safety: never leave splash orphaned after OOBE.
                DismissSplash(fade: false);

                bool restart = wizard.ShouldRestartAfterComplete;
                try
                {
                    LauncherTelemetry.Initialize(LauncherSettingsPageBinder.LoadSettings());
                    if (restart)
                    {
                        ReleaseSingleInstanceLock();
                        RestartLauncherProcess();
                    }
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
                    }
                }
            };

            // Show OOBE first (still under splash if Topmost), then start intro, then dismiss splash.
            desktop.MainWindow = wizard;
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            if (!wizard.IsVisible)
                wizard.Show();

            if (splashBounds is { } bounds)
                wizard.PrepareFromSplash(bounds);
            else
                Dispatcher.UIThread.Post(wizard.StartIntroAnimation, DispatcherPriority.Loaded);

            // If intro already started synchronously, release splash on next render.
            if (wizard.HasIntroStarted)
            {
                Dispatcher.UIThread.Post(
                    () => DismissSplash(fade: false),
                    DispatcherPriority.Render);
            }
            else
            {
                // Safety net if IntroStarted never fires.
                DispatcherTimer safety = new() { Interval = TimeSpan.FromMilliseconds(900) };
                safety.Tick += (_, _) =>
                {
                    safety.Stop();
                    DismissSplash(fade: false);
                };
                safety.Start();
            }

            DesktopFileLog.Info(
                "Startup",
                $"OOBE 已创建并显示；Kind={plan.Kind}；Reason={plan.Reason}；Steps={string.Join('>', plan.Steps)}；Restart={plan.RestartAfterComplete}。");
        });

        UnhandledExceptionGuard.Observe(
            ObservePluginOptionalRuntimeAsync("oobe"),
            "App.ObservePluginOptionalRuntimeAsync(oobe)");
    }

    private static async Task ObservePluginOptionalRuntimeAsync(string phase)
    {
        using TelemetryOperation operation = LauncherTelemetry.StartOperation(
            "plugin.load",
            "plugin.load");
        DesktopFileLog.Info(
            "Startup",
            $"[{phase}] 后台插件侧车探测开始（日志超时 {PluginWarmupTimeout.TotalSeconds:0}s；不阻塞首窗）。");
        try
        {
            PluginOptionalRuntimeResult result = await DesktopHost
                .EnsureOptionalRuntimeReadyAsync()
                .WaitAsync(PluginWarmupTimeout)
                .ConfigureAwait(false);

            DesktopFileLog.Info(
                "Startup",
                $"[{phase}] 后台插件侧车探测结束：Status={result.Status}；{result.Message}");
        }
        catch (TimeoutException)
        {
            operation.Cancel();
            DesktopFileLog.Warn(
                "Startup",
                $"[{phase}] 插件侧车在 {PluginWarmupTimeout.TotalSeconds:0}s 内未完成；首窗已显示，插件页将在就绪后注入。");
        }
        catch (Exception ex)
        {
            operation.Fail(ex);
            LauncherTelemetry.CaptureException(ex, "plugin.load");
            DesktopFileLog.Warn("Startup", $"[{phase}] 后台插件侧车等待异常；主界面不受影响。", ex);
        }
    }

    private void ShowMainWindow(IClassicDesktopStyleApplicationLifetime desktop, bool fadeSplash)
    {
        MainWindow mainWindow = new();
        DesktopFileLog.Info("Startup", "主窗口创建完成。");

        EventHandler? opened = null;
        opened = (_, _) =>
        {
            mainWindow.Opened -= opened;
            // Window.Show() may pump native messages and must not be the only
            // route that can reach splash cleanup.
            DismissSplash(fade: fadeSplash);
            LauncherTelemetry.MarkStartupReady();
        };
        mainWindow.Opened += opened;

        SingleInstanceCoordinator?.ActivationRequested += (_, _) =>
            Dispatcher.UIThread.Post(mainWindow.ActivateExistingInstance);
        if (SingleInstanceCoordinator?.ConsumePendingActivation() == true)
            Dispatcher.UIThread.Post(mainWindow.ActivateExistingInstance);

        // Show main first so lifetime always has a real shell window.
        desktop.MainWindow = mainWindow;
        // A close request can be dispatched re-entrantly by Window.Show().
        desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
        if (!mainWindow.IsVisible)
            mainWindow.Show();

        // Fallback for hosts where Opened fired before the handler was observed.
        DismissSplash(fade: fadeSplash);

        // Hard guarantee: if fade path failed or Opened race left splash up, force close next frame.
        Dispatcher.UIThread.Post(
            () => DismissSplash(fade: false),
            DispatcherPriority.Background);
    }

    private void HandleSplashClosing(IClassicDesktopStyleApplicationLifetime desktop)
    {
        // Programmatic splash dismissal sets _splashDismissed first. A close
        // before that point is a real user exit request.
        if (Volatile.Read(ref _splashDismissed) != 0 ||
            Interlocked.Exchange(ref _startupShutdownRequested, 1) != 0)
        {
            return;
        }

        DesktopFileLog.Info("Startup", "启动阶段收到关闭请求；取消进入主界面并退出进程。");
        Dispatcher.UIThread.Post(
            () =>
            {
                try
                {
                    desktop.Shutdown(0);
                }
                catch (Exception ex)
                {
                    DesktopFileLog.Warn("Startup", "启动阶段退出失败。", ex);
                }
            },
            DispatcherPriority.Send);
    }

    /// <summary>
    /// Close splash once. Safe to call multiple times.
    /// </summary>
    private void DismissSplash(bool fade)
    {
        if (Interlocked.Exchange(ref _splashDismissed, 1) == 1)
        {
            // Already dismissed once — still force-close any leftover reference without fade.
            SplashWindow? leftover = _splashWindow;
            _splashWindow = null;
            if (leftover is not null)
            {
                try
                {
                    leftover.Hide();
                    leftover.Close();
                }
                catch
                {
                    // ignore
                }
            }

            return;
        }

        SplashWindow? splash = _splashWindow;
        _splashWindow = null;
        if (splash is null)
            return;

        try
        {
            if (fade)
            {
                splash.CloseWithFade(TimeSpan.FromMilliseconds(280));
                DesktopFileLog.Info("Startup", "Splash 已开始淡出关闭。");
                return;
            }

            splash.Hide();
            splash.Close();
            DesktopFileLog.Info("Startup", "Splash 已立即关闭。");
        }
        catch (Exception ex)
        {
            DesktopFileLog.Warn("Startup", "关闭 Splash 失败，尝试强制 Close。", ex);
            try { splash.Close(); } catch { /* ignore */ }
        }
    }

    private static void ReleaseSingleInstanceLock()
    {
        try
        {
            SingleInstanceCoordinator? coordinator = SingleInstanceCoordinator;
            SingleInstanceCoordinator = null;
            coordinator?.Dispose();
        }
        catch (Exception ex)
        {
            DesktopFileLog.Warn("OOBE", "释放单实例锁失败。", ex);
        }
    }

    /// <summary>Relaunch this host after OOBE so path overrides and migrated settings take effect.</summary>
    private static void RestartLauncherProcess(IReadOnlyList<string>? arguments = null)
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
            ProcessStartInfo start = new()
            {
                FileName = exe,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? Environment.CurrentDirectory
            };
            if (arguments is { Count: > 0 })
            {
                foreach (string argument in arguments)
                    start.ArgumentList.Add(argument);
            }

            Process.Start(start);
            DesktopFileLog.Info(
                "OOBE",
                "已请求重启启动器：" + exe +
                (arguments is { Count: > 0 } ? " " + start.Arguments : string.Empty));
        }
        catch (Exception ex)
        {
            DesktopFileLog.Warn("OOBE", "重启启动器失败：" + ex.Message);
        }
    }
}
