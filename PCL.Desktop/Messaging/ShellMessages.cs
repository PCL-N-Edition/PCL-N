// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using CommunityToolkit.Mvvm.Messaging.Messages;
using PCL.Desktop.Features.Community;
using PCL.UI.Abstractions.Navigation;

namespace PCL.Desktop.Messaging;

/// <summary>Feature → shell: open a navigation route.</summary>
public sealed record NavigateRequestMessage(NavigationRouteId Route, bool Animate = true);

/// <summary>Feature → shell: enter title sub-page mode.</summary>
public sealed record TitleSubPageMessage(string Title, bool Exit = false);

/// <summary>Any → shell: toast / hint bar.</summary>
public sealed record HintMessage(string Message, bool Critical = false);

/// <summary>FolderStore → features: selected Minecraft root changed.</summary>
public sealed class FolderSelectionChangedMessage(string? rootDirectory)
    : ValueChangedMessage<string?>(rootDirectory);

/// <summary>GameSessionStore → ExtraDock: Minecraft process running state.</summary>
public sealed class GameRunningChangedMessage(bool isRunning)
    : ValueChangedMessage<bool>(isRunning);

/// <summary>TaskStore → ExtraDock / Tasks page.</summary>
public sealed record TaskProgressChangedMessage(
    bool HasVisibleTask,
    bool HasActiveTask,
    double Progress,
    bool IsTaskManagerVisible);

/// <summary>Settings → shell/features: experimental UI profile toggled.</summary>
public sealed record ExperimentalProfileChangedMessage(bool HomepageUiEnabled);

/// <summary>Network server catalog → shell: resolve or install a compatible instance, then join.</summary>
public sealed record CommunityServerJoinRequestedMessage(CommunityResourceEntry Entry);
