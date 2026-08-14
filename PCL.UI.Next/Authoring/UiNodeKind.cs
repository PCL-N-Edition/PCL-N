// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

/// <summary>Authoring and blueprint node kinds.</summary>
public enum UiNodeKind : byte
{
    None = 0,
    Column = 1,
    Row = 2,
    Container = 3,
    Text = 4,
    Button = 5,
    /// <summary>Structural: condition chooses one of two child templates.</summary>
    If = 6,
    Grid = 7,
    Overlay = 8,
    Absolute = 9,
    Scroll = 10,
    VirtualList = 11,
    NativeHost = 12
}
