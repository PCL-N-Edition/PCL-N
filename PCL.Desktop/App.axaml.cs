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
                    desktop.Exit += (_, _) =>
                    {
                        DesktopHost.ShutdownOptionalRuntime();
                        LauncherUpdateCoordinator.Current.Dispose();
                    };
                    bool showSplash = settings.GetBooleanOption(
                        "UiLauncherLogo",
                        LauncherSettingDefaults.GetBoolean("UiLauncherLogo"));
                    bool runOobe = FirstRunWizardWindow.NeedsWizard(settings);

                    if (runOobe)
                    {
                        OobeRunPlan plan = OobeConfiguration.CreateRunPlan(settings);
                        FirstRunWizardWindow wizard = new(plan);
                        wizard.PrepareCentered();
                        desktop.MainWindow = wizard;

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
                                    // Short update OOBE: continue into the main shell without restart.
                                    ShowMainWindow(desktop, fadeSplash: false);
                                }
                            }
                        };

                        if (showSplash)
                        {
                            _splashWindow = new SplashWindow();
                            _splashWindow.Show();
                            Dispatcher.UIThread.Post(() =>
                            {
                                try
                                {
                                    if (_splashWindow is { } splash)
                                    {
                                        PixelPoint pos = splash.Position;
                                        double scale = splash.RenderScaling > 0 ? splash.RenderScaling : 1;
                                        int w = (int)Math.Round(Math.Max(splash.Bounds.Width, 136) * scale);
                                        int h = (int)Math.Round(Math.Max(splash.Bounds.Height, 136) * scale);
                                        splash.Hide();
                                        splash.Close();
                                        _splashWindow = null;
                                        wizard.PrepareFromSplash(new PixelRect(pos.X, pos.Y, w, h));
                                    }
                                    else
                                    {
                                        wizard.StartIntroAnimation();
                                    }
                                }
                                catch (Exception ex)
                                {
                                    DesktopFileLog.Warn("Startup", "OOBE 与 Splash 衔接失败，使用居中开场。", ex);
                                    wizard.StartIntroAnimation();
                                }

                                if (!wizard.IsVisible)
                                    wizard.Show();
                            }, DispatcherPriority.Loaded);
                        }
                        else
                        {
                            Dispatcher.UIThread.Post(wizard.StartIntroAnimation, DispatcherPriority.Loaded);
                        }

                        DesktopFileLog.Info(
                            "Startup",
                            $"OOBE 已创建；Kind={plan.Kind}；Reason={plan.Reason}；Steps={string.Join('>', plan.Steps)}；Restart={plan.RestartAfterComplete}。");
                    }
                    else
                    {
                        if (showSplash)
                        {
                            _splashWindow = new SplashWindow();
                            _splashWindow.Show();
                        }

                        ShowMainWindow(desktop, fadeSplash: true);
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

    private void ShowMainWindow(IClassicDesktopStyleApplicationLifetime desktop, bool fadeSplash)
    {
        MainWindow mainWindow = new();
        DesktopFileLog.Info("Startup", "主窗口创建完成。");
        if (fadeSplash)
            mainWindow.Opened += (_, _) => _splashWindow?.CloseWithFade(TimeSpan.FromMilliseconds(400));
        else
            _splashWindow?.Close();

        SingleInstanceCoordinator?.ActivationRequested += (_, _) =>
            Dispatcher.UIThread.Post(mainWindow.ActivateExistingInstance);
        if (SingleInstanceCoordinator?.ConsumePendingActivation() == true)
            Dispatcher.UIThread.Post(mainWindow.ActivateExistingInstance);

        desktop.MainWindow = mainWindow;
        if (!mainWindow.IsVisible)
            mainWindow.Show();
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
