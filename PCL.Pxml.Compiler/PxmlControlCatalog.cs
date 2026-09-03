using PCL.UI.Next;

namespace PCL.Pxml;

/// <summary>
/// The complete PXML-visible control names expanded from the configured build-time directory.
/// </summary>
public static class PxmlControlCatalog
{
    public static IReadOnlyList<string> Names => PxmlGeneratedControlCatalog.Names;
}

internal enum PxmlControlValueKind
{
    Double = 1,
    Thickness = 2,
    Boolean = 3,
    Orientation = 4,
    String = 5,
    SemanticId = 6,
    Alignment = 7,
}

internal enum PxmlIrPropertyTarget
{
    Width = 1,
    Height = 2,
    Margin = 3,
    Padding = 4,
    Visibility = 5,
    Label = 6,
    Orientation = 7,
    Spacing = 8,
    Scrollable = 9,
    Content = 10,
    Command = 11,
    Focusable = 12,
    Clickable = 13,
    ImageSource = 14,
    StretchLastChild = 15,
    MinWidth = 16,
    MaxWidth = 17,
    MinHeight = 18,
    MaxHeight = 19,
    Weight = 20,
    HorizontalAlignment = 21,
    VerticalAlignment = 22,
}

internal sealed class PxmlControlPropertyModel
{
    public PxmlControlPropertyModel(
        string name,
        PxmlControlValueKind valueKind,
        PxmlIrPropertyTarget target,
        XsrUiStateProperty? bindingProperty,
        XsrUiDirtyKinds bindingDirtyKinds,
        bool required,
        string? defaultValue)
    {
        Name = name;
        ValueKind = valueKind;
        Target = target;
        BindingProperty = bindingProperty;
        BindingDirtyKinds = bindingDirtyKinds;
        Required = required;
        DefaultValue = defaultValue;
    }

    public string Name { get; }

    public PxmlControlValueKind ValueKind { get; }

    public PxmlIrPropertyTarget Target { get; }

    public XsrUiStateProperty? BindingProperty { get; }

    public XsrUiDirtyKinds BindingDirtyKinds { get; }

    public bool Required { get; }

    public string? DefaultValue { get; }
}

internal sealed class PxmlControlModel
{
    public PxmlControlModel(
        PxmlIrNodeKind kind,
        string name,
        XsrUiSemanticRole role,
        PxmlRuntimeRecipe recipe,
        bool allowsChildren,
        IReadOnlyList<PxmlControlPropertyModel> properties)
    {
        Kind = kind;
        Name = name;
        Role = role;
        Recipe = recipe;
        AllowsChildren = allowsChildren;
        Properties = properties;
    }

    public PxmlIrNodeKind Kind { get; }

    public string Name { get; }

    public XsrUiSemanticRole Role { get; }

    public PxmlRuntimeRecipe Recipe { get; }

    public bool AllowsChildren { get; }

    public IReadOnlyList<PxmlControlPropertyModel> Properties { get; }

    public bool TryGetProperty(string name, out PxmlControlPropertyModel property)
    {
        foreach (PxmlControlPropertyModel candidate in Properties)
        {
            if (candidate.Name == name)
            {
                property = candidate;
                return true;
            }
        }

        property = null!;
        return false;
    }
}
