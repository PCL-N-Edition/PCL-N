using System.Globalization;
using PCL.UI.Next;

namespace PCL.Pxml;

/// <summary>
/// Compiles a PXML DOM into the UI.Next IR. Element and property semantics come from static,
/// closed tables — there is no reflection anywhere in the pipeline. Unknown elements,
/// properties, or malformed values are compile errors with the failing name in the message.
/// </summary>
public static class PxmlCompiler
{
    public static PxmlUiIr Compile(PxmlDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return new PxmlUiIr(CompileNode(document.Root));
    }

    private static PxmlIrNode CompileNode(PxmlElement element)
    {
        return element.Name switch
        {
            "Page" => CompileContainer(element, PxmlIrNodeKind.Page, XsrUiSemanticRole.Page),
            "StackPanel" => CompileStackPanel(element),
            "Text" => CompileText(element),
            "Button" => CompileButton(element),
            "Image" => CompileImage(element),
            _ => throw Fail(element.Name, null, "is not a known PXML element"),
        };
    }

    private static PxmlIrNode CompileContainer(PxmlElement element, PxmlIrNodeKind kind, XsrUiSemanticRole role)
    {
        return Build(element, kind, role, supportsCommand: false, allowContent: false);
    }

    private static PxmlIrNode CompileStackPanel(PxmlElement element)
    {
        PxmlIrNode node = Build(element, PxmlIrNodeKind.StackPanel, XsrUiSemanticRole.None, supportsCommand: false, allowContent: false);
        node = node with
        {
            Orientation = ParseEnum<XsrUiOrientation>(element, "Orientation", defaultValue: XsrUiOrientation.Vertical),
            Spacing = ParseDouble(element, "Spacing", defaultValue: 0),
            Scrollable = ParseBool(element, "Scroll", defaultValue: false),
        };
        return node;
    }

    private static PxmlIrNode CompileText(PxmlElement element)
    {
        PxmlIrNode node = Build(element, PxmlIrNodeKind.Text, XsrUiSemanticRole.Text, supportsCommand: false, allowContent: true);
        string? content = node.Bindings.FirstOrDefault(binding => binding.Property == XsrUiStateProperty.Text) is null
            ? element.FindProperty("Content")?.Text
            : null;
        return node with { Content = content };
    }

    private static PxmlIrNode CompileButton(PxmlElement element)
    {
        PxmlIrNode node = Build(element, PxmlIrNodeKind.Button, XsrUiSemanticRole.Button, supportsCommand: true, allowContent: false);
        return node with
        {
            Focusable = ParseBool(element, "Focusable", defaultValue: true),
            Clickable = ParseBool(element, "Clickable", defaultValue: true),
        };
    }

    private static PxmlIrNode CompileImage(PxmlElement element)
    {
        PxmlIrNode node = Build(element, PxmlIrNodeKind.Image, XsrUiSemanticRole.Image, supportsCommand: false, allowContent: false);
        string? source = Require(element, "Source")?.Text;
        return node with { ImageSource = source };
    }

