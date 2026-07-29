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
    private int _splashDismissed;

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
                    // While splash is the only window, closing it must not exit the process.
                    desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

                    desktop.Exit += (_, _) =>
                    {
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
                    };
                    bool showSplash = settings.GetBooleanOption(
                        "UiLauncherLogo",
                        LauncherSettingDefaults.GetBoolean("UiLauncherLogo"));
                    bool runOobe = FirstRunWizardWindow.NeedsWizard(settings);

                    if (showSplash)
                    {
                        _splashWindow = new SplashWindow();
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

    private async Task EnterMainShellAfterPluginReadyAsync(
        IClassicDesktopStyleApplicationLifetime desktop,
        bool fadeSplash)
    {
        await WaitForPluginOptionalRuntimeAsync("main").ConfigureAwait(false);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            ShowMainWindow(desktop, fadeSplash);
        });
    }

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

        // Show main first so lifetime always has a real shell window.
        desktop.MainWindow = mainWindow;
        if (!mainWindow.IsVisible)
            mainWindow.Show();

        // Always dismiss splash — do not rely solely on Opened (may have already fired).
        DismissSplash(fade: fadeSplash);

        // Hard guarantee: if fade path failed or Opened race left splash up, force close next frame.
        Dispatcher.UIThread.Post(
            () => DismissSplash(fade: false),
            DispatcherPriority.Background);

        desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
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
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? Environment.CurrentDirectory
            };
            if (arguments is { Count: > 0 })
            {
                // UseShellExecute does not support ArgumentList on all hosts — join args safely.
                start.Arguments = string.Join(
                    ' ',
                    arguments.Select(static a =>
                        a.Contains(' ', StringComparison.Ordinal) ? "\"" + a.Replace("\"", "\\\"") + "\"" : a));
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
