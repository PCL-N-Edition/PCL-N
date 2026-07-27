// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.Application.Accounts;

/// <summary>
/// Describes why a host-side Microsoft Minecraft profile became available.
/// The host publishes only local session metadata; optional online behavior is
/// implemented by host-side account providers.
/// </summary>
internal enum HostAccountSessionReason
{
    Selected,
    Authenticated,
    Restored
}

internal sealed record HostMicrosoftProfileSnapshot(
    string Username,
    string Uuid,
    string? SkinAddress);

internal interface IHostAccountSessionObserver
{
    void OnMicrosoftProfileAvailable(
        HostMicrosoftProfileSnapshot profile,
        HostAccountSessionReason reason,
        bool allowBackgroundOnlineWork);
}

internal static class HostAccountSessionEvents
{
    private static readonly object Gate = new();
    private static readonly List<IHostAccountSessionObserver> Observers = [];

    public static IDisposable Register(IHostAccountSessionObserver observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        lock (Gate)
            Observers.Add(observer);
        return new Registration(observer);
    }

    public static void PublishMicrosoftProfile(
        HostMicrosoftProfileSnapshot profile,
        HostAccountSessionReason reason,
        bool allowBackgroundOnlineWork = true)
    {
        ArgumentNullException.ThrowIfNull(profile);
        IHostAccountSessionObserver[] snapshot;
        lock (Gate)
            snapshot = Observers.ToArray();

        foreach (IHostAccountSessionObserver observer in snapshot)
        {
            try
            {
                observer.OnMicrosoftProfileAvailable(profile, reason, allowBackgroundOnlineWork);
            }
            catch
            {
                // An optional extension must never break local account selection or login.
            }
        }
    }

    private sealed class Registration(IHostAccountSessionObserver observer) : IDisposable
    {
        private IHostAccountSessionObserver? _observer = observer;

        public void Dispose()
        {
            IHostAccountSessionObserver? current = Interlocked.Exchange(ref _observer, null);
            if (current is null)
                return;
            lock (Gate)
                Observers.Remove(current);
        }
    }
}
