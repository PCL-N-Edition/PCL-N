using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace PCL.Pxml.Generators;

[Generator(LanguageNames.CSharp)]
public sealed class PxmlControlCatalogGenerator : IIncrementalGenerator
{
    private static readonly DiagnosticDescriptor MissingCatalog = new(
        "PXMLGEN001",
        "PXML control catalog is empty",
        "No .pxml-control files were supplied through PxmlControlCatalogDirectory",
        "PXML",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InvalidControl = new(
        "PXMLGEN002",
        "PXML control descriptor is invalid",
        "Control descriptor '{0}' is invalid: {1}",
        "PXML",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InvalidCatalog = new(
        "PXMLGEN003",
        "PXML control catalog is inconsistent",
        "PXML control catalog is invalid: {0}",
        "PXML",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly HashSet<string> Roles = new(StringComparer.Ordinal)
    {
        "None", "Text", "Button", "Page", "List", "ListItem", "Image", "ProgressBar", "Dialog",
        "TitleBar", "Navigation", "NavigationItem", "Content",
    };

    private static readonly HashSet<string> ValueKinds = new(StringComparer.Ordinal)
    {
        "Double", "Thickness", "Boolean", "Orientation", "String", "SemanticId", "Alignment",
    };

    private static readonly HashSet<string> Recipes = new(StringComparer.Ordinal)
    {
        "Element", "StackLayout", "Text", "CommandInput", "Image",
    };

    private static readonly HashSet<string> Targets = new(StringComparer.Ordinal)
    {
        "Width", "Height", "MinWidth", "MaxWidth", "MinHeight", "MaxHeight", "Weight",
        "HorizontalAlignment", "VerticalAlignment", "Margin", "Padding", "Visibility", "Label",
        "Orientation", "Spacing", "StretchLastChild", "Scrollable", "Content", "Command",
        "Focusable", "Clickable", "ImageSource", "Key",
    };

    private static readonly HashSet<string> BindingProperties = new(StringComparer.Ordinal)
    {
        "None", "Text", "Visibility", "SemanticLabel",
    };

    private static readonly HashSet<string> DirtyKinds = new(StringComparer.Ordinal)
    {
        "None", "Structure", "Layout", "Paint", "State",
    };

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<ParseResult> controls = context.AdditionalTextsProvider
            .Where(static file => file.Path.EndsWith(".pxml-control", StringComparison.OrdinalIgnoreCase))
            .Select(static (file, cancellationToken) => Parse(file, cancellationToken));

        context.RegisterSourceOutput(controls.Collect(), static (productionContext, results) =>
            Emit(productionContext, results));
    }

    private static ParseResult Parse(AdditionalText file, System.Threading.CancellationToken cancellationToken)
    {
        string fileName = Path.GetFileName(file.Path);
        SourceText? source = file.GetText(cancellationToken);
        if (source is null)
        {
            return ParseResult.Failed(fileName, "the file could not be read");
        }

        string? schema = null;
        string? name = null;
        string? role = null;
        string? recipe = null;
        int? id = null;
        bool? allowsChildren = null;
        int propertyDeclarations = 0;
        List<PropertyDefinition> properties = new();
        List<string> errors = new();
        string[] lines = source.ToString().Replace("\r\n", "\n").Split('\n');
        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index].Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            int separator = line.IndexOf('=');
            if (separator <= 0)
            {
                errors.Add($"line {index + 1} must be key=value");
                continue;
            }

            string key = line.Substring(0, separator).Trim();
            string value = line.Substring(separator + 1).Trim();
            switch (key)
            {
                case "schema":
                    AssignOnce(ref schema, value, key, index, errors);
                    break;
                case "id":
                    if (id is not null)
                    {
                        errors.Add($"line {index + 1} declares id more than once");
                    }
                    else if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsedId)
                        || parsedId <= 0)
                    {
                        errors.Add($"line {index + 1} needs a positive integer id");
                    }
                    else
                    {
                        id = parsedId;
                    }

                    break;
                case "name":
                    AssignOnce(ref name, value, key, index, errors);
                    break;
                case "role":
                    AssignOnce(ref role, value, key, index, errors);
                    break;
                case "recipe":
                    AssignOnce(ref recipe, value, key, index, errors);
                    break;
                case "children":
                    if (allowsChildren is not null)
                    {
                        errors.Add($"line {index + 1} declares children more than once");
                    }
                    else if (value == "true")
                    {
                        allowsChildren = true;
                    }
                    else if (value == "false")
                    {
                        allowsChildren = false;
                    }
                    else
                    {
                        errors.Add($"line {index + 1} children must be exactly true or false");
                    }

                    break;
                case "property":
                    propertyDeclarations++;
                    PropertyDefinition? property = ParseProperty(value, index + 1, errors);
                    if (property is not null)
                    {
                        properties.Add(property);
                    }

                    break;
                default:
                    errors.Add($"line {index + 1} uses unknown key '{key}'");
                    break;
            }
        }

        if (schema != "1")
        {
            errors.Add("schema must be exactly 1");
        }

        if (id is null)
        {
            errors.Add("id is required");
        }

        if (name is null || name.Length == 0 || !IsIdentifier(name))
        {
            errors.Add("name must be a non-empty C# identifier");
        }

        if (role is null || role.Length == 0 || !Roles.Contains(role))
        {
            errors.Add("role is missing or unsupported");
        }

        if (recipe is null || recipe.Length == 0 || !Recipes.Contains(recipe))
        {
            errors.Add("recipe is missing or unsupported");
        }

        if (allowsChildren is null)
        {
            errors.Add("children is required");
        }

        if (propertyDeclarations == 0)
        {
            errors.Add("at least one property is required");
        }

        foreach (IGrouping<string, PropertyDefinition> duplicate in properties.GroupBy(
                     property => property.Name,
                     StringComparer.Ordinal).Where(group => group.Count() > 1))
        {
            errors.Add($"property '{duplicate.Key}' is declared more than once");
        }

        foreach (IGrouping<string, PropertyDefinition> duplicate in properties.GroupBy(
                     property => property.Target,
                     StringComparer.Ordinal).Where(group => group.Count() > 1))
        {
            errors.Add($"IR target '{duplicate.Key}' is assigned more than once");
        }

        if (recipe is not null && Recipes.Contains(recipe))
        {
            foreach (PropertyDefinition property in properties)
            {
                if (!IsTargetSupportedByRecipe(recipe, property.Target))
                {
                    errors.Add($"IR target '{property.Target}' is not supported by recipe '{recipe}'");
                }

                if (property.Binding == "Text"
                    && (property.Target != "Content" || recipe is not ("Text" or "CommandInput")))
                {
                    errors.Add("the Text binding must target Content on the Text or CommandInput recipe");
                }

                if (property.Binding == "Visibility" && property.Target != "Visibility")
                {
                    errors.Add("the Visibility binding must target Visibility");
                }

                if (property.Binding == "SemanticLabel" && property.Target != "Label")
                {
                    errors.Add("the SemanticLabel binding must target Label");
                }
            }
        }

        return errors.Count == 0
            ? ParseResult.Succeeded(fileName, new ControlDefinition(
                id!.Value,
                name!,
                role!,
                recipe!,
                allowsChildren!.Value,
                properties))
            : ParseResult.Failed(fileName, string.Join("; ", errors));
    }

    private static PropertyDefinition? ParseProperty(string value, int lineNumber, List<string> errors)
    {
        string[] parts = value.Split('|');
        if (parts.Length != 7)
        {
            errors.Add($"line {lineNumber} property needs seven pipe-separated fields");
            return null;
        }

        for (int index = 0; index < parts.Length; index++)
        {
            parts[index] = parts[index].Trim();
        }

        string name = parts[0];
        string kind = parts[1];
        string target = parts[2];
        string binding = parts[3];
        string dirty = parts[4];
        string requiredRaw = parts[5];
        string? defaultValue = parts[6].Length == 0 ? null : parts[6];

        bool valid = true;
        if (!IsIdentifier(name))
        {
            errors.Add($"line {lineNumber} has invalid property name '{name}'");
            valid = false;
        }

        valid &= ValidateToken(ValueKinds, kind, "value kind", lineNumber, errors);
        valid &= ValidateToken(Targets, target, "IR target", lineNumber, errors);
        valid &= ValidateToken(BindingProperties, binding, "binding property", lineNumber, errors);

        if (ValueKinds.Contains(kind) && Targets.Contains(target) && !IsCompatibleTarget(kind, target))
        {
            errors.Add($"line {lineNumber} value kind '{kind}' is incompatible with IR target '{target}'");
            valid = false;
        }

        string[] dirtyParts = dirty.Split(',');
        if (dirtyParts.Length == 0 || dirtyParts.Any(part => !DirtyKinds.Contains(part)))
        {
            errors.Add($"line {lineNumber} has unsupported dirty kinds '{dirty}'");
            valid = false;
        }

        if (binding == "None" && dirty != "None" || binding != "None" && dirty == "None")
        {
            errors.Add($"line {lineNumber} must pair bindings with non-None dirty kinds");
            valid = false;
        }

        if ((binding is "Text" or "SemanticLabel") && kind != "String"
            || binding == "Visibility" && kind != "Boolean")
        {
            errors.Add($"line {lineNumber} binding '{binding}' is incompatible with '{kind}'");
            valid = false;
        }

        if (binding == "Text" && dirty != "Layout,Paint"
            || binding == "Visibility" && dirty != "State"
            || binding == "SemanticLabel" && dirty != "Paint")
        {
            errors.Add($"line {lineNumber} binding '{binding}' uses an incompatible dirty-kind set '{dirty}'");
            valid = false;
        }

        bool required;
        if (requiredRaw == "true")
        {
            required = true;
        }
        else if (requiredRaw == "false")
        {
            required = false;
        }
        else
        {
            errors.Add($"line {lineNumber} required must be exactly true or false");
            required = false;
            valid = false;
        }

        if (required && defaultValue is not null)
        {
            errors.Add($"line {lineNumber} cannot be required and have a default");
            valid = false;
        }

        if (defaultValue is not null && !IsValidLiteral(kind, defaultValue))
        {
            errors.Add($"line {lineNumber} has invalid default '{defaultValue}' for '{kind}'");
            valid = false;
        }

        return valid
            ? new PropertyDefinition(name, kind, target, binding, dirtyParts, required, defaultValue)
            : null;
    }

    private static void Emit(SourceProductionContext context, ImmutableArray<ParseResult> results)
    {
        if (results.IsDefaultOrEmpty)
        {
            context.ReportDiagnostic(Diagnostic.Create(MissingCatalog, Location.None));
            EmitFailureStub(context);
            return;
        }

        bool failed = false;
        List<ControlDefinition> controls = new();
        foreach (ParseResult result in results.OrderBy(result => result.FileName, StringComparer.Ordinal))
        {
            if (result.Error is not null)
            {
                context.ReportDiagnostic(Diagnostic.Create(InvalidControl, Location.None, result.FileName, result.Error));
                failed = true;
            }
            else
            {
                controls.Add(result.Control!);
            }
        }

        foreach (IGrouping<int, ControlDefinition> duplicate in controls.GroupBy(control => control.Id).Where(group => group.Count() > 1))
        {
            context.ReportDiagnostic(Diagnostic.Create(InvalidCatalog, Location.None, $"id {duplicate.Key} is duplicated"));
            failed = true;
        }

        foreach (IGrouping<string, ControlDefinition> duplicate in controls.GroupBy(
                     control => control.Name,
                     StringComparer.Ordinal).Where(group => group.Count() > 1))
        {
            context.ReportDiagnostic(Diagnostic.Create(InvalidCatalog, Location.None, $"name '{duplicate.Key}' is duplicated"));
            failed = true;
        }

        controls.Sort(static (left, right) => left.Id != right.Id
            ? left.Id.CompareTo(right.Id)
            : StringComparer.Ordinal.Compare(left.Name, right.Name));
        for (int index = 0; index < controls.Count; index++)
        {
            if (controls[index].Id != index + 1)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    InvalidCatalog,
                    Location.None,
                    $"ids must be contiguous from 1; expected {index + 1} but found {controls[index].Id}"));
                failed = true;
                break;
            }
        }

        if (failed)
        {
            EmitFailureStub(context);
            return;
        }

        context.AddSource("PxmlControlCatalog.g.cs", SourceText.From(BuildSource(controls), Encoding.UTF8));
    }

    private static void EmitFailureStub(SourceProductionContext context)
    {
        const string source = """
            // <auto-generated />
            #nullable enable
            using System;
            using System.Collections.Generic;

            namespace PCL.Pxml;

            public enum PxmlIrNodeKind
            {
            }

            internal static class PxmlGeneratedControlCatalog
            {
                private static readonly IReadOnlyList<PxmlControlModel> EmptyModels = Array.Empty<PxmlControlModel>();
                private static readonly IReadOnlyList<string> EmptyNames = Array.Empty<string>();

                public static IReadOnlyList<PxmlControlModel> Models => EmptyModels;
                public static IReadOnlyList<string> Names => EmptyNames;

                public static bool TryGet(string name, out PxmlControlModel model)
                {
                    model = null!;
                    return false;
                }
            }
            """;
        context.AddSource("PxmlControlCatalog.g.cs", SourceText.From(source, Encoding.UTF8));
    }

    private static string BuildSource(IReadOnlyList<ControlDefinition> controls)
    {
        StringBuilder source = new();
        _ = source.AppendLine("// <auto-generated />");
        _ = source.AppendLine("#nullable enable");
        _ = source.AppendLine("using System;");
        _ = source.AppendLine("using System.Collections.Generic;");
        _ = source.AppendLine("using PCL.UI.Next;");
        _ = source.AppendLine();
        _ = source.AppendLine("namespace PCL.Pxml;");
        _ = source.AppendLine();
        _ = source.AppendLine("public enum PxmlIrNodeKind");
        _ = source.AppendLine("{");
        foreach (ControlDefinition control in controls)
        {
            _ = source.Append("    ").Append(control.Name).Append(" = ")
                .Append(control.Id.ToString(CultureInfo.InvariantCulture)).AppendLine(",");
        }

        _ = source.AppendLine("}");
        _ = source.AppendLine();
        _ = source.AppendLine("internal static class PxmlGeneratedControlCatalog");
        _ = source.AppendLine("{");
        _ = source.AppendLine("    private static readonly PxmlControlModel[] AllModels =");
        _ = source.AppendLine("    {");
        foreach (ControlDefinition control in controls)
        {
            _ = source.Append("        new(PxmlIrNodeKind.").Append(control.Name)
                .Append(", ").Append(Literal(control.Name))
                .Append(", XsrUiSemanticRole.").Append(control.Role)
                .Append(", PxmlRuntimeRecipe.").Append(control.Recipe)
                .Append(", ").Append(control.AllowsChildren ? "true" : "false").AppendLine(",");
            _ = source.AppendLine("            new PxmlControlPropertyModel[]");
            _ = source.AppendLine("            {");
            foreach (PropertyDefinition property in control.Properties)
            {
                _ = source.Append("                new(")
                    .Append(Literal(property.Name)).Append(", PxmlControlValueKind.").Append(property.Kind)
                    .Append(", PxmlIrPropertyTarget.").Append(property.Target).Append(", ")
                    .Append(property.Binding == "None" ? "null" : $"XsrUiStateProperty.{property.Binding}")
                    .Append(", ").Append(DirtyExpression(property.DirtyKinds))
                    .Append(", ").Append(property.Required ? "true" : "false")
                    .Append(", ").Append(property.DefaultValue is null ? "null" : Literal(property.DefaultValue))
                    .AppendLine("),");
            }

            _ = source.AppendLine("            }),");
        }

        _ = source.AppendLine("    };");
        _ = source.AppendLine();
        _ = source.AppendLine("    private static readonly string[] AllNames =");
        _ = source.AppendLine("    {");
        foreach (ControlDefinition control in controls)
        {
            _ = source.Append("        ").Append(Literal(control.Name)).AppendLine(",");
        }

        _ = source.AppendLine("    };");
        _ = source.AppendLine();
        _ = source.AppendLine("    private static readonly IReadOnlyList<PxmlControlModel> ReadOnlyModels = Array.AsReadOnly(AllModels);");
        _ = source.AppendLine("    private static readonly IReadOnlyList<string> ReadOnlyNames = Array.AsReadOnly(AllNames);");
        _ = source.AppendLine();
        _ = source.AppendLine("    public static IReadOnlyList<PxmlControlModel> Models => ReadOnlyModels;");
        _ = source.AppendLine("    public static IReadOnlyList<string> Names => ReadOnlyNames;");
        _ = source.AppendLine();
        _ = source.AppendLine("    public static bool TryGet(string name, out PxmlControlModel model)");
        _ = source.AppendLine("    {");
        _ = source.AppendLine("        switch (name)");
        _ = source.AppendLine("        {");
        for (int index = 0; index < controls.Count; index++)
        {
            _ = source.Append("            case ").Append(Literal(controls[index].Name)).Append(": model = AllModels[")
                .Append(index.ToString(CultureInfo.InvariantCulture)).AppendLine("]; return true;");
        }

        _ = source.AppendLine("            default: model = null!; return false;");
        _ = source.AppendLine("        }");
        _ = source.AppendLine("    }");
        _ = source.AppendLine("}");
        return source.ToString();
    }

    private static string DirtyExpression(IReadOnlyList<string> kinds) =>
        string.Join(" | ", kinds.Select(kind => $"XsrUiDirtyKinds.{kind}"));

    private static string Literal(string value)
    {
        StringBuilder literal = new("\"");
        foreach (char character in value)
        {
            _ = character switch
            {
                '\0' => literal.Append("\\0"),
                '\a' => literal.Append("\\a"),
                '\b' => literal.Append("\\b"),
                '\f' => literal.Append("\\f"),
                '\n' => literal.Append("\\n"),
                '\r' => literal.Append("\\r"),
                '\t' => literal.Append("\\t"),
                '\v' => literal.Append("\\v"),
                '\"' => literal.Append("\\\""),
                '\\' => literal.Append("\\\\"),
                '\u2028' => literal.Append("\\u2028"),
                '\u2029' => literal.Append("\\u2029"),
                _ when char.IsControl(character) => literal.Append("\\u").Append(
                    ((int)character).ToString("x4", CultureInfo.InvariantCulture)),
                _ => literal.Append(character),
            };
        }

        return literal.Append('\"').ToString();
    }

    private static void AssignOnce(
        ref string? target,
        string value,
        string key,
        int zeroBasedLine,
        List<string> errors)
    {
        if (target is not null)
        {
            errors.Add($"line {zeroBasedLine + 1} declares {key} more than once");
        }
        else
        {
            target = value;
        }
    }

    private static bool ValidateToken(
        HashSet<string> values,
        string value,
        string label,
        int lineNumber,
        List<string> errors)
    {
        if (values.Contains(value))
        {
            return true;
        }

        errors.Add($"line {lineNumber} has unsupported {label} '{value}'");
        return false;
    }

    private static bool IsIdentifier(string value)
    {
        if (string.IsNullOrEmpty(value)
            || SyntaxFacts.GetKeywordKind(value) != SyntaxKind.None
            || SyntaxFacts.GetContextualKeywordKind(value) != SyntaxKind.None
            || !(value[0] == '_' || char.IsLetter(value[0])))
        {
            return false;
        }

        for (int index = 1; index < value.Length; index++)
        {
            if (value[index] != '_' && !char.IsLetterOrDigit(value[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValidLiteral(string kind, string value)
    {
        switch (kind)
        {
            case "Boolean":
                return value == "true" || value == "false";
            case "Double":
                return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)
                    && !double.IsNaN(number)
                    && !double.IsInfinity(number);
            case "Thickness":
                string[] parts = value.Split(',');
                return (parts.Length == 1 || parts.Length == 4)
                    && parts.All(part => double.TryParse(
                            part,
                            NumberStyles.Float,
                            CultureInfo.InvariantCulture,
                            out double thickness)
                        && !double.IsNaN(thickness)
                        && !double.IsInfinity(thickness));
            case "Orientation":
                return value == "Vertical" || value == "Horizontal";
            case "Alignment":
                return value == "Stretch" || value == "Start" || value == "Center" || value == "End";
            case "SemanticId":
                return value.Length > 0 && !value.Any(character => char.IsWhiteSpace(character) || char.IsControl(character));
            case "String":
                return true;
            default:
                return false;
        }
    }

    private static bool IsCompatibleTarget(string kind, string target)
    {
        switch (kind)
        {
            case "Double":
                return target == "Width" || target == "Height"
                    || target == "MinWidth" || target == "MaxWidth"
                    || target == "MinHeight" || target == "MaxHeight"
                    || target == "Weight" || target == "Spacing";
            case "Thickness":
                return target == "Margin" || target == "Padding";
            case "Boolean":
                return target == "Visibility" || target == "Scrollable"
                    || target == "Focusable" || target == "Clickable" || target == "StretchLastChild";
            case "Orientation":
                return target == "Orientation";
            case "Alignment":
                return target == "HorizontalAlignment" || target == "VerticalAlignment";
            case "String":
                return target == "Label" || target == "Content" || target == "ImageSource"
                    || target == "Key";
            case "SemanticId":
                return target == "Command";
            default:
                return false;
        }
    }

    private static bool IsTargetSupportedByRecipe(string recipe, string target)
    {
        if (target == "Width" || target == "Height"
            || target == "MinWidth" || target == "MaxWidth"
            || target == "MinHeight" || target == "MaxHeight" || target == "Weight"
            || target == "HorizontalAlignment" || target == "VerticalAlignment"
            || target == "Margin" || target == "Padding" || target == "Visibility"
            || target == "Label" || target == "Key")
        {
            return true;
        }

        switch (recipe)
        {
            case "Element":
                return false;
            case "StackLayout":
                return target == "Orientation" || target == "Spacing" || target == "Scrollable"
                    || target == "StretchLastChild";
            case "Text":
                return target == "Content";
            case "CommandInput":
                return target == "Command" || target == "Focusable" || target == "Clickable"
                    || target == "Content" || target == "ImageSource";
            case "Image":
                return target == "ImageSource";
            default:
                return false;
        }
    }

    private sealed class ParseResult
    {
        private ParseResult(string fileName, ControlDefinition? control, string? error)
        {
            FileName = fileName;
            Control = control;
            Error = error;
        }

        public string FileName { get; }
        public ControlDefinition? Control { get; }
        public string? Error { get; }

        public static ParseResult Succeeded(string fileName, ControlDefinition control) =>
            new(fileName, control, null);

        public static ParseResult Failed(string fileName, string error) =>
            new(fileName, null, error);
    }

    private sealed class ControlDefinition
    {
        public ControlDefinition(
            int id,
            string name,
            string role,
            string recipe,
            bool allowsChildren,
            IReadOnlyList<PropertyDefinition> properties)
        {
            Id = id;
            Name = name;
            Role = role;
            Recipe = recipe;
            AllowsChildren = allowsChildren;
            Properties = properties;
        }

        public int Id { get; }
        public string Name { get; }
        public string Role { get; }
        public string Recipe { get; }
        public bool AllowsChildren { get; }
        public IReadOnlyList<PropertyDefinition> Properties { get; }
    }

    private sealed class PropertyDefinition
    {
        public PropertyDefinition(
            string name,
            string kind,
            string target,
            string binding,
            IReadOnlyList<string> dirtyKinds,
            bool required,
            string? defaultValue)
        {
            Name = name;
            Kind = kind;
            Target = target;
            Binding = binding;
            DirtyKinds = dirtyKinds;
            Required = required;
            DefaultValue = defaultValue;
        }

        public string Name { get; }
        public string Kind { get; }
        public string Target { get; }
        public string Binding { get; }
        public IReadOnlyList<string> DirtyKinds { get; }
        public bool Required { get; }
        public string? DefaultValue { get; }
    }
}
