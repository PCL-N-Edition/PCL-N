// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using PCL.Application.Hosting.RuntimeExtensions;
using PCL.Desktop.Views;

namespace PCL.Desktop.Hosting;

internal sealed class DesktopHostWindowActivation : IHostWindowActivation
{
    public static DesktopHostWindowActivation Instance { get; } = new();

    public async ValueTask ActivateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Dispatcher.UIThread.InvokeAsync(
            () =>
            {
                if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime &&
                    lifetime.MainWindow is MainWindow mainWindow)
                {
                    mainWindow.ActivateExistingInstance();
                }
            },
            DispatcherPriority.Send,
            cancellationToken);
    }
}
