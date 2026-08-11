// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

[Flags]
public enum InteractionState : ushort
{
    None = 0,
    Hovered = 1 << 0,
    Pressed = 1 << 1,
    Focused = 1 << 2,
    Selected = 1 << 3,
    Disabled = 1 << 4,
    Checked = 1 << 5,
    Expanded = 1 << 6,
    Dragging = 1 << 7
}

public struct InteractionStateComponent
{
    public InteractionState Value { get; set; }
}

public readonly struct ThemeValue<T> where T : struct
{
    public ThemeValue(T literal)
    {
        Literal = literal;
        Token = default;
        UsesToken = false;
    }

    public ThemeValue(ThemeToken<T> token)
    {
        Literal = default;
        Token = token;
        UsesToken = true;
    }

    public T Literal { get; }
    public ThemeToken<T> Token { get; }
    public bool UsesToken { get; }

    public T Resolve(ThemeRegistry theme) => UsesToken ? theme.Get(Token) : Literal;

    internal bool References(int tokenId) => UsesToken && Token.Id == tokenId;

    public static implicit operator ThemeValue<T>(T value) => new(value);
    public static implicit operator ThemeValue<T>(ThemeToken<T> token) => new(token);
}

[Flags]
public enum UiStyleProperty : ushort
{
    None = 0,
    Background = 1 << 0,
    Foreground = 1 << 1,
    Opacity = 1 << 2,
    CornerRadius = 1 << 3,
    Padding = 1 << 4,
    FontSize = 1 << 5,
    FontWeight = 1 << 6,
    FontFamily = 1 << 7
}

/// <summary>Rule payload. Fluent With methods set explicit property bits.</summary>
public struct UiStyleValues
{
    public UiStyleProperty Defined { get; private set; }
    public ThemeValue<UiColor> Background { get; private set; }
    public ThemeValue<UiColor> Foreground { get; private set; }
    public ThemeValue<float> Opacity { get; private set; }
    public ThemeValue<float> CornerRadius { get; private set; }
    public ThemeValue<UiThickness> Padding { get; private set; }
    public ThemeValue<float> FontSize { get; private set; }
    public ThemeValue<int> FontWeight { get; private set; }
    public ThemeValue<int> FontFamily { get; private set; }

    public UiStyleValues WithBackground(ThemeValue<UiColor> value)
    {
        Background = value;
        Defined |= UiStyleProperty.Background;
        return this;
    }

    public UiStyleValues WithForeground(ThemeValue<UiColor> value)
    {
        Foreground = value;
        Defined |= UiStyleProperty.Foreground;
        return this;
    }

    public UiStyleValues WithOpacity(ThemeValue<float> value)
    {
        Opacity = value;
        Defined |= UiStyleProperty.Opacity;
        return this;
    }

    public UiStyleValues WithCornerRadius(ThemeValue<float> value)
    {
        CornerRadius = value;
        Defined |= UiStyleProperty.CornerRadius;
        return this;
    }

    public UiStyleValues WithPadding(ThemeValue<UiThickness> value)
    {
        Padding = value;
        Defined |= UiStyleProperty.Padding;
        return this;
    }

    public UiStyleValues WithFontSize(ThemeValue<float> value)
    {
        FontSize = value;
        Defined |= UiStyleProperty.FontSize;
        return this;
    }

    public UiStyleValues WithFontWeight(ThemeValue<int> value)
    {
        FontWeight = value;
        Defined |= UiStyleProperty.FontWeight;
        return this;
    }

    public UiStyleValues WithFontFamily(ThemeValue<int> value)
    {
        FontFamily = value;
        Defined |= UiStyleProperty.FontFamily;
        return this;
    }

    internal bool ReferencesToken(int tokenId) =>
        ((Defined & UiStyleProperty.Background) != 0 && Background.References(tokenId)) ||
        ((Defined & UiStyleProperty.Foreground) != 0 && Foreground.References(tokenId)) ||
        ((Defined & UiStyleProperty.Opacity) != 0 && Opacity.References(tokenId)) ||
        ((Defined & UiStyleProperty.CornerRadius) != 0 && CornerRadius.References(tokenId)) ||
        ((Defined & UiStyleProperty.Padding) != 0 && Padding.References(tokenId)) ||
        ((Defined & UiStyleProperty.FontSize) != 0 && FontSize.References(tokenId)) ||
        ((Defined & UiStyleProperty.FontWeight) != 0 && FontWeight.References(tokenId)) ||
        ((Defined & UiStyleProperty.FontFamily) != 0 && FontFamily.References(tokenId));
}

public struct ResolvedStyle : IEquatable<ResolvedStyle>
{
    public UiColor Background { get; set; }
    public UiColor Foreground { get; set; }
    public float Opacity { get; set; }
    public float CornerRadius { get; set; }
    public UiThickness Padding { get; set; }
    public float FontSize { get; set; }
    public int FontWeight { get; set; }
    public int FontFamilyId { get; set; }

    public static ResolvedStyle Default => new()
    {
        Background = UiColor.Transparent,
        Foreground = UiColor.FromRgb(31, 35, 41),
        Opacity = 1f,
        FontSize = 14f,
        FontWeight = 400
    };

    public bool Equals(ResolvedStyle other) =>
        Background == other.Background &&
        Foreground == other.Foreground &&
        Opacity.Equals(other.Opacity) &&
        CornerRadius.Equals(other.CornerRadius) &&
        Padding == other.Padding &&
        FontSize.Equals(other.FontSize) &&
        FontWeight == other.FontWeight &&
        FontFamilyId == other.FontFamilyId;

    public override bool Equals(object? obj) => obj is ResolvedStyle other && Equals(other);
    public static bool operator ==(ResolvedStyle left, ResolvedStyle right) => left.Equals(right);
    public static bool operator !=(ResolvedStyle left, ResolvedStyle right) => !left.Equals(right);
    public override int GetHashCode() => HashCode.Combine(
        Background,
        Foreground,
        Opacity,
        CornerRadius,
        Padding,
        FontSize,
        FontWeight,
        FontFamilyId);
}
