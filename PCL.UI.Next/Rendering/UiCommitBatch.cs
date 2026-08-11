// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

/// <summary>Immutable minimal render changes produced for one runtime frame.</summary>
public readonly struct UiCommitBatch
{
    private readonly RenderMutation[]? _mutations;

    public UiCommitBatch(long frameId, ReadOnlySpan<RenderMutation> mutations)
    {
        if (frameId <= 0)
            throw new ArgumentOutOfRangeException(nameof(frameId));
        FrameId = frameId;
        _mutations = mutations.IsEmpty ? Array.Empty<RenderMutation>() : mutations.ToArray();
    }

    internal UiCommitBatch(long frameId, RenderMutation[] mutations, bool takeOwnership)
    {
        if (frameId <= 0)
            throw new ArgumentOutOfRangeException(nameof(frameId));
        ArgumentNullException.ThrowIfNull(mutations);
        if (!takeOwnership)
            throw new ArgumentException("Internal batch construction must explicitly take ownership.", nameof(takeOwnership));
        FrameId = frameId;
        _mutations = mutations;
    }

    public long FrameId { get; }

    public ReadOnlyMemory<RenderMutation> Mutations =>
        _mutations ?? Array.Empty<RenderMutation>();

    public bool IsEmpty => Mutations.IsEmpty;
}
