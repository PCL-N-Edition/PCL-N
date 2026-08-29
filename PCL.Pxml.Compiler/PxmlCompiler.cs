using System.Globalization;
using PCL.UI.Next;
using PCL.Xsr;

namespace PCL.Pxml;

/// <summary>
/// Compiles a structural PXML document through the control catalog generated from the required
/// build-time directory. The compiler owns generic literal parsing and typed IR slots only; it
/// contains no hand-written control-name or per-control property table.
/// </summary>
public static class PxmlCompiler
{
    public static PxmlHostIr Compile(PxmlDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return new PxmlHostIr(CompileNode(document.Root));
    }

    private static PxmlIrNode CompileNode(PxmlElement element)
    {
        if (!PxmlGeneratedControlCatalog.TryGet(element.Name, out PxmlControlModel model))
        {
            throw Fail(element.Name, null, "is not present in the configured PXML control catalog");
        }

        if (!model.AllowsChildren && element.Children.Count != 0)
        {
            throw Fail(element.Name, null, "does not accept child elements");
        }

        NodeBuilder builder = new(model.Kind, model.Recipe, model.Role);
        List<PxmlIrBinding> bindings = [];

        foreach (PxmlControlPropertyModel property in model.Properties)
        {
            if (property.DefaultValue is not null)
            {
                ApplyLiteral(element.Name, property, property.DefaultValue, builder);
            }
        }

        foreach (PxmlProperty attribute in element.Attributes)
        {
            if (!model.TryGetProperty(attribute.Name, out PxmlControlPropertyModel property))
            {
                throw Fail(element.Name, attribute.Name, "is not declared by this control model");
            }

            if (attribute.Value.Kind == PxmlValueKind.StateBinding)
            {
                if (property.BindingProperty is not { } bindingProperty)
                {
                    throw Fail(element.Name, attribute.Name, "does not support a state binding");
                }

                bindings.Add(new PxmlIrBinding(
                    ParseSemanticId(element.Name, attribute.Name, attribute.Value.Text),
                    bindingProperty,
                    property.BindingDirtyKinds));
                continue;
            }

            ApplyLiteral(element.Name, property, attribute.Value.Text, builder);
        }

        foreach (PxmlControlPropertyModel property in model.Properties)
        {
            if (property.Required && element.FindProperty(property.Name) is null)
            {
                throw Fail(element.Name, property.Name, "is required by this control model");
            }
        }

        return builder.Build(
            [.. element.Children.Select(CompileNode)],
            bindings);
    }

    private static void ApplyLiteral(
        string elementName,
        PxmlControlPropertyModel property,
        string raw,
        NodeBuilder builder)
    {
        switch (property.ValueKind)
        {
            case PxmlControlValueKind.Double:
                builder.Set(property.Target, ParseDouble(elementName, property.Name, raw));
                break;
            case PxmlControlValueKind.Thickness:
                builder.Set(property.Target, ParseThickness(elementName, property.Name, raw));
                break;
            case PxmlControlValueKind.Boolean:
                builder.Set(property.Target, ParseBoolean(elementName, property.Name, raw));
                break;
            case PxmlControlValueKind.Orientation:
                builder.Set(property.Target, ParseOrientation(elementName, property.Name, raw));
                break;
            case PxmlControlValueKind.String:
                builder.Set(property.Target, raw);
                break;
            case PxmlControlValueKind.SemanticId:
                builder.Set(property.Target, ParseSemanticId(elementName, property.Name, raw));
                break;
            default:
                throw new InvalidOperationException(
                    $"The generated control catalog uses unsupported value kind '{property.ValueKind}'.");
        }
    }

    private static double ParseDouble(string elementName, string propertyName, string raw)
    {
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            || !double.IsFinite(parsed))
        {
            throw Fail(elementName, propertyName, "needs a finite invariant-culture number");
        }

