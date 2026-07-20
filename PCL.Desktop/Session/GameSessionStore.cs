// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Diagnostics;
using CommunityToolkit.Mvvm.Messaging;
using PCL.Desktop.Messaging;

namespace PCL.Desktop.Session;

/// <summary>Tracks the watched Minecraft process for FAB shutdown / log actions.</summary>
public sealed class GameSessionStore
{
    private readonly IMessenger _messenger;
    private Process? _runningProcess;
    private object? _context;
    private bool _isRunning;

    public GameSessionStore(IMessenger messenger)
    {
        _messenger = messenger;
    }

    public bool IsRunning => _isRunning;

    public Process? RunningProcess => _runningProcess;

    public object? Context => _context;

    public void SetRunning(Process? process, object? context = null)
    {
        bool running = process is { HasExited: false };
        _runningProcess = running ? process : null;
        _context = running ? context : null;
        if (_isRunning == running)
            return;

        _isRunning = running;
        _messenger.Send(new GameRunningChangedMessage(_isRunning));
    }

    public void Clear() => SetRunning(null);
}
