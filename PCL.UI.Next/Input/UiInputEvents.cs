// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

[Flags]
public enum UiInputModifiers : byte
{
    None = 0,
    Shift = 1 << 0,
    Control = 1 << 1,
    Alt = 1 << 2,
    Meta = 1 << 3
}

public enum UiPointerButton : byte
{
    None = 0,
    Primary = 1,
    Secondary = 2,
    Middle = 3,
    X1 = 4,
    X2 = 5
}

[Flags]
public enum UiPointerButtons : byte
{
    None = 0,
    Primary = 1 << 0,
    Secondary = 1 << 1,
    Middle = 1 << 2,
    X1 = 1 << 3,
    X2 = 1 << 4
}

public enum UiPointerEventKind : byte
{
    Move = 0,
    Down = 1,
    Up = 2,
    Cancel = 3
}

public enum UiKeyEventKind : byte
{
    Down = 0,
    Up = 1
}

/// <summary>Backend-neutral physical/logical keys used by focus and shortcuts.</summary>
public enum UiKey : ushort
{
    None = 0,
    Tab = 1,
    Enter = 2,
    Space = 3,
    Escape = 4,
    Left = 5,
    Up = 6,
    Right = 7,
    Down = 8,
    Home = 9,
    End = 10,
    PageUp = 11,
    PageDown = 12,
    Backspace = 13,
    Delete = 14,
    F1 = 20,
    F2 = 21,
    F3 = 22,
    F4 = 23,
    F5 = 24,
    F6 = 25,
    F7 = 26,
    F8 = 27,
    F9 = 28,
    F10 = 29,
    F11 = 30,
    F12 = 31,
    A = 40,
    B = 41,
    C = 42,
    D = 43,
    E = 44,
    F = 45,
    G = 46,
    H = 47,
    I = 48,
    J = 49,
    K = 50,
    L = 51,
    M = 52,
    N = 53,
    O = 54,
    P = 55,
    Q = 56,
    R = 57,
    S = 58,
    T = 59,
    U = 60,
    V = 61,
    W = 62,
    X = 63,
    Y = 64,
    Z = 65,
    Digit0 = 70,
    Digit1 = 71,
    Digit2 = 72,
    Digit3 = 73,
    Digit4 = 74,
    Digit5 = 75,
    Digit6 = 76,
    Digit7 = 77,
    Digit8 = 78,
    Digit9 = 79
}

public readonly record struct UiKeyGesture(UiKey Key, UiInputModifiers Modifiers = UiInputModifiers.None)
{
    public bool Matches(in UiKeyEvent keyEvent) =>
        keyEvent.Kind == UiKeyEventKind.Down &&
        keyEvent.Key == Key &&
        keyEvent.Modifiers == Modifiers;
}

public readonly record struct UiPointerEvent(
    UiInputRootId InputRoot,
    UiScopeId Scope,
    UiPointerEventKind Kind,
    UiTimestamp Timestamp,
    int PointerId,
    UiPoint Position,
    UiPointerButton ChangedButton,
    UiPointerButtons Buttons,
    UiInputModifiers Modifiers);

public readonly record struct UiKeyEvent(
    UiInputRootId InputRoot,
    UiScopeId Scope,
    UiKeyEventKind Kind,
    UiTimestamp Timestamp,
    UiKey Key,
    UiInputModifiers Modifiers,
    bool IsRepeat);

public readonly record struct UiWheelEvent(
    UiInputRootId InputRoot,
    UiScopeId Scope,
    UiTimestamp Timestamp,
    UiPoint Position,
    UiPoint Delta,
    UiInputModifiers Modifiers);

public enum UiInputEventKind : byte
{
    Pointer = 0,
    Key = 1,
    Wheel = 2
}

/// <summary>Normalized per-frame input union consumed by interactive systems.</summary>
public readonly struct UiInputEvent
{
    private UiInputEvent(UiPointerEvent pointer)
    {
        Kind = UiInputEventKind.Pointer;
        Pointer = pointer;
        Key = default;
        Wheel = default;
    }

    private UiInputEvent(UiKeyEvent key)
    {
        Kind = UiInputEventKind.Key;
        Pointer = default;
        Key = key;
        Wheel = default;
    }

    private UiInputEvent(UiWheelEvent wheel)
    {
        Kind = UiInputEventKind.Wheel;
        Pointer = default;
        Key = default;
        Wheel = wheel;
    }

    public UiInputEventKind Kind { get; }
    public UiPointerEvent Pointer { get; }
    public UiKeyEvent Key { get; }
    public UiWheelEvent Wheel { get; }

    public static UiInputEvent FromPointer(in UiPointerEvent pointer) => new(pointer);
    public static UiInputEvent FromKey(in UiKeyEvent key) => new(key);
    public static UiInputEvent FromWheel(in UiWheelEvent wheel) => new(wheel);
}