    private static PxmlIrNode Build(
        PxmlElement element,
        PxmlIrNodeKind kind,
        XsrUiSemanticRole role,
        bool supportsCommand,
        bool allowContent)
    {
        List<PxmlIrBinding> bindings = [];
        double? width = null;
        double? height = null;
        XsrUiThickness margin = default;
        XsrUiThickness padding = default;
        bool isVisible = true;
        string? label = null;
        string? command = null;

        if (!KnownProperties(kind).IsSupersetOf(element.Attributes.Select(property => property.Name)))
        {
            string unknown = element.Attributes
                .Select(property => property.Name)
                .First(name => !KnownProperties(kind).Contains(name));
            throw Fail(element.Name, unknown, "is not valid on this element");
        }

        width = ParseNullableDouble(element, "Width");
        height = ParseNullableDouble(element, "Height");
        margin = ParseThickness(element, "Margin");
        padding = ParseThickness(element, "Padding");

        PxmlValue? visibility = element.FindProperty("IsVisible");
        if (visibility is { } visibleValue)
        {
            isVisible = ParseBool(element, "IsVisible", defaultValue: true, overrideRaw: visibleValue);
            if (visibleValue.Kind == PxmlValueKind.StateBinding)
            {
                bindings.Add(new PxmlIrBinding(visibleValue.Text, XsrUiStateProperty.Visibility, XsrUiDirtyKinds.Paint));
            }
        }

        PxmlValue? labelValue = element.FindProperty("Label");
        label = labelValue?.Text;

        if (element.FindProperty("Content") is { } contentValue
            && contentValue.Kind == PxmlValueKind.StateBinding)
        {
            bindings.Add(new PxmlIrBinding(contentValue.Text, XsrUiStateProperty.Text, XsrUiDirtyKinds.Paint));
        }

        if (supportsCommand && element.FindProperty("Command") is { } commandValue)
        {
            if (commandValue.Kind != PxmlValueKind.Literal)
            {
                throw Fail(element.Name, "Command", "must reference a command by plain semantic ID");
            }

            if (commandValue.Text.Length == 0
                || commandValue.Text.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
            {
                throw Fail(element.Name, "Command", "needs one non-empty semantic ID without whitespace");
            }

            command = commandValue.Text;
        }

        return new PxmlIrNode
        {
            Kind = kind,
            Children = [.. element.Children.Select(CompileNode)],
            Width = width,
            Height = height,
            Margin = margin,
            Padding = padding,
            IsVisible = isVisible,
            Label = label,
            Role = role,
            Focusable = kind == PxmlIrNodeKind.Button && ParseBool(element, "Focusable", defaultValue: true),
            Clickable = kind == PxmlIrNodeKind.Button && ParseBool(element, "Clickable", defaultValue: true),
            Command = command,
            Bindings = bindings,
        };
    }

    private static void Reject(PxmlElement element, string property, bool condition)
    {
        if (condition && element.FindProperty(property) is not null)
        {
            throw Fail(element.Name, property, "is not valid on this element");
        }
    }

    private static PxmlValue? Require(PxmlElement element, string property)
    {
        return element.FindProperty(property)
            ?? throw Fail(element.Name, property, "is required on this element");
    }

    private static double ParseDouble(PxmlElement element, string property, double defaultValue) =>
        ParseNullableDouble(element, property, overrideRaw: element.FindProperty(property)) ?? defaultValue;

    private static double? ParseNullableDouble(PxmlElement element, string property, PxmlValue? overrideRaw = null)
    {
        PxmlValue? value = overrideRaw ?? element.FindProperty(property);
        if (value is null || value.Value.Kind == PxmlValueKind.StateBinding)
        {
            return null;
        }

        if (!double.TryParse(
                value!.Value.Text,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double parsed))
        {
            throw Fail(element.Name, property, "needs an invariant-culture number");
        }

        return parsed;
    }

    private static bool ParseBool(PxmlElement element, string property, bool defaultValue, PxmlValue? overrideRaw = null)
    {
        PxmlValue? value = overrideRaw ?? element.FindProperty(property);
        if (value is null || value.Value.Kind == PxmlValueKind.StateBinding)
        {
            return defaultValue;
        }

        return value!.Value.Text switch
        {
            "true" => true,
            "false" => false,
            _ => throw Fail(element.Name, property, "needs the literal 'true' or 'false'"),
        };
    }

    private static XsrUiThickness ParseThickness(PxmlElement element, string property)
    {
        PxmlValue? value = element.FindProperty(property);
        if (value is null || value.Value.Kind == PxmlValueKind.StateBinding)
        {
            return default;
        }

        string[] parts = value!.Value.Text.Split(',');
        switch (parts.Length)
        {
            case 1:
                double uniform = ParseNumber(element, property, parts[0]);
                return new XsrUiThickness(uniform, uniform, uniform, uniform);
            case 4:
                return new XsrUiThickness(
                    ParseNumber(element, property, parts[0]),
                    ParseNumber(element, property, parts[1]),
                    ParseNumber(element, property, parts[2]),
                    ParseNumber(element, property, parts[3]));
            default:
                throw Fail(element.Name, property, "needs one number or four comma-separated numbers");
        }
    }

    private static double ParseNumber(PxmlElement element, string property, string raw)
    {
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
        {
            throw Fail(element.Name, property, "needs invariant-culture numbers");
        }

        return parsed;
    }

    private static T ParseEnum<T>(PxmlElement element, string property, T defaultValue)
        where T : struct, Enum
    {
        PxmlValue? value = element.FindProperty(property);
        if (value is null || value.Value.Kind == PxmlValueKind.StateBinding)
        {
            return defaultValue;
        }

        if (!Enum.TryParse(value!.Value.Text, ignoreCase: false, out T parsed) || !Enum.IsDefined(parsed))
        {
            throw Fail(element.Name, property, $"needs one of {string.Join(", ", Enum.GetNames<T>())}");
        }

        return parsed;
    }

    private static HashSet<string> KnownProperties(PxmlIrNodeKind kind)
    {
        HashSet<string> common = ["Width", "Height", "Margin", "Padding", "IsVisible", "Label"];
        return kind switch
        {
            PxmlIrNodeKind.Page => common,
            PxmlIrNodeKind.StackPanel => [.. common, "Orientation", "Spacing", "Scroll"],
            PxmlIrNodeKind.Text => [.. common, "Content"],
            PxmlIrNodeKind.Button => [.. common, "Command", "Focusable", "Clickable"],
            PxmlIrNodeKind.Image => [.. common, "Source"],
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    private static PxmlCompileException Fail(string element, string? property, string problem) =>
        new(property is null
            ? $"The element '{element}' {problem}."
            : $"The property '{property}' on '{element}' {problem}.");
}
