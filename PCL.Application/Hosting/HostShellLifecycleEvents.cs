// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.Application.Hosting;

internal interface IHostShellReadyObserver
{
    void OnHostShellReady();
}

internal static class HostShellLifecycleEvents
{
    private static readonly object Gate = new();
    private static readonly List<IHostShellReadyObserver> Observers = [];
    private static bool _isReady;

    public static IDisposable Register(IHostShellReadyObserver observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        bool notifyNow;
        lock (Gate)
        {
            Observers.Add(observer);
            notifyNow = _isReady;
        }
        if (notifyNow)
            Notify(observer);
        return new Registration(observer);
    }

    public static void PublishReady()
    {
        IHostShellReadyObserver[] snapshot;
        lock (Gate)
        {
            if (_isReady)
                return;
            _isReady = true;
            snapshot = Observers.ToArray();
        }
        foreach (IHostShellReadyObserver observer in snapshot)
            Notify(observer);
    }

    private static void Notify(IHostShellReadyObserver observer)
    {
        try
        {
            observer.OnHostShellReady();
        }
        catch
        {
            // Optional extensions must not break host startup.
        }
    }

    private sealed class Registration(IHostShellReadyObserver observer) : IDisposable
    {
        private IHostShellReadyObserver? _observer = observer;

        public void Dispose()
        {
            IHostShellReadyObserver? current = Interlocked.Exchange(ref _observer, null);
            if (current is null)
                return;
            lock (Gate)
                Observers.Remove(current);
        }
    }
}
