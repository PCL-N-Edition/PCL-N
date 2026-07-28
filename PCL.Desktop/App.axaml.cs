// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

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
                    bool firstRunWizard = FirstRunWizardWindow.NeedsWizard(settings)
                        && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PCL_DISABLE_FIRST_RUN"));

                    if (firstRunWizard)
                    {
                        // First-run: splash icon hands off into the wizard (circular expand → welcome).
                        FirstRunWizardWindow wizard = new();
                        wizard.PrepareCentered();
                        desktop.MainWindow = wizard;

                        wizard.Completed += (_, _) =>
                        {
                            // Wizard marks legal + first-run version on "同意条款".
                            ShowMainWindow(desktop, fadeSplash: false);
                            wizard.Close();
                        };

                        if (showSplash)
                        {
                            _splashWindow = new SplashWindow();
                            _splashWindow.Show();
                            // Wait until splash is laid out, then hand off position and START expand once.
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
                                    DesktopFileLog.Warn("Startup", "首次启动与 Splash 衔接失败，使用居中开场。", ex);
                                    wizard.StartIntroAnimation();
                                }

                                if (!wizard.IsVisible)
                                    wizard.Show();
                            }, DispatcherPriority.Loaded);
                        }
                        else
                        {
                            // No splash: Opened/StartIntroAnimation path will expand from center.
                            Dispatcher.UIThread.Post(wizard.StartIntroAnimation, DispatcherPriority.Loaded);
                        }

                        DesktopFileLog.Info("Startup", "首次启动向导已创建（第 1 页：欢迎）。");
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
}
