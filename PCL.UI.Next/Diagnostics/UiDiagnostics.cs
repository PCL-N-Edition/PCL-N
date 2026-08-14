// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Diagnostics;

namespace PCL.UI.Next;

/// <summary>Bounded runtime diagnostics with opt-in high-cost dirty and timeline tracing.</summary>
public sealed class UiDiagnostics
{
    private readonly UiDiagnosticsOptions _options;
    private readonly UiFrameTimeline?[] _timelines;
    private readonly List<UiSystemTiming> _systemTimings = [];
    private int _timelineHead;
    private int _timelineCount;
    private long _frameStartTimestamp;
    private long _frameAllocationStart;
    private int _dirtyMarkCount;
    private int _renderMutationCount;
    private UiFrameContext _frame;

    internal UiDiagnostics(UiDiagnosticsOptions options)
    {
        Validate(options);
        _options = options;
        Events = new UiDiagnosticJournal(options.EventCapacity);
        _timelines = new UiFrameTimeline[options.TimelineCapacity];
    }

    public UiDiagnosticsOptions Options => _options;
    public UiDiagnosticJournal Events { get; }
    public int TimelineCount => _timelineCount;

    public void CopyTimelinesTo(List<UiFrameTimeline> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        for (int i = 0; i < _timelineCount; i++)
        {
            UiFrameTimeline? timeline = _timelines[(_timelineHead + i) % _timelines.Length];
            if (timeline is not null)
                destination.Add(timeline);
        }
    }

    internal bool CapturesDirtyTrace =>
        (_options.Features & UiDiagnosticFeatures.DirtyTrace) != 0 &&
        IsLevelEnabled(UiDiagnosticLevel.Trace);

    internal bool CapturesTimeline =>
        (_options.Features & UiDiagnosticFeatures.FrameTimeline) != 0;

    internal void BeginFrame(in UiFrameContext frame)
    {
        _frame = frame;
        _dirtyMarkCount = 0;
        _renderMutationCount = 0;
        if (CapturesTimeline)
        {
            _systemTimings.Clear();
            _frameStartTimestamp = Stopwatch.GetTimestamp();
            _frameAllocationStart = GC.GetAllocatedBytesForCurrentThread();
        }
        Publish(
            UiDiagnosticEventKind.FrameStarted,
            UiDiagnosticLevel.Trace,
            UiEntity.None,
            UiEntity.None,
            UiScopeId.None);
    }

    internal long BeginSystem() => CapturesTimeline ? Stopwatch.GetTimestamp() : 0L;

    internal void EndSystem(IUiSystem system, long started)
    {
        if (!CapturesTimeline)
            return;
        double milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        _systemTimings.Add(new UiSystemTiming(system.Phase, system.Name, milliseconds));
    }

    internal void EndFrame(int entityCount)
    {
        if (CapturesTimeline)
        {
            double runtimeMilliseconds = Stopwatch.GetElapsedTime(_frameStartTimestamp).TotalMilliseconds;
            long allocated = GC.GetAllocatedBytesForCurrentThread() - _frameAllocationStart;
            UiFrameTimeline timeline = new(
                _frame.FrameIndex,
                _frame.Now,
                _frame.DeltaSeconds,
                runtimeMilliseconds,
                allocated,
                entityCount,
                _dirtyMarkCount,
                _renderMutationCount,
                _systemTimings.ToArray());
            AppendTimeline(timeline);
        }
        Publish(
            UiDiagnosticEventKind.FrameCompleted,
            UiDiagnosticLevel.Trace,
            UiEntity.None,
            UiEntity.None,
            UiScopeId.None,
            value0: _dirtyMarkCount,
            value1: _renderMutationCount);
    }

    internal void EntityCreated(UiEntity entity, UiScopeId scope) =>
        Publish(UiDiagnosticEventKind.EntityCreated, UiDiagnosticLevel.Info, entity, UiEntity.None, scope);

