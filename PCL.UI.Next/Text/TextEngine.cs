// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Text;

namespace PCL.UI.Next;

public enum UiTextWrapping : byte
{
    NoWrap = 0,
    Wrap = 1
}

public enum UiTextDirection : byte
{
    Auto = 0,
    LeftToRight = 1,
    RightToLeft = 2
}

public struct TextFormat
{
    public UiTextWrapping Wrapping { get; set; }
    public float MaxWidth { get; set; }
    public UiTextDirection Direction { get; set; }

    public static TextFormat Default => new()
    {
        Wrapping = UiTextWrapping.NoWrap,
        MaxWidth = float.PositiveInfinity,
        Direction = UiTextDirection.Auto
    };
}

public readonly record struct TextLayoutHandle(int Index, uint Generation)
{
    public static TextLayoutHandle None => default;

    public bool IsNone => Index <= 0 || Generation == 0;
}

public readonly record struct TextLayoutRequest(
    string Text,
    int FontFamilyId,
    float FontSize,
    int FontWeight,
    float WidthConstraint,
    UiTextWrapping Wrapping,
    UiTextDirection Direction);

/// <summary>Backend text shaping/measurement contract. Runtime stores only its opaque handle.</summary>
public interface ITextEngine
{
    TextLayoutHandle Layout(in TextLayoutRequest request);

    UiSize Measure(TextLayoutHandle handle);
}

public struct TextLayout
{
    public TextLayoutHandle Handle { get; set; }
    public UiSize Size { get; set; }
}

/// <summary>Stable request cache; identical text layouts share one backend handle.</summary>
public sealed class TextLayoutCache
{
    private readonly ITextEngine _engine;
    private readonly Dictionary<TextLayoutRequest, TextLayout> _entries = new();

    public TextLayoutCache(ITextEngine engine)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
    }

    public int Count => _entries.Count;

    public TextLayout GetOrCreate(in TextLayoutRequest request)
    {
        if (_entries.TryGetValue(request, out TextLayout cached))
            return cached;

        TextLayoutHandle handle = _engine.Layout(in request);
        if (handle.IsNone)
            throw new InvalidOperationException("Text engine returned an invalid layout handle.");
        TextLayout created = new() { Handle = handle, Size = _engine.Measure(handle) };
        _entries.Add(request, created);
        return created;
    }

    public void Clear() => _entries.Clear();
}

/// <summary>
/// Headless deterministic metrics for tests and early playground work. Production backends
/// replace this with a mature shaping engine; this class intentionally does not claim Unicode
/// shaping or font fallback support.
/// </summary>
public sealed class DeterministicTextEngine : ITextEngine
{
    private readonly List<UiSize> _metrics = [default];

    public int LayoutCount => _metrics.Count - 1;

    public TextLayoutHandle Layout(in TextLayoutRequest request)
    {
        float fontSize = Math.Max(1f, request.FontSize);
        float lineHeight = fontSize * 1.2f;
        float maxWidth = NormalizeConstraint(request.WidthConstraint);
        float currentWidth = 0f;
        float widest = 0f;
        int lines = 1;

        foreach (Rune rune in request.Text.EnumerateRunes())
        {
            if (rune.Value == '\n')
            {
                widest = Math.Max(widest, currentWidth);
                currentWidth = 0f;
                lines++;
                continue;
            }

            float advance = rune.Value <= 0x7f ? fontSize * 0.6f : fontSize;
            if (request.Wrapping == UiTextWrapping.Wrap &&
                float.IsFinite(maxWidth) &&
                currentWidth > 0f &&
                currentWidth + advance > maxWidth)
            {
                widest = Math.Max(widest, currentWidth);
                currentWidth = advance;
                lines++;
            }
            else
            {
                currentWidth += advance;
            }
        }

        widest = Math.Max(widest, currentWidth);
        if (float.IsFinite(maxWidth))
            widest = Math.Min(widest, maxWidth);

        _metrics.Add(new UiSize(widest, lines * lineHeight));
        return new TextLayoutHandle(_metrics.Count - 1, 1);
    }

    public UiSize Measure(TextLayoutHandle handle)
    {
        if (handle.Generation != 1 || handle.Index <= 0 || handle.Index >= _metrics.Count)
            throw new InvalidOperationException("Text layout handle is stale or invalid: " + handle);
        return _metrics[handle.Index];
    }

    private static float NormalizeConstraint(float value) =>
        value <= 0f || float.IsNaN(value) ? float.PositiveInfinity : value;
}
