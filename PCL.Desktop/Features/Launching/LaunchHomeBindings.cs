// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using PCL.Desktop.Features.Launching.Views;

namespace PCL.Desktop.Features.Launching;

/// <summary>Host callbacks required to wire launch home surfaces.</summary>
public sealed class LaunchHomeBindings
{
    public required Action NavigateDownload { get; init; }

    public required Action NavigateInstanceSelect { get; init; }

    public required Action<LaunchInstanceInfo> ManageInstance { get; init; }

    public required Action CancelLaunch { get; init; }

    public required Action<string> StatusMessage { get; init; }

    public required Action<ILaunchHomeSurface, PageLaunchLeft.LaunchLoginPageType> OpenLoginPage { get; init; }

    public required Func<StartMinecraftRequest, Task> StartMinecraft { get; init; }

    public required Func<LaunchShortcutPin, Task> ActivateShortcut { get; init; }

    public required Action HideCommunityHint { get; init; }

    public required Action ApplyLaunchPageSettings { get; init; }

    public required Action ApplyHomepageSettings { get; init; }

    public required Func<int> ResolveMaximumLogLines { get; init; }

    public required Action EnsureFoldersLoaded { get; init; }

    public required string? SelectedMinecraftRoot { get; init; }

    public required string? PreferredInstanceDirectory { get; init; }

    public required bool ShowLaunchingHint { get; init; }
}