        return parsed;
    }

    private static XsrUiThickness ParseThickness(string elementName, string propertyName, string raw)
    {
        string[] parts = raw.Split(',');
        if (parts.Length == 1)
        {
            double uniform = ParseDouble(elementName, propertyName, parts[0]);
            return new XsrUiThickness(uniform, uniform, uniform, uniform);
        }

        if (parts.Length == 4)
        {
            return new XsrUiThickness(
                ParseDouble(elementName, propertyName, parts[0]),
                ParseDouble(elementName, propertyName, parts[1]),
                ParseDouble(elementName, propertyName, parts[2]),
                ParseDouble(elementName, propertyName, parts[3]));
        }

        throw Fail(elementName, propertyName, "needs one number or four comma-separated numbers");
    }

    private static bool ParseBoolean(string elementName, string propertyName, string raw) => raw switch
    {
        "true" => true,
        "false" => false,
        _ => throw Fail(elementName, propertyName, "needs the literal 'true' or 'false'"),
    };

    private static XsrUiOrientation ParseOrientation(string elementName, string propertyName, string raw) => raw switch
    {
        "Vertical" => XsrUiOrientation.Vertical,
        "Horizontal" => XsrUiOrientation.Horizontal,
        _ => throw Fail(elementName, propertyName, "needs one of Vertical, Horizontal"),
    };

    private static XsrSemanticId ParseSemanticId(string elementName, string propertyName, string raw)
    {
        // Semantic IDs are validated and parsed here, so the IR carries validated IDs and load
        // time only resolves them through the registry.
        try
        {
            return XsrSemanticId.Parse(raw);
        }
        catch (ArgumentException exception)
        {
            throw Fail(elementName, propertyName, $"needs a valid semantic ID ({exception.Message})");
        }
    }

    private static PxmlCompileException Fail(string element, string? property, string problem) =>
        new(property is null
            ? $"The element '{element}' {problem}."
            : $"The property '{property}' on '{element}' {problem}.");

    private sealed class NodeBuilder(
        PxmlIrNodeKind kind,
        PxmlRuntimeRecipe recipe,
        XsrUiSemanticRole role)
    {
        private double? _width;
        private double? _height;
        private XsrUiThickness _margin;
        private XsrUiThickness _padding;
        private bool _isVisible = true;
        private XsrUiOrientation _orientation;
        private double _spacing;
        private bool _scrollable;
        private string? _content;
        private string? _label;
        private bool _focusable;
        private bool _clickable;
        private XsrSemanticId? _command;
        private string? _imageSource;

        public void Set(PxmlIrPropertyTarget target, object value)
        {
            switch (target)
            {
                case PxmlIrPropertyTarget.Width:
                    _width = (double)value;
                    break;
                case PxmlIrPropertyTarget.Height:
                    _height = (double)value;
                    break;
                case PxmlIrPropertyTarget.Margin:
                    _margin = (XsrUiThickness)value;
                    break;
                case PxmlIrPropertyTarget.Padding:
                    _padding = (XsrUiThickness)value;
                    break;
                case PxmlIrPropertyTarget.Visibility:
                    _isVisible = (bool)value;
                    break;
                case PxmlIrPropertyTarget.Label:
                    _label = (string)value;
                    break;
                case PxmlIrPropertyTarget.Orientation:
                    _orientation = (XsrUiOrientation)value;
                    break;
                case PxmlIrPropertyTarget.Spacing:
                    _spacing = (double)value;
                    break;
                case PxmlIrPropertyTarget.Scrollable:
                    _scrollable = (bool)value;
                    break;
                case PxmlIrPropertyTarget.Content:
                    _content = (string)value;
                    break;
                case PxmlIrPropertyTarget.Command:
                    _command = (XsrSemanticId)value;
                    break;
                case PxmlIrPropertyTarget.Focusable:
                    _focusable = (bool)value;
                    break;
                case PxmlIrPropertyTarget.Clickable:
                    _clickable = (bool)value;
                    break;
                case PxmlIrPropertyTarget.ImageSource:
                    _imageSource = (string)value;
                    break;
                default:
                    throw new InvalidOperationException(
                        $"The generated control catalog uses unsupported IR target '{target}'.");
            }
        }

        public PxmlIrNode Build(
            IReadOnlyList<PxmlIrNode> children,
            IReadOnlyList<PxmlIrBinding> bindings) =>
            new()
            {
                Kind = kind,
                Recipe = recipe,
                Children = children,
                Width = _width,
                Height = _height,
                Margin = _margin,
                Padding = _padding,
                IsVisible = _isVisible,
                Orientation = _orientation,
                Spacing = _spacing,
                Scrollable = _scrollable,
                Content = _content,
                Label = _label,
                Role = role,
                Focusable = _focusable,
                Clickable = _clickable,
                Command = _command,
                ImageSource = _imageSource,
                Bindings = bindings,
            };
    }
}
