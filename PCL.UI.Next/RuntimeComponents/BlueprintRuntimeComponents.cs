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

/// <summary>Text payload; managed strings are acceptable off the hot animation path.</summary>
public struct TextContent
{
    public string? Value { get; set; }
}

/// <summary>
/// Inline style classes for the hot path. Overflow storage is not implemented yet.
/// Future: overflow via StyleClassStore handle.
/// </summary>
public struct StyleClassSet
{
    public const int MaxInlineCount = 4;

    public int Class0 { get; set; }
    public int Class1 { get; set; }
    public int Class2 { get; set; }
    public int Class3 { get; set; }
    public byte Count { get; set; }

    public static StyleClassSet From(ReadOnlySpan<int> ids)
    {
        if (ids.Length > MaxInlineCount)
        {
            throw new ArgumentException(
                $"StyleClassSet supports at most {MaxInlineCount} inline classes; got {ids.Length}. " +
                "Style-class overflow storage is not implemented.",
                nameof(ids));
        }

        StyleClassSet set = default;
        set.Count = (byte)ids.Length;
        if (ids.Length > 0) set.Class0 = ids[0];
        if (ids.Length > 1) set.Class1 = ids[1];
        if (ids.Length > 2) set.Class2 = ids[2];
        if (ids.Length > 3) set.Class3 = ids[3];
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
