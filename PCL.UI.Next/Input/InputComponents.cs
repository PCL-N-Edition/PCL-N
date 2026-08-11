// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

/// <summary>Marks an entity as a target in the retained hit-test index.</summary>
public struct HitTestableComponent
{
    public bool IsVisible { get; set; }
    public bool IsEnabled { get; set; }
    public int ZIndex { get; set; }

    public static HitTestableComponent Default => new() { IsVisible = true, IsEnabled = true };
}

public struct FocusableComponent
{
    public int TabIndex { get; set; }
    public bool IsTabStop { get; set; }

    public static FocusableComponent Default => new() { IsTabStop = true };
}

public struct FocusScopeComponent
{
    public bool IsTrap { get; set; }
    public bool RestorePreviousFocus { get; set; }
}

[Flags]
public enum UiGestureMask : byte
{
    None = 0,
    Click = 1 << 0,
    DoubleClick = 1 << 1,
    LongPress = 1 << 2,
    Drag = 1 << 3,
    Pan = 1 << 4,
    Pinch = 1 << 5
}

public struct GestureComponent
{
    public UiGestureMask Enabled { get; set; }
}

public readonly record struct UiGestureThresholds(
    float DragDistance,
    float ClickDistance,
    double DoubleClickSeconds,
    double LongPressSeconds)
{
    public static UiGestureThresholds Default => new(6f, 6f, 0.5d, 0.6d);
}
