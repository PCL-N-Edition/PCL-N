// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

/// <summary>Reasons that keep the runtime in continuous frame mode.</summary>
[Flags]
public enum UiContinuousReason : uint
{
    None = 0,
    Animation = 1u << 0,
    ScrollInertia = 1u << 1,
    CaretBlink = 1u << 2,
    Video = 1u << 3,
    RealtimeEffect = 1u << 4,
    Gesture = 1u << 5,
    OverlayTimer = 1u << 6
}

/// <summary>
/// Idle / reactive / continuous frame scheduler (architecture §32).
/// Reactive requests made during a frame schedule the <em>next</em> frame —
/// they must not be cleared by end-of-frame acknowledge of the current frame.
/// </summary>
public sealed class UiFrameScheduler
{
    private bool _reactiveRequested;
    private UiContinuousReason _continuous;
    private readonly int[] _leaseCounts = new int[32];
    private UiContinuousReason _leasedContinuous;

    public bool NeedsFrame => _reactiveRequested || (_continuous | _leasedContinuous) != UiContinuousReason.None;

    public bool HasContinuous => (_continuous | _leasedContinuous) != UiContinuousReason.None;

    public UiContinuousReason ContinuousReasons => _continuous | _leasedContinuous;

    /// <summary>Reference-counted ownership for subsystems that may have multiple instances per world.</summary>
    public IDisposable AcquireContinuousFrame(UiContinuousReason reason)
    {
        if (reason == UiContinuousReason.None || ((uint)reason & ((uint)reason - 1u)) != 0)
            throw new ArgumentOutOfRangeException(nameof(reason), "A continuous-frame lease requires one reason bit.");
        int bit = System.Numerics.BitOperations.TrailingZeroCount((uint)reason);
        _leaseCounts[bit]++;
        _leasedContinuous |= reason;
        return new ContinuousLease(this, reason, bit);
    }

    public void RequestReactiveFrame() => _reactiveRequested = true;

    public void RequestContinuousFrame(UiContinuousReason reason)
    {
        if (reason == UiContinuousReason.None)
            return;
        _continuous |= reason;
    }

    public void ReleaseContinuousFrame(UiContinuousReason reason)
    {
        if (reason == UiContinuousReason.None)
            return;
        _continuous &= ~reason;
    }

    /// <summary>
    /// Call at frame start: consumes the request that caused this frame to run.
    /// Mid-frame <see cref="RequestReactiveFrame"/> calls remain for frame N+1.
    /// </summary>
    public void BeginFrame() => _reactiveRequested = false;

    public void Reset()
    {
        _reactiveRequested = false;
        _continuous = UiContinuousReason.None;
        _leasedContinuous = UiContinuousReason.None;
        Array.Clear(_leaseCounts);
    }

    private void ReleaseLease(UiContinuousReason reason, int bit)
    {
        if (_leaseCounts[bit] <= 0)
            return;
        _leaseCounts[bit]--;
        if (_leaseCounts[bit] == 0)
            _leasedContinuous &= ~reason;
    }

    private sealed class ContinuousLease(UiFrameScheduler owner, UiContinuousReason reason, int bit) : IDisposable
    {
        private UiFrameScheduler? _owner = owner;

        public void Dispose()
        {
            UiFrameScheduler? current = Interlocked.Exchange(ref _owner, null);
            current?.ReleaseLease(reason, bit);
        }
    }
}
