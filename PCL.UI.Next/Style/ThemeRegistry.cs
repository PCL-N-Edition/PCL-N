// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

public readonly struct ThemeToken<T> : IEquatable<ThemeToken<T>> where T : struct
{
    public ThemeToken(int id, string? name = null)
    {
        if (id <= 0)
            throw new ArgumentOutOfRangeException(nameof(id));
        Id = id;
        Name = name;
    }

    public int Id { get; }
    public string? Name { get; }

    public bool Equals(ThemeToken<T> other) => Id == other.Id;
    public override bool Equals(object? obj) => obj is ThemeToken<T> other && Equals(other);
    public override int GetHashCode() => Id;
    public static bool operator ==(ThemeToken<T> left, ThemeToken<T> right) => left.Equals(right);
    public static bool operator !=(ThemeToken<T> left, ThemeToken<T> right) => !left.Equals(right);
}

/// <summary>Versioned backend-neutral theme values with coalesced token invalidation.</summary>
public sealed class ThemeRegistry
{
    private readonly Dictionary<int, Entry> _entries = new();
    private readonly HashSet<int> _changed = [];

    public event Action<int>? TokenChanged;

    public void Set<T>(ThemeToken<T> token, T value) where T : struct
    {
        if (_entries.TryGetValue(token.Id, out Entry existing))
        {
            if (existing.ValueType != typeof(T))
                throw new InvalidOperationException($"Theme token {token.Id} was registered with {existing.ValueType.Name}.");
            if (EqualityComparer<T>.Default.Equals((T)existing.Value, value))
                return;

            _entries[token.Id] = new Entry(typeof(T), value, unchecked(existing.Version + 1));
        }
        else
        {
            _entries[token.Id] = new Entry(typeof(T), value, 1);
        }

        _changed.Add(token.Id);
        TokenChanged?.Invoke(token.Id);
    }

    public T Get<T>(ThemeToken<T> token) where T : struct
    {
        if (!_entries.TryGetValue(token.Id, out Entry entry))
            throw new InvalidOperationException("Theme token has no value: " + (token.Name ?? token.Id.ToString()));
        if (entry.ValueType != typeof(T))
            throw new InvalidOperationException($"Theme token {token.Id} contains {entry.ValueType.Name}, not {typeof(T).Name}.");
        return (T)entry.Value;
    }

    public ulong Version<T>(ThemeToken<T> token) where T : struct =>
        _entries.TryGetValue(token.Id, out Entry entry) ? entry.Version : 0;

    internal void DrainChangedTokens(List<int> destination)
    {
        foreach (int token in _changed)
            destination.Add(token);
        _changed.Clear();
    }

    private readonly record struct Entry(Type ValueType, object Value, ulong Version);
}

public static class UiThemeTokens
{
    public static ThemeToken<UiColor> Accent { get; } = new(1, "Color.Accent");
    public static ThemeToken<UiColor> Surface { get; } = new(2, "Color.Surface");
    public static ThemeToken<UiColor> SurfaceHover { get; } = new(3, "Color.SurfaceHover");
    public static ThemeToken<UiColor> TextPrimary { get; } = new(4, "Color.TextPrimary");
    public static ThemeToken<UiColor> TextSecondary { get; } = new(5, "Color.TextSecondary");

    public static ThemeToken<float> SpacingSmall { get; } = new(101, "Spacing.Small");
    public static ThemeToken<float> SpacingMedium { get; } = new(102, "Spacing.Medium");
    public static ThemeToken<float> RadiusSmall { get; } = new(111, "Radius.Small");
    public static ThemeToken<float> RadiusCard { get; } = new(112, "Radius.Card");
    public static ThemeToken<float> FontBody { get; } = new(121, "Typography.Body");
    public static ThemeToken<float> FontTitle { get; } = new(122, "Typography.Title");
}

public static class UiDefaultTheme
{
    public static void Apply(ThemeRegistry theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        theme.Set(UiThemeTokens.Accent, UiColor.FromRgb(76, 134, 255));
        theme.Set(UiThemeTokens.Surface, UiColor.FromRgb(245, 247, 250));
        theme.Set(UiThemeTokens.SurfaceHover, UiColor.FromRgb(232, 237, 244));
        theme.Set(UiThemeTokens.TextPrimary, UiColor.FromRgb(31, 35, 41));
        theme.Set(UiThemeTokens.TextSecondary, UiColor.FromRgb(91, 98, 108));
        theme.Set(UiThemeTokens.SpacingSmall, 6f);
        theme.Set(UiThemeTokens.SpacingMedium, 12f);
        theme.Set(UiThemeTokens.RadiusSmall, 6f);
        theme.Set(UiThemeTokens.RadiusCard, 10f);
        theme.Set(UiThemeTokens.FontBody, 14f);
        theme.Set(UiThemeTokens.FontTitle, 28f);
    }
}
