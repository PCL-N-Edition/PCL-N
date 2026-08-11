// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

/// <summary>Authoring/runtime constraints shared by every layout entity.</summary>
public struct LayoutStyle
{
    public UiLength Width { get; set; }
    public UiLength Height { get; set; }
    public UiSize MinSize { get; set; }
    public UiSize MaxSize { get; set; }
    public UiThickness Margin { get; set; }
    public UiThickness Padding { get; set; }
    public UiHorizontalAlignment HorizontalAlignment { get; set; }
    public UiVerticalAlignment VerticalAlignment { get; set; }
    public bool IsMeasureBoundary { get; set; }

    public static LayoutStyle Default => new()
    {
        Width = UiLength.Auto,
        Height = UiLength.Auto,
        MinSize = UiSize.Zero,
        MaxSize = UiSize.Infinite,
        HorizontalAlignment = UiHorizontalAlignment.Stretch,
        VerticalAlignment = UiVerticalAlignment.Stretch
    };
}

public struct DesiredSize
{
    /// <summary>Outer desired size, including margin.</summary>
    public UiSize Value { get; set; }
}

public struct LayoutRect
{
    /// <summary>Arranged border box in root logical coordinates, excluding margin.</summary>
    public UiRect Value { get; set; }
}

internal struct LayoutMeasureInput
{
    public UiSize Available { get; set; }
}

internal struct LayoutSlot
{
    public UiRect Value { get; set; }
}

public struct StackLayout
{
    public UiOrientation Orientation { get; set; }
    public float Gap { get; set; }
}

public struct OverlayLayout;

public struct AbsoluteLayout;

public struct GridLayout
{
    public GridTrackSetHandle Tracks { get; set; }
    public float ColumnGap { get; set; }
    public float RowGap { get; set; }
}

public struct GridPlacement
{
    public int Row { get; set; }
    public int Column { get; set; }
    public int RowSpan { get; set; }
    public int ColumnSpan { get; set; }

    public static GridPlacement Default => new() { RowSpan = 1, ColumnSpan = 1 };
}

public struct AbsolutePlacement
{
    public float Left { get; set; }
    public float Top { get; set; }
}

public enum UiGridTrackKind : byte
{
    Fixed = 0,
    Auto = 1,
    Star = 2
}

public readonly record struct UiGridTrack(UiGridTrackKind Kind, float Value, float Min, float Max)
{
    public static UiGridTrack Fixed(float pixels) =>
        new(UiGridTrackKind.Fixed, pixels, pixels, pixels);

    public static UiGridTrack Auto(float min = 0f, float max = float.PositiveInfinity) =>
        new(UiGridTrackKind.Auto, 0f, min, max);

    public static UiGridTrack Star(float weight = 1f, float min = 0f, float max = float.PositiveInfinity) =>
        new(UiGridTrackKind.Star, Math.Max(weight, 0.0001f), min, max);
}

public readonly record struct GridTrackSetHandle(int Index, uint Generation)
{
    public static GridTrackSetHandle None => default;

    public bool IsNone => Index <= 0 || Generation == 0;
}

/// <summary>
/// Interns immutable grid track arrays behind compact handles. Unique blueprint definitions
/// are shared across instances and no managed arrays enter GridLayout components.
/// </summary>
public sealed class LayoutResourceStore
{
    private static readonly UiGridTrack[] DefaultColumns = [UiGridTrack.Star()];
    private static readonly UiGridTrack[] DefaultRows = [UiGridTrack.Auto()];
    private readonly List<Entry> _entries = [default];

    public GridTrackSetHandle Intern(ReadOnlySpan<UiGridTrack> columns, ReadOnlySpan<UiGridTrack> rows)
    {
        ReadOnlySpan<UiGridTrack> normalizedColumns = columns.IsEmpty ? DefaultColumns : columns;
        ReadOnlySpan<UiGridTrack> normalizedRows = rows.IsEmpty ? DefaultRows : rows;

        for (int i = 1; i < _entries.Count; i++)
        {
            Entry existing = _entries[i];
            if (normalizedColumns.SequenceEqual(existing.Columns) && normalizedRows.SequenceEqual(existing.Rows))
                return new GridTrackSetHandle(i, 1);
        }

        _entries.Add(new Entry(normalizedColumns.ToArray(), normalizedRows.ToArray()));
        return new GridTrackSetHandle(_entries.Count - 1, 1);
    }

    public ReadOnlySpan<UiGridTrack> GetColumns(GridTrackSetHandle handle) => Get(handle).Columns;

    public ReadOnlySpan<UiGridTrack> GetRows(GridTrackSetHandle handle) => Get(handle).Rows;

    private Entry Get(GridTrackSetHandle handle)
    {
        if (handle.Generation != 1 || handle.Index <= 0 || handle.Index >= _entries.Count)
            throw new InvalidOperationException("Grid track handle is stale or invalid: " + handle);
        return _entries[handle.Index];
    }

    private readonly record struct Entry(UiGridTrack[] Columns, UiGridTrack[] Rows);
}