/// <summary>
/// Stable packing contract between a platform backend and the Runtime platform-event queue.
/// Floating-point coordinates are stored as their IEEE-754 bit patterns.
/// </summary>
public static class UiPlatformInput
{
    public static UiPlatformEvent Pointer(
        UiInputRootId inputRoot,
        UiScopeId scope,
        UiPointerEventKind kind,
        UiTimestamp timestamp,
        UiPoint position,
        int pointerId = 0,
        UiPointerButton changedButton = UiPointerButton.None,
        UiPointerButtons buttons = UiPointerButtons.None,
        UiInputModifiers modifiers = UiInputModifiers.None)
    {
        uint platformKind = kind switch
        {
            UiPointerEventKind.Move => UiPlatformEventKind.PointerMove,
            UiPointerEventKind.Down => UiPlatformEventKind.PointerDown,
            UiPointerEventKind.Up => UiPlatformEventKind.PointerUp,
            UiPointerEventKind.Cancel => UiPlatformEventKind.PointerCancel,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        int packed = (int)changedButton | ((int)buttons << 8) | ((int)modifiers << 16);
        return new UiPlatformEvent(
            scope,
            platformKind,
            timestamp,
            BitConverter.SingleToInt32Bits(position.X),
            BitConverter.SingleToInt32Bits(position.Y),
            pointerId,
            packed,
            inputRoot);
    }

    public static UiPlatformEvent Key(
        UiInputRootId inputRoot,
        UiScopeId scope,
        UiKeyEventKind kind,
        UiTimestamp timestamp,
        UiKey key,
        UiInputModifiers modifiers = UiInputModifiers.None,
        bool isRepeat = false) =>
        new(
            scope,
            kind == UiKeyEventKind.Down ? UiPlatformEventKind.KeyDown : UiPlatformEventKind.KeyUp,
            timestamp,
            (int)key,
            (int)modifiers,
            isRepeat ? 1 : 0,
            inputRoot: inputRoot);

    public static UiPlatformEvent Wheel(
        UiInputRootId inputRoot,
        UiScopeId scope,
        UiTimestamp timestamp,
        UiPoint position,
        UiPoint delta,
        UiInputModifiers modifiers = UiInputModifiers.None) =>
        new(
            scope,
            UiPlatformEventKind.PointerWheel,
            timestamp,
            BitConverter.SingleToInt32Bits(position.X),
            BitConverter.SingleToInt32Bits(position.Y),
            BitConverter.SingleToInt32Bits(delta.X),
            BitConverter.SingleToInt32Bits(delta.Y),
            inputRoot,
            (int)modifiers);

    internal static bool TryNormalize(in UiPlatformEvent platformEvent, out UiInputEvent inputEvent)
    {
        UiPointerEventKind pointerKind;
        switch (platformEvent.Kind)
        {
            case UiPlatformEventKind.PointerMove:
                pointerKind = UiPointerEventKind.Move;
                break;
            case UiPlatformEventKind.PointerDown:
                pointerKind = UiPointerEventKind.Down;
                break;
            case UiPlatformEventKind.PointerUp:
                pointerKind = UiPointerEventKind.Up;
                break;
            case UiPlatformEventKind.PointerCancel:
                pointerKind = UiPointerEventKind.Cancel;
                break;
            case UiPlatformEventKind.KeyDown:
            case UiPlatformEventKind.KeyUp:
                UiKeyEvent key = new(
                    platformEvent.InputRoot,
                    platformEvent.Scope,
                    platformEvent.Kind == UiPlatformEventKind.KeyDown ? UiKeyEventKind.Down : UiKeyEventKind.Up,
                    platformEvent.Timestamp,
                    (UiKey)platformEvent.Payload0,
                    (UiInputModifiers)platformEvent.Payload1,
                    platformEvent.Payload2 != 0);
                inputEvent = UiInputEvent.FromKey(in key);
                return true;
            case UiPlatformEventKind.PointerWheel:
                UiWheelEvent wheel = new(
                    platformEvent.InputRoot,
                    platformEvent.Scope,
                    platformEvent.Timestamp,
                    new UiPoint(
                        BitConverter.Int32BitsToSingle(platformEvent.Payload0),
                        BitConverter.Int32BitsToSingle(platformEvent.Payload1)),
                    new UiPoint(
                        BitConverter.Int32BitsToSingle(platformEvent.Payload2),
                        BitConverter.Int32BitsToSingle(platformEvent.Payload3)),
                    (UiInputModifiers)platformEvent.Payload4);
                inputEvent = UiInputEvent.FromWheel(in wheel);
                return true;
            default:
                inputEvent = default;
                return false;
        }

        int packed = platformEvent.Payload3;
        UiPointerEvent pointer = new(
            platformEvent.InputRoot,
            platformEvent.Scope,
            pointerKind,
            platformEvent.Timestamp,
            platformEvent.Payload2,
            new UiPoint(
                BitConverter.Int32BitsToSingle(platformEvent.Payload0),
                BitConverter.Int32BitsToSingle(platformEvent.Payload1)),
            (UiPointerButton)(packed & 0xff),
            (UiPointerButtons)((packed >> 8) & 0xff),
            (UiInputModifiers)((packed >> 16) & 0xff));
        inputEvent = UiInputEvent.FromPointer(in pointer);
        return true;
    }
}