    internal void EntityDestroyed(UiEntity entity, UiScopeId scope) =>
        Publish(UiDiagnosticEventKind.EntityDestroyed, UiDiagnosticLevel.Info, entity, UiEntity.None, scope);

    internal void ScopeCreated(UiScopeId scope, UiScopeId parent) =>
        Publish(
            UiDiagnosticEventKind.ScopeCreated,
            UiDiagnosticLevel.Info,
            UiEntity.None,
            UiEntity.None,
            scope,
            value0: parent.Index,
            value1: parent.Generation);

    internal void ScopeDisposed(UiScopeId scope) =>
        Publish(UiDiagnosticEventKind.ScopeDisposed, UiDiagnosticLevel.Info, UiEntity.None, UiEntity.None, scope);

    internal void DirtyMarked(
        UiEntity entity,
        UiEntity source,
        UiDirtyFlags requested,
        UiDirtyFlags effective)
    {
        if (!CapturesDirtyTrace)
            return;
        _dirtyMarkCount++;
        Publish(
            UiDiagnosticEventKind.DirtyMarked,
            UiDiagnosticLevel.Trace,
            entity,
            source,
            UiScopeId.None,
            requested,
            value0: (long)(uint)effective);
    }

    internal void RenderMutationsGenerated(int count)
    {
        _renderMutationCount += count;
        Publish(
            UiDiagnosticEventKind.RenderMutationsGenerated,
            UiDiagnosticLevel.Trace,
            UiEntity.None,
            UiEntity.None,
            UiScopeId.None,
            value0: count);
    }

    private void Publish(
        UiDiagnosticEventKind kind,
        UiDiagnosticLevel level,
        UiEntity entity,
        UiEntity related,
        UiScopeId scope,
        UiDirtyFlags dirtyFlags = UiDirtyFlags.None,
        UiSystemPhase phase = default,
        long value0 = 0,
        long value1 = 0)
    {
        if (!IsLevelEnabled(level) || !IsFeatureEnabled(kind))
            return;
        UiDiagnosticEvent diagnosticEvent = new(
            0,
            _frame.FrameIndex,
            _frame.Now,
            kind,
            level,
            entity,
            related,
            scope,
            dirtyFlags,
            phase,
            value0,
            value1);
        Events.Publish(in diagnosticEvent);
    }

    private bool IsLevelEnabled(UiDiagnosticLevel level) =>
        _options.MinimumLevel != UiDiagnosticLevel.Off && level >= _options.MinimumLevel;

    private bool IsFeatureEnabled(UiDiagnosticEventKind kind) => kind switch
    {
        UiDiagnosticEventKind.DirtyMarked =>
            (_options.Features & UiDiagnosticFeatures.DirtyTrace) != 0,
        UiDiagnosticEventKind.FrameStarted or
        UiDiagnosticEventKind.FrameCompleted or
        UiDiagnosticEventKind.RenderMutationsGenerated =>
            (_options.Features & UiDiagnosticFeatures.FrameTimeline) != 0,
        _ => (_options.Features & UiDiagnosticFeatures.Lifecycle) != 0
    };

    private void AppendTimeline(UiFrameTimeline timeline)
    {
        if (_timelineCount == _timelines.Length)
        {
            _timelines[_timelineHead] = timeline;
            _timelineHead = (_timelineHead + 1) % _timelines.Length;
            return;
        }
        int index = (_timelineHead + _timelineCount) % _timelines.Length;
        _timelines[index] = timeline;
        _timelineCount++;
    }

    private static void Validate(UiDiagnosticsOptions options)
    {
        if (options.EventCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Diagnostic event capacity must be positive.");
        if (options.TimelineCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Timeline capacity must be positive.");
        if (!Enum.IsDefined(options.MinimumLevel))
            throw new ArgumentOutOfRangeException(nameof(options), "Unknown diagnostic level.");
        if ((options.Features & ~UiDiagnosticFeatures.All) != 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Unknown diagnostic feature.");
    }
}
