// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.UI.Next;

public enum UiSemanticRole : byte
{
    Generic = 0,
    Group = 1,
    StaticText = 2,
    Button = 3,
    TextBox = 4,
    PasswordBox = 5,
    Image = 6,
    Link = 7,
    CheckBox = 8,
    RadioButton = 9,
    Slider = 10,
    ProgressBar = 11,
    List = 12,
    ListItem = 13,
    Dialog = 14,
    Tooltip = 15,
    Heading = 16
}

[Flags]
public enum UiAccessibleState : ushort
{
    None = 0,
    Disabled = 1 << 0,
    Focused = 1 << 1,
    Selected = 1 << 2,
    Checked = 1 << 3,
    Expanded = 1 << 4,
    ReadOnly = 1 << 5,
    Hidden = 1 << 6
}

[Flags]
public enum UiAccessibleAction : ushort
{
    None = 0,
    Invoke = 1 << 0,
    Focus = 1 << 1,
    SetValue = 1 << 2,
    Increment = 1 << 3,
    Decrement = 1 << 4,
    Toggle = 1 << 5,
    ExpandCollapse = 1 << 6
}

public struct SemanticRole
{
    public UiSemanticRole Value { get; set; }
}

public struct AccessibleName
{
    public string? Value { get; set; }
}

public struct AccessibleDescription
{
    public string? Value { get; set; }
}

public struct AccessibleValue
{
    public string? Value { get; set; }
}

public struct AccessibleState
{
    public UiAccessibleState Value { get; set; }
}

public struct AccessibleAction
{
    public UiAccessibleAction Value { get; set; }
}

/// <summary>Authoring/blueprint payload expanded into independent semantic ECS components.</summary>
public readonly record struct SemanticDefinition(
    UiSemanticRole Role,
    string? Name = null,
    string? Description = null,
    string? Value = null,
    UiAccessibleState State = UiAccessibleState.None,
    UiAccessibleAction Actions = UiAccessibleAction.None)
{
    public bool IsDefined { get; } = true;
}
