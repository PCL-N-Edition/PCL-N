// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

/// <summary>
/// Presentation-state patch envelope. Consumers must validate scope generation
/// before applying (architecture §16).
/// </summary>
public readonly struct UiStatePatch
{
    public UiStatePatch(
        UiScopeId scope,
        uint requestGeneration,
        int patchKind,
        int payload0 = 0,
        int payload1 = 0,
        long payloadLong = 0)
    {
        Scope = scope;
        RequestGeneration = requestGeneration;
        PatchKind = patchKind;
        Payload0 = payload0;
        Payload1 = payload1;
        PayloadLong = payloadLong;
    }

    public UiScopeId Scope { get; }

    /// <summary>Generation captured when the async/request producer started.</summary>
    public uint RequestGeneration { get; }

    public int PatchKind { get; }
    public int Payload0 { get; }
    public int Payload1 { get; }
    public long PayloadLong { get; }
}
