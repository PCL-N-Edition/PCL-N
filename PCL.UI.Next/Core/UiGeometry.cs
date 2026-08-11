// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

/// <summary>Logical-pixel size used by the backend-independent runtime.</summary>
public readonly record struct UiSize(float Width, float Height)
{
    public static UiSize Zero => default;

    public static UiSize Infinite => new(float.PositiveInfinity, float.PositiveInfinity);

    public bool IsFinite => float.IsFinite(Width) && float.IsFinite(Height);
}

/// <summary>Logical-pixel point.</summary>
public readonly record struct UiPoint(float X, float Y)
{
    public static UiPoint Zero => default;
}

/// <summary>Logical-pixel rectangle.</summary>
public readonly record struct UiRect(float X, float Y, float Width, float Height)
{
    public static UiRect Empty => default;

    public float Right => X + Width;

    public float Bottom => Y + Height;
}

/// <summary>Logical-pixel edge thickness.</summary>
public readonly record struct UiThickness(float Left, float Top, float Right, float Bottom)
{
    public UiThickness(float uniform) : this(uniform, uniform, uniform, uniform)
    {
    }

    public UiThickness(float horizontal, float vertical) : this(horizontal, vertical, horizontal, vertical)
    {
    }

    public static UiThickness Zero => default;

    public float Horizontal => Left + Right;

    public float Vertical => Top + Bottom;

    public static UiThickness operator +(UiThickness left, UiThickness right) =>
        new(
            left.Left + right.Left,
            left.Top + right.Top,
            left.Right + right.Right,
            left.Bottom + right.Bottom);
}

/// <summary>Backend-neutral, non-premultiplied sRGB color.</summary>
public readonly record struct UiColor(byte A, byte R, byte G, byte B)
{
    public static UiColor Transparent => default;

    public static UiColor FromRgb(byte red, byte green, byte blue) => new(255, red, green, blue);

    public static UiColor FromArgb(byte alpha, byte red, byte green, byte blue) =>
        new(alpha, red, green, blue);
}

public enum UiLengthKind : byte
{
    Auto = 0,
    Pixels = 1,
    Percent = 2,
    Star = 3,
    MinContent = 4,
    MaxContent = 5
}

/// <summary>Backend-independent layout length (architecture section 36).</summary>
public readonly record struct UiLength(UiLengthKind Kind, float Value)
{
    public static UiLength Auto => default;

    public static UiLength Pixels(float value) => new(UiLengthKind.Pixels, value);

    public static UiLength Percent(float value) => new(UiLengthKind.Percent, value);

    public static UiLength Star(float value = 1f) => new(UiLengthKind.Star, value);
}

public enum UiOrientation : byte
{
    Horizontal = 0,
    Vertical = 1
}

public enum UiHorizontalAlignment : byte
{
    Stretch = 0,
    Start = 1,
    Center = 2,
    End = 3
}

public enum UiVerticalAlignment : byte
{
    Stretch = 0,
    Start = 1,
    Center = 2,
    End = 3
}
