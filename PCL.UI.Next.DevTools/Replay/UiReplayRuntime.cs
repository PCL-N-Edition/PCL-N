// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next.DevTools;

/// <summary>Records deterministic Runtime inputs and actual frame boundaries.</summary>
public sealed class UiReplayRecorder : IDisposable
{
    public const int DefaultCapacity = 1_000_000;
    private readonly UiWorld _world;
    private readonly UiInteractiveRuntime? _interactive;
    private readonly List<UiReplayEntry> _entries;
    private readonly int _capacity;
    private bool _overflowed;
    private bool _disposed;

    public UiReplayRecorder(
        UiWorld world,
        UiInteractiveRuntime? interactive = null,
        int capacity = DefaultCapacity)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        if (interactive is not null && !ReferenceEquals(interactive.World, world))
            throw new InvalidOperationException("Replay recorder and interactive runtime must use the same world.");
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        _interactive = interactive;
        _capacity = capacity;
        _entries = new List<UiReplayEntry>(Math.Min(capacity, 4_096));
        _world.PlatformEventEnqueued += OnPlatformEvent;
        _world.StatePatchEnqueued += OnStatePatch;
        _world.FrameStarting += OnFrameStarting;
        if (_interactive is not null)
        {
            _interactive.ViewportChanged += OnViewportChanged;
            Append(UiReplayEntry.ViewportChanged(_interactive.Layout.Viewport));
        }
    }

    public int Count => _entries.Count;
    public bool IsOverflowed => _overflowed;

    public void RecordResourceReady(int resourceId, uint generation)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (resourceId <= 0)
            throw new ArgumentOutOfRangeException(nameof(resourceId));
        Append(UiReplayEntry.ResourceReady(resourceId, generation));
    }

    public UiReplayLog Complete()
    {
        if (_overflowed)
            throw new InvalidOperationException("Replay recording exceeded its fixed entry capacity.");
        return new UiReplayLog(_entries.ToArray());
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _world.PlatformEventEnqueued -= OnPlatformEvent;
        _world.StatePatchEnqueued -= OnStatePatch;
        _world.FrameStarting -= OnFrameStarting;
        if (_interactive is not null)
            _interactive.ViewportChanged -= OnViewportChanged;
        _disposed = true;
    }

    private void OnPlatformEvent(UiPlatformEvent platformEvent) =>
        Append(UiReplayEntry.FromPlatformEvent(in platformEvent));

    private void OnStatePatch(UiStatePatch patch) =>
        Append(UiReplayEntry.FromStatePatch(in patch));

    private void OnFrameStarting(UiFrameContext frame) =>
        Append(UiReplayEntry.ClockTick(frame.Now));

    private void OnViewportChanged(UiSize viewport) =>
        Append(UiReplayEntry.ViewportChanged(viewport));

    private void Append(in UiReplayEntry entry)
    {
        if (_overflowed)
            return;
        if (_entries.Count >= _capacity)
        {
            _overflowed = true;
            return;
        }
        _entries.Add(entry);
    }
}

/// <summary>Replays a versioned log against an identically constructed headless Runtime.</summary>
public sealed class UiReplayRunner
{
    private readonly UiWorld _world;
    private readonly DeterministicUiClock _clock;
    private readonly UiInteractiveRuntime? _interactive;
    private readonly Action<int, uint>? _resourceReady;

    public UiReplayRunner(
        UiWorld world,
        DeterministicUiClock clock,
        UiInteractiveRuntime? interactive = null,
        Action<int, uint>? resourceReady = null)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        if (!ReferenceEquals(world.Clock, clock))
            throw new InvalidOperationException("Replay requires the target world to use the supplied deterministic clock.");
        if (interactive is not null && !ReferenceEquals(interactive.World, world))
            throw new InvalidOperationException("Replay runner and interactive runtime must use the same world.");
        _interactive = interactive;
        _resourceReady = resourceReady;
    }

    public int Replay(UiReplayLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        int frames = 0;
        ReadOnlySpan<UiReplayEntry> entries = log.Entries.Span;
        for (int i = 0; i < entries.Length; i++)
        {
            UiReplayEntry entry = entries[i];
            switch (entry.Kind)
            {
                case UiReplayEntryKind.PlatformEvent:
                    UiPlatformEvent platformEvent = entry.PlatformEvent;
                    _world.EnqueuePlatformEvent(in platformEvent);
                    break;
                case UiReplayEntryKind.StatePatch:
                    UiStatePatch patch = entry.StatePatch;
                    _world.EnqueueStatePatch(in patch);
                    break;
                case UiReplayEntryKind.ClockTick:
                    if (entry.Timestamp.CompareTo(_clock.Now) < 0)
                        throw new InvalidDataException("Replay clock moved backwards.");
                    _clock.Set(entry.Timestamp.Seconds);
                    _world.Update(force: true);
                    frames++;
                    break;
                case UiReplayEntryKind.Viewport:
                    if (_interactive is null)
                        throw new InvalidOperationException("Replay contains viewport events but no interactive runtime was supplied.");
                    _interactive.SetViewport(entry.Viewport);
                    break;
                case UiReplayEntryKind.ResourceReady:
                    if (_resourceReady is null)
                        throw new InvalidOperationException("Replay contains resource events but no resource callback was supplied.");
                    _resourceReady(entry.ResourceId, entry.ResourceGeneration);
                    break;
                default:
                    throw new InvalidDataException("Unknown UI replay entry kind: " + entry.Kind);
            }
        }
        return frames;
    }
}
