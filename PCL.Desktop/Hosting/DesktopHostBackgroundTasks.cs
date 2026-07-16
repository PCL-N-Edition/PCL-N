// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using PCL.Application.Hosting.RuntimeExtensions;

namespace PCL.Desktop.Hosting;

/// <summary>
/// Forwards plugin/background install downloads to the main-window task manager
/// (same surface as Minecraft install progress).
/// </summary>
internal sealed class DesktopHostBackgroundTasks : IHostBackgroundTasks
{
    public static DesktopHostBackgroundTasks Instance { get; } = new();

    private Func<string, bool, IHostBackgroundTask>? _factory;

    public void Attach(Func<string, bool, IHostBackgroundTask> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    public void Detach() => _factory = null;

    public IHostBackgroundTask Begin(string title, bool openTaskManager = true)
    {
        Func<string, bool, IHostBackgroundTask>? factory = _factory;
        if (factory is null)
            return NullHostBackgroundTask.Instance;
        try
        {
            return factory(title, openTaskManager);
        }
        catch
        {
            return NullHostBackgroundTask.Instance;
        }
    }
}
