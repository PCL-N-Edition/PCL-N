// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using PCL.Desktop.Messaging;

namespace PCL.Desktop.Shell;

/// <summary>
/// Bottom-right FAB dock visibility state. View applies Show/Progress to MyExtraButton;
/// chrome painting (glass dock) is driven by <see cref="ShouldShowGlassDock"/>.
/// Explicit messenger registration for AOT/trimming safety.
/// </summary>
public sealed partial class ExtraDockViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAnyVisibleButton))]
    [NotifyPropertyChangedFor(nameof(ShouldShowGlassDock))]
    private bool _showBackToTop;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAnyVisibleButton))]
    [NotifyPropertyChangedFor(nameof(ShouldShowGlassDock))]
    private bool _showTaskManager;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAnyVisibleButton))]
    [NotifyPropertyChangedFor(nameof(ShouldShowGlassDock))]
    private bool _showShutdown;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAnyVisibleButton))]
    [NotifyPropertyChangedFor(nameof(ShouldShowGlassDock))]
    private bool _showGameLog;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAnyVisibleButton))]
    [NotifyPropertyChangedFor(nameof(ShouldShowGlassDock))]
    private bool _showUpdateRestart;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAnyVisibleButton))]
    [NotifyPropertyChangedFor(nameof(ShouldShowGlassDock))]
    private bool _showApril;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAnyVisibleButton))]
    [NotifyPropertyChangedFor(nameof(ShouldShowGlassDock))]
    private bool _showMusic;

    [ObservableProperty]
    private double _taskProgress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShouldShowGlassDock))]
    private bool _useGlassChrome;

    public ExtraDockViewModel(IMessenger messenger, AppShellViewModel shell)
    {
        _useGlassChrome = shell.UseExperimentalChrome;
        messenger.Register<ExtraDockViewModel, GameRunningChangedMessage>(
            this,
            static (r, m) => r.SetGameRunning(m.Value));
        messenger.Register<ExtraDockViewModel, TaskProgressChangedMessage>(
            this,
            static (r, m) => r.SetTaskManager(
                m.HasVisibleTask && !m.IsTaskManagerVisible,
                m.HasActiveTask ? m.Progress : m.HasVisibleTask ? 1d : 0d));
        messenger.Register<ExtraDockViewModel, ExperimentalProfileChangedMessage>(
            this,
            static (r, m) => r.UseGlassChrome = m.HomepageUiEnabled);
    }

    public bool HasAnyVisibleButton =>
        ShowBackToTop || ShowTaskManager || ShowShutdown || ShowGameLog ||
        ShowUpdateRestart || ShowApril || ShowMusic;

    /// <summary>Experimental glass dock only when at least one FAB is shown.</summary>
    public bool ShouldShowGlassDock => UseGlassChrome && HasAnyVisibleButton;

    public void SetBackToTopVisible(bool visible) => ShowBackToTop = visible;

    public void SetTaskManager(bool visible, double progress)
    {
        ShowTaskManager = visible;
        TaskProgress = progress;
    }

    public void SetGameRunning(bool running)
    {
        ShowShutdown = running;
        ShowGameLog = running;
    }
}
