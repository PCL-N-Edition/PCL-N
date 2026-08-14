// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

public enum UiScrollMotionKind : byte
{
    Idle = 0,
    Inertia = 1,
    Spring = 2,
    Manipulation = 3
}

public enum UiScrollAlignment : byte
{
    Nearest = 0,
    Start = 1,
    Center = 2,
    End = 3
}

/// <summary>Hot one-axis viewport state. Offset is expressed in logical pixels.</summary>
public struct ScrollState
{
    public float Offset { get; set; }
    public float Velocity { get; set; }
    public float Target { get; set; }
    public float Extent { get; set; }
    public float Viewport { get; set; }
    public UiScrollMotionKind Motion { get; set; }
    internal UiTimestamp LastSampleTimestamp { get; set; }

    public float MaximumOffset => Math.Max(0f, Extent - Viewport);
}

/// <summary>Input and motion policy for a scroll viewport.</summary>
public struct ScrollViewport
{
    public UiOrientation Orientation { get; set; }
    public float WheelStep { get; set; }
    public float InertiaFriction { get; set; }
    public float SpringStrength { get; set; }
    public float SpringDamping { get; set; }
    public float OverscrollLimit { get; set; }

    public static ScrollViewport Vertical => new()
    {
        Orientation = UiOrientation.Vertical,
        WheelStep = 48f,
        InertiaFriction = 8f,
        SpringStrength = 220f,
        SpringDamping = 28f,
        OverscrollLimit = 72f
    };

    public static ScrollViewport Horizontal
    {
        get
        {
            ScrollViewport viewport = Vertical;
            viewport.Orientation = UiOrientation.Horizontal;
            return viewport;
        }
    }
}

/// <summary>Layout marker for a viewport whose first child is its scroll content.</summary>
public struct ScrollLayout
{
    public UiOrientation Orientation { get; set; }
}

/// <summary>Local transform applied to a retained scroll-content subtree.</summary>
public struct ScrollContentTransform
{
    public float X { get; set; }
    public float Y { get; set; }
}
