// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Avalonia.Threading;

namespace PCL.Desktop.Diagnostics;

/// <summary>
/// Coalesces high-frequency UI updates onto the dispatcher (guide: batch every 16–100 ms).
/// Store/model updates should still happen immediately; only view refresh goes through this.
/// </summary>
internal sealed class UiUpdateCoalescer : IDisposable
{
    private readonly Action _flush;
    private readonly DispatcherTimer _timer;
    private bool _dirty;
    private bool _disposed;

    public UiUpdateCoalescer(Action flush, int intervalMs = 50)
    {
        ArgumentNullException.ThrowIfNull(flush);
        _flush = flush;
        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(Math.Clamp(intervalMs, 16, 250))
        };
        _timer.Tick += OnTick;
    }

    public void Request()
    {
        if (_disposed)
            return;
        _dirty = true;
        if (!_timer.IsEnabled)
            _timer.Start();
    }

    public void FlushNow()
    {
        if (_disposed)
            return;
        _dirty = false;
        _timer.Stop();
        _flush();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (!_dirty)
        {
            _timer.Stop();
            return;
        }

        _dirty = false;
        _flush();
    }
}
