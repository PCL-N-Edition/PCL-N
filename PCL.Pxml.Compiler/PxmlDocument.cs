namespace PCL.Pxml;

/// <summary>
/// The PXML authoring namespace. Documents that declare a namespace must use exactly this one.
/// </summary>
public static class PxmlWellKnown
{
    public const string Namespace = "https://pcln.dev/pxml/2026";
}

/// <summary>
/// Classifies one attribute value: a literal, or a state binding path wrapped in `{state ...}`.
/// </summary>
public enum PxmlValueKind
{
    Literal = 0,
    StateBinding = 1,
}

/// <summary>
/// One attribute value. <see cref="Text"/> carries the literal text or the binding path.
/// </summary>
public readonly record struct PxmlValue(PxmlValueKind Kind, string Text)
{
    public static PxmlValue Literal(string text) => new(PxmlValueKind.Literal, text);

    public static PxmlValue StateBinding(string path) => new(PxmlValueKind.StateBinding, path);
}

/// <summary>
/// One attribute of a PXML element. Attribute names are unique per element.
/// </summary>
public sealed record PxmlProperty(string Name, PxmlValue Value);

/// <summary>
/// One element of a PXML document. The DOM is structural: element and attribute semantics are
/// resolved later by the compiler.
/// </summary>
public sealed class PxmlElement
{
    public PxmlElement(string name, IReadOnlyList<PxmlProperty> attributes, IReadOnlyList<PxmlElement> children)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(attributes);
        ArgumentNullException.ThrowIfNull(children);

        Name = name;
        Attributes = attributes;
        Children = children;
    }

    public string Name { get; }

    public IReadOnlyList<PxmlProperty> Attributes { get; }

    public IReadOnlyList<PxmlElement> Children { get; }

    public PxmlValue? FindProperty(string name)
    {
        foreach (PxmlProperty attribute in Attributes)
        {
            if (attribute.Name == name)
            {
                return attribute.Value;
            }
        }

        return null;
    }
}

/// <summary>
/// One parsed PXML document with exactly one root element.
/// </summary>
public sealed class PxmlDocument(PxmlElement root)
{
    public PxmlElement Root { get; } = root ?? throw new ArgumentNullException(nameof(root));
}
