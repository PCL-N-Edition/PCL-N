// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

/// <summary>Identifies which blueprint node an entity was created from.</summary>
public struct BlueprintNodeRef
{
    public int InstanceId { get; set; }
    public int NodeIndex { get; set; }
}

public struct NodeKindComponent
{
    public UiNodeKind Kind { get; set; }
}

/// <summary>Phase-2 text payload (string is acceptable off hot animation path).</summary>
public struct TextContent
{
    public string? Value { get; set; }
}

public struct StyleClassSet
{
    public int Class0 { get; set; }
    public int Class1 { get; set; }
    public int Class2 { get; set; }
    public int Class3 { get; set; }
    public byte Count { get; set; }

    public static StyleClassSet From(ReadOnlySpan<int> ids)
    {
        StyleClassSet set = default;
        int n = Math.Min(4, ids.Length);
        set.Count = (byte)n;
        if (n > 0) set.Class0 = ids[0];
        if (n > 1) set.Class1 = ids[1];
        if (n > 2) set.Class2 = ids[2];
        if (n > 3) set.Class3 = ids[3];
        return set;
    }

    public bool Contains(int classId)
    {
        if (Count > 0 && Class0 == classId) return true;
        if (Count > 1 && Class1 == classId) return true;
        if (Count > 2 && Class2 == classId) return true;
        if (Count > 3 && Class3 == classId) return true;
        return false;
    }
}

public struct CommandBindingComponent
{
    public int CommandId { get; set; }
}

public struct BehaviorComponent
{
    public UiBehavior Flags { get; set; }
}

/// <summary>Tracks active branch for structural If nodes.</summary>
public struct StructuralIfState
{
    /// <summary>0 = none, 1 = true branch, 2 = false branch.</summary>
    public byte ActiveBranch { get; set; }
    public ulong LastConditionVersion { get; set; }
}
