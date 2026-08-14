// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

public enum UiNativeHostKind : byte
{
    TextBox = 0,
    PasswordBox = 1
}

public readonly record struct NativeHostHandle(int Index, uint Generation)
{
    public static NativeHostHandle None => default;
    public bool IsNone => Index <= 0 || Generation == 0;
}

/// <summary>ECS-owned target state for one backend-native control.</summary>
public struct NativeHostComponent
{
    public UiNativeHostKind Kind { get; set; }
    public string? Value { get; set; }
    public string? Placeholder { get; set; }
    public int SelectionStart { get; set; }
    public int SelectionEnd { get; set; }
    public bool IsReadOnly { get; set; }
    public bool AcceptsReturn { get; set; }
}

public readonly record struct NativeHostDescriptor(
    UiEntity Owner,
    UiScopeId Scope,
    UiNativeHostKind Kind,
    NativeHostVisualState State);

public readonly record struct NativeHostVisualState(
    UiRect Bounds,
    string Value,
    string Placeholder,
    int SelectionStart,
    int SelectionEnd,
    bool IsVisible,
    bool IsEnabled,
    bool IsFocused,
    bool IsReadOnly,
    bool AcceptsReturn);

[Flags]
public enum NativeHostMutationFlags : ushort
{
    None = 0,
    Bounds = 1 << 0,
    Value = 1 << 1,
    Placeholder = 1 << 2,
    Selection = 1 << 3,
    Visibility = 1 << 4,
    Enabled = 1 << 5,
    Focus = 1 << 6,
    ReadOnly = 1 << 7,
    AcceptsReturn = 1 << 8,
    All = Bounds | Value | Placeholder | Selection | Visibility | Enabled | Focus | ReadOnly | AcceptsReturn
}

public readonly record struct NativeHostMutation(
    NativeHostMutationFlags Flags,
    NativeHostVisualState State);

public enum NativeHostEventKind : byte
{
    ValueChanged = 0,
    SelectionChanged = 1,
    GotFocus = 2,
    LostFocus = 3,
    Submitted = 4
}

public readonly record struct NativeHostEvent(
    NativeHostHandle Handle,
    NativeHostEventKind Kind,
    UiTimestamp Timestamp,
    string? Value = null,
    int SelectionStart = 0,
    int SelectionEnd = 0);

public readonly record struct NativeHostFrameEvent(
    UiEntity Entity,
    NativeHostEventKind Kind,
    UiTimestamp Timestamp,
    string? Value,
    int SelectionStart,
    int SelectionEnd);

/// <summary>Optional backend capability for platform-native controls.</summary>
public interface INativeHostBackend
{
    event Action<NativeHostEvent>? NativeHostEventRaised;

    NativeHostHandle CreateNativeHost(in NativeHostDescriptor descriptor);

    void UpdateNativeHost(NativeHostHandle handle, in NativeHostMutation mutation);

    void DestroyNativeHost(NativeHostHandle handle);
}
