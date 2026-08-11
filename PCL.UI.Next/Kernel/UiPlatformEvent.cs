// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

/// <summary>
/// Platform event envelope. Payload is untyped integer slots (no GC);
/// specialized input systems normalize pointer and keyboard events.
/// </summary>
public readonly struct UiPlatformEvent
{
    public UiPlatformEvent(
        UiScopeId scope,
        uint kind,
        UiTimestamp timestamp,
        int payload0 = 0,
        int payload1 = 0,
        int payload2 = 0,
        int payload3 = 0)
    {
        Scope = scope;
        Kind = kind;
        Timestamp = timestamp;
        Payload0 = payload0;
        Payload1 = payload1;
        Payload2 = payload2;
        Payload3 = payload3;
    }

    public UiScopeId Scope { get; }
    public uint Kind { get; }
    public UiTimestamp Timestamp { get; }
    public int Payload0 { get; }
    public int Payload1 { get; }
    public int Payload2 { get; }
    public int Payload3 { get; }
}

/// <summary>Well-known platform event kinds reserved by the kernel.</summary>
public static class UiPlatformEventKind
{
    public const uint None = 0;
    public const uint WindowResize = 1;
    public const uint PointerMove = 2;
    public const uint PointerDown = 3;
    public const uint PointerUp = 4;
    public const uint KeyDown = 5;
    public const uint KeyUp = 6;
    public const uint TextInput = 7;
    public const uint ThemeChanged = 8;
    public const uint DpiChanged = 9;
    public const uint PointerCancel = 10;
}
