// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

/// <summary>Authoring / blueprint node kinds (Phase 2).</summary>
public enum UiNodeKind : byte
{
    None = 0,
    Column = 1,
    Row = 2,
    Container = 3,
    Text = 4,
    Button = 5,
    /// <summary>Structural: condition chooses one of two child templates.</summary>
    If = 6
}
