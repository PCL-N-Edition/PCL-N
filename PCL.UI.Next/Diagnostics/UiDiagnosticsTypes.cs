// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

public enum UiDiagnosticLevel : byte
{
    Trace = 0,
    Info = 1,
    Warning = 2,
    Error = 3,
    Fatal = 4,
    Off = byte.MaxValue
}

[Flags]
public enum UiDiagnosticFeatures : byte
{
    None = 0,
    Lifecycle = 1 << 0,
    DirtyTrace = 1 << 1,
    FrameTimeline = 1 << 2,
    All = Lifecycle | DirtyTrace | FrameTimeline
}

public readonly record struct UiDiagnosticsOptions(
    int EventCapacity,
    int TimelineCapacity,
    UiDiagnosticLevel MinimumLevel,
    UiDiagnosticFeatures Features)
{
    public static UiDiagnosticsOptions Default => new(
        EventCapacity: 2_048,
        TimelineCapacity: 1,
        MinimumLevel: UiDiagnosticLevel.Info,
        Features: UiDiagnosticFeatures.Lifecycle);

    public static UiDiagnosticsOptions Developer => new(
        EventCapacity: 16_384,
        TimelineCapacity: 240,
        MinimumLevel: UiDiagnosticLevel.Trace,
        Features: UiDiagnosticFeatures.All);

    public static UiDiagnosticsOptions Disabled => new(
        EventCapacity: 1,
        TimelineCapacity: 1,
        MinimumLevel: UiDiagnosticLevel.Off,
        Features: UiDiagnosticFeatures.None);
}

public enum UiDiagnosticEventKind : byte
{
    EntityCreated = 0,
    EntityDestroyed = 1,
    ScopeCreated = 2,
    ScopeDisposed = 3,
    DirtyMarked = 4,
    RenderMutationsGenerated = 5,
    FrameStarted = 6,
    FrameCompleted = 7
}

/// <summary>Allocation-free structured diagnostic payload retained by the bounded journal.</summary>
public readonly record struct UiDiagnosticEvent(
    long Sequence,
    long FrameIndex,
    UiTimestamp Timestamp,
    UiDiagnosticEventKind Kind,
    UiDiagnosticLevel Level,
    UiEntity Entity,
    UiEntity RelatedEntity,
    UiScopeId Scope,
    UiDirtyFlags DirtyFlags,
    UiSystemPhase Phase,
    long Value0,
    long Value1);

public readonly record struct UiSystemTiming(
    UiSystemPhase Phase,
    string SystemName,
    double DurationMilliseconds);

public sealed class UiFrameTimeline
{
    private readonly UiSystemTiming[] _systems;

    internal UiFrameTimeline(
        long frameIndex,
        UiTimestamp timestamp,
        double deltaSeconds,
        double runtimeMilliseconds,
        long allocatedBytes,
        int entityCount,
        int dirtyMarkCount,
        int renderMutationCount,
        UiSystemTiming[] systems)
    {
        FrameIndex = frameIndex;
        Timestamp = timestamp;
        DeltaSeconds = deltaSeconds;
        RuntimeMilliseconds = runtimeMilliseconds;
        AllocatedBytes = allocatedBytes;
        EntityCount = entityCount;
        DirtyMarkCount = dirtyMarkCount;
        RenderMutationCount = renderMutationCount;
        _systems = systems;
    }

    public long FrameIndex { get; }
    public UiTimestamp Timestamp { get; }
    public double DeltaSeconds { get; }
    public double RuntimeMilliseconds { get; }
    public long AllocatedBytes { get; }
    public int EntityCount { get; }
    public int DirtyMarkCount { get; }
    public int RenderMutationCount { get; }
    public ReadOnlyMemory<UiSystemTiming> Systems => _systems;
}

