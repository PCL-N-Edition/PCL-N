// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

/// <summary>Immutable authoring-time grid definition copied into a blueprint.</summary>
public sealed class UiGridDefinition
{
    private readonly UiGridTrack[] _columns;
    private readonly UiGridTrack[] _rows;

    public UiGridDefinition(IEnumerable<UiGridTrack> columns, IEnumerable<UiGridTrack>? rows = null)
    {
        ArgumentNullException.ThrowIfNull(columns);
        _columns = columns.ToArray();
        _rows = rows?.ToArray() ?? [UiGridTrack.Auto()];
        if (_columns.Length == 0)
            throw new ArgumentException("Grid requires at least one column.", nameof(columns));
        if (_rows.Length == 0)
            throw new ArgumentException("Grid requires at least one row.", nameof(rows));
    }

    public ReadOnlySpan<UiGridTrack> Columns => _columns;

    public ReadOnlySpan<UiGridTrack> Rows => _rows;
}
