// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using PCL.Desktop.Features.Instances.Views;
using PCL.Desktop.Features.Launching.Views;

namespace PCL.Desktop.Features.Instances;

/// <summary>
/// Host callbacks for the version-select surface (MainWindow supplies implementations).
/// Keeps the feature free of dialog/navigation ownership during Phase 3.
/// </summary>
public sealed class InstancesSelectBindings
{
    public required Func<MinecraftFolderInfo, bool, Task> SelectFolderAsync { get; init; }

    public required Action<string> OpenPath { get; init; }

    public required Action<MinecraftFolderInfo> PromptRenameFolder { get; init; }

    public required Action<MinecraftFolderInfo> RemoveFolder { get; init; }

    public required Func<Task> CreateDefaultFolderAsync { get; init; }

    public required Func<Task> AddFolderAsync { get; init; }

    public required Func<Task> ImportModpackAsync { get; init; }

    public required Func<Task<IReadOnlyList<LaunchInstanceInfo>>> RefreshInstancesAsync { get; init; }

    public required Func<LaunchInstanceInfo?> GetSelectedInstance { get; init; }

    public required Action NavigateDownload { get; init; }

    public required Action NavigateLaunch { get; init; }

    public required Action<LaunchInstanceInfo> OpenInstanceFolder { get; init; }

    public required Action<LaunchInstanceInfo> DeleteInstance { get; init; }

    public required Action<LaunchInstanceInfo> SelectInstance { get; init; }

    public required Action<LaunchInstanceInfo> ManageInstance { get; init; }
}
