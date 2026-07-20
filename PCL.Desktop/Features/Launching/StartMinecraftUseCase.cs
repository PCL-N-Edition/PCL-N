// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using PCL.Desktop.Features.Launching.Views;

namespace PCL.Desktop.Features.Launching;

/// <summary>
/// Facade entry for starting Minecraft. The heavy pipeline still lives in MainWindow until
/// fully extracted; host binds the implementation once at startup.
/// </summary>
public sealed class StartMinecraftUseCase
{
    private Func<StartMinecraftRequest, CancellationToken, Task>? _handler;

    public void Bind(Func<StartMinecraftRequest, CancellationToken, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _handler = handler;
    }

    public Task ExecuteAsync(StartMinecraftRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_handler is null)
            throw new InvalidOperationException("StartMinecraftUseCase 尚未 Bind。");

        return _handler(request, cancellationToken);
    }
}

public sealed record StartMinecraftRequest(
    ILaunchHomeSurface Home,
    LaunchInstanceInfo Instance,
    string? WorldName = null,
    string? ServerAddress = null);
