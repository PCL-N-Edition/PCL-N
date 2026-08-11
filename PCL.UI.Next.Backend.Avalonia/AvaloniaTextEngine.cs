// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using PCL.UI.Next;
using AvaloniaTextLayout = Avalonia.Media.TextFormatting.TextLayout;

namespace PCL.UI.Next.Backend.Avalonia;

/// <summary>Avalonia shaping implementation behind the backend-neutral text contract.</summary>
public sealed class AvaloniaTextEngine : ITextEngine, IDisposable
{
    private readonly Func<int, FontFamily> _fontFamilyResolver;
    private readonly List<Entry> _entries = [new Entry()];
    private readonly Stack<int> _free = [];
    private bool _disposed;

    public AvaloniaTextEngine(Func<int, FontFamily>? fontFamilyResolver = null)
    {
        _fontFamilyResolver = fontFamilyResolver ?? ResolveDefaultFont;
    }

    public TextLayoutHandle Layout(in TextLayoutRequest request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        FontFamily family = _fontFamilyResolver(request.FontFamilyId) ?? FontFamily.Default;
        Typeface typeface = new(
            family,
            FontStyle.Normal,
            (FontWeight)Math.Clamp(request.FontWeight, 1, 999),
            FontStretch.Normal);
        SolidColorBrush foreground = new(Colors.White);
        AvaloniaTextLayout layout = new(
            request.Text,
            typeface,
            Math.Max(1f, request.FontSize),
            foreground,
            textWrapping: request.Wrapping == UiTextWrapping.Wrap
                ? TextWrapping.Wrap
                : TextWrapping.NoWrap,
            flowDirection: request.Direction == UiTextDirection.RightToLeft
                ? FlowDirection.RightToLeft
                : FlowDirection.LeftToRight,
            maxWidth: NormalizeConstraint(request.WidthConstraint));

        int index;
        Entry entry;
        if (_free.Count > 0)
        {
            index = _free.Pop();
            entry = _entries[index];
        }
        else
        {
            index = _entries.Count;
            entry = new Entry { Generation = 1 };
            _entries.Add(entry);
        }

        entry.Alive = true;
        entry.Layout = layout;
        entry.Foreground = foreground;
        _entries[index] = entry;
        return new TextLayoutHandle(index, entry.Generation);
    }

    public UiSize Measure(TextLayoutHandle handle)
    {
        Entry entry = Get(handle);
        return new UiSize(
            checked((float)entry.Layout!.WidthIncludingTrailingWhitespace),
            checked((float)entry.Layout.Height));
    }

    public void Release(TextLayoutHandle handle)
    {
        if (!TryGet(handle, out Entry entry))
            return;
        entry.Layout!.Dispose();
        entry.Layout = null;
        entry.Foreground = null;
        entry.Alive = false;
        entry.Generation = NextGeneration(entry.Generation);
        _entries[handle.Index] = entry;
        _free.Push(handle.Index);
    }

    internal void Draw(
        TextLayoutHandle handle,
        DrawingContext context,
        Point origin,
        UiColor color)
    {
        ArgumentNullException.ThrowIfNull(context);
        Entry entry = Get(handle);
        entry.Foreground!.Color = Color.FromArgb(color.A, color.R, color.G, color.B);
        entry.Layout!.Draw(context, origin);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        for (int i = 1; i < _entries.Count; i++)
        {
            Entry entry = _entries[i];
            if (!entry.Alive)
                continue;
            entry.Layout!.Dispose();
            entry.Layout = null;
            entry.Foreground = null;
            entry.Alive = false;
            entry.Generation = NextGeneration(entry.Generation);
            _entries[i] = entry;
        }
        _free.Clear();
        _disposed = true;
    }

    private Entry Get(TextLayoutHandle handle)
    {
        if (!TryGet(handle, out Entry entry))
            throw new InvalidOperationException("Text layout handle is stale or invalid: " + handle);
        return entry;
    }

    private bool TryGet(TextLayoutHandle handle, out Entry entry)
    {
        if (handle.IsNone || handle.Index >= _entries.Count)
        {
            entry = null!;
            return false;
        }
        entry = _entries[handle.Index];
        return entry.Alive && entry.Generation == handle.Generation;
    }

    private static double NormalizeConstraint(float constraint) =>
        float.IsFinite(constraint) ? Math.Max(0f, constraint) : double.PositiveInfinity;

    private static FontFamily ResolveDefaultFont(int fontFamilyId)
    {
        _ = fontFamilyId;
        return FontFamily.Default;
    }

    private static uint NextGeneration(uint generation)
    {
        uint next = unchecked(generation + 1);
        return next == 0 ? 1 : next;
    }

    private sealed class Entry
    {
        public uint Generation { get; set; } = 1;
        public bool Alive { get; set; }
        public AvaloniaTextLayout? Layout { get; set; }
        public SolidColorBrush? Foreground { get; set; }
    }
}
