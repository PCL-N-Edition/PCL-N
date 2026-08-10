// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

/// <summary>Composable interaction behaviors (architecture §110).</summary>
[Flags]
public enum UiBehavior : uint
{
    None = 0,
    Hoverable = 1u << 0,
    Pressable = 1u << 1,
    Clickable = 1u << 2,
    Selectable = 1u << 3,
    Focusable = 1u << 4,
    ButtonDefaults = Hoverable | Pressable | Clickable | Focusable
}
