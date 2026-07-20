// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using PCL.Desktop.Messaging;

namespace PCL.Desktop.Shell;

/// <summary>
/// Shell-level state (title sub-page, experimental chrome flag). Views stay Avalonia-free.
/// Messenger handlers are registered explicitly for AOT/trimming safety.
/// </summary>
public sealed partial class AppShellViewModel : ObservableObject
{
    private readonly IMessenger _messenger;
    private readonly ExperimentalUiProfileSource _profileSource;

    [ObservableProperty]
    private bool _isTitleSubPageVisible;

    [ObservableProperty]
    private string _titleSubPageText = string.Empty;

    [ObservableProperty]
    private bool _useExperimentalChrome;

    [ObservableProperty]
    private string? _pendingHint;

    [ObservableProperty]
    private bool _pendingHintCritical;

    public AppShellViewModel(IMessenger messenger, ExperimentalUiProfileSource profileSource)
    {
        _messenger = messenger;
        _profileSource = profileSource;
        ExperimentalUiProfile profile = profileSource.Current;
        _useExperimentalChrome = profile.Chrome == ChromeStyle.Glass;

        _messenger.Register<AppShellViewModel, ExperimentalProfileChangedMessage>(
            this,
            static (r, m) => r.UseExperimentalChrome = m.HomepageUiEnabled);
        _messenger.Register<AppShellViewModel, TitleSubPageMessage>(
            this,
            static (r, m) =>
            {
                if (m.Exit)
                {
                    r.IsTitleSubPageVisible = false;
                    r.TitleSubPageText = string.Empty;
                    return;
                }

                r.TitleSubPageText = m.Title;
                r.IsTitleSubPageVisible = true;
            });
        _messenger.Register<AppShellViewModel, HintMessage>(
            this,
            static (r, m) =>
            {
                r.PendingHint = m.Message;
                r.PendingHintCritical = m.Critical;
            });
    }

    public ExperimentalUiProfile Profile => _profileSource.Current;

    public ExperimentalUiProfile RefreshProfile()
    {
        ExperimentalUiProfile profile = _profileSource.RefreshFromSettings();
        UseExperimentalChrome = profile.Chrome == ChromeStyle.Glass;
        return profile;
    }
}
