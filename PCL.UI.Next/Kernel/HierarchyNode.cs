// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

/// <summary>Intrusive hierarchy links for the entity tree (architecture §13).</summary>
public struct HierarchyNode
{
    public UiEntity Parent { get; set; }
    public UiEntity FirstChild { get; set; }
    public UiEntity LastChild { get; set; }
    public UiEntity PreviousSibling { get; set; }
    public UiEntity NextSibling { get; set; }
    public ushort Depth { get; set; }
}
