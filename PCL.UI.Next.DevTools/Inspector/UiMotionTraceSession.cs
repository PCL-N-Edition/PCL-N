// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next.DevTools;

/// <summary>Bounded per-frame animation sampling suitable for curves and retarget traces.</summary>
public sealed class UiMotionTraceSession : IUiSystem, IDisposable
{
    private readonly UiWorld _world;
    private readonly UiAnimationRuntime _animations;
    private readonly UiMotionTraceSample[] _samples;
    private readonly List<UiAnimationSnapshot> _scratch = [];
    private readonly UiAnimationEventReader _events;
    private int _head;
    private int _count;
    private bool _disposed;

    public UiMotionTraceSession(UiWorld world, UiAnimationRuntime animations, int capacity = 8_192)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _animations = animations ?? throw new ArgumentNullException(nameof(animations));
        if (!ReferenceEquals(world, animations.World))
            throw new InvalidOperationException("Motion trace and animation runtime must use the same world.");
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        _samples = new UiMotionTraceSample[capacity];
        _events = animations.Events.CreateReader(UiAnimationEventReaderStart.NextPublished);
        _world.Systems.Register(this);
    }

    public UiSystemPhase Phase => UiSystemPhase.BackendCommit;
    public string Name => "devtools.motion-trace";
    public int Count => _count;
    public int Capacity => _samples.Length;

    public void Update(UiWorld world, in UiFrameContext frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _scratch.Clear();
        _animations.CopySnapshotsTo(_scratch, activeOnly: true);
        for (int i = 0; i < _scratch.Count; i++)
            Append(new UiMotionTraceSample(frame.FrameIndex, frame.Now, _scratch[i]));

        while (_events.TryRead(out UiAnimationEvent animationEvent))
        {
            if (animationEvent.Kind == UiAnimationEventKind.Settled &&
                _animations.TryGetSnapshot(animationEvent.Settlement.Channel, out UiAnimationSnapshot settled))
            {
                Append(new UiMotionTraceSample(frame.FrameIndex, frame.Now, settled));
            }
        }
    }

    public void CopySamplesTo(List<UiMotionTraceSample> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        for (int i = 0; i < _count; i++)
            destination.Add(_samples[(_head + i) % _samples.Length]);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _world.Systems.Unregister(this);
        _scratch.Clear();
        _disposed = true;
    }

    private void Append(in UiMotionTraceSample sample)
    {
        if (_count == _samples.Length)
        {
            _samples[_head] = sample;
            _head = (_head + 1) % _samples.Length;
            return;
        }
        _samples[(_head + _count) % _samples.Length] = sample;
        _count++;
    }
}
