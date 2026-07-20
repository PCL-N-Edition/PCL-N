// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using PCL.Desktop.Messaging;

namespace PCL.Desktop.Shell;

/// <summary>Title bar sub-page typography + back affordance state (no Avalonia types).</summary>
public sealed partial class TitleBarViewModel : ObservableObject
{
    private readonly IMessenger _messenger;

    [ObservableProperty]
    private bool _isSubPageVisible;

    [ObservableProperty]
    private string _subPageTitle = string.Empty;

    [ObservableProperty]
    private double _titleFontSize = 15d;

    [ObservableProperty]
    private double _titleLetterSpacing;

    [ObservableProperty]
    private double _backButtonSize = 28d;

    [ObservableProperty]
    private double _titleHeight = 48d;

    public TitleBarViewModel(IMessenger messenger, AppShellViewModel shell)
    {
        _messenger = messenger;
        ApplyChrome(shell.UseExperimentalChrome);
        _messenger.Register<TitleBarViewModel, TitleSubPageMessage>(
            this,
            static (r, m) =>
            {
                if (m.Exit)
                {
                    r.IsSubPageVisible = false;
                    r.SubPageTitle = string.Empty;
                    return;
                }

                r.SubPageTitle = m.Title;
                r.IsSubPageVisible = true;
            });
        _messenger.Register<TitleBarViewModel, ExperimentalProfileChangedMessage>(
            this,
            static (r, m) => r.ApplyChrome(m.HomepageUiEnabled));
    }

    public void ApplyChrome(bool experimental)
    {
        TitleHeight = experimental ? 52d : 48d;
        TitleFontSize = experimental ? 17d : 15d;
        TitleLetterSpacing = experimental ? -0.35d : 0d;
        BackButtonSize = experimental ? 30d : 28d;
    }

    [RelayCommand]
    private void RequestBack() =>
        _messenger.Send(new TitleSubPageMessage(string.Empty, Exit: true));
}
