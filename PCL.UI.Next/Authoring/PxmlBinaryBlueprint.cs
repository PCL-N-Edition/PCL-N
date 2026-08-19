// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace PCL.UI.Next;

/// <summary>Describes one compiled PXML binding that needs an application reader.</summary>
public sealed class PxmlBindingDescriptor
{
    public PxmlBindingDescriptor(
        uint propertyId,
        string propertyName,
        string expression,
        ReadOnlyMemory<ulong> dependencies)
    {
        PropertyId = propertyId;
        PropertyName = propertyName;
        Expression = expression;
        Dependencies = dependencies;
    }

    public uint PropertyId { get; }

    public string PropertyName { get; }

    public string Expression { get; }

    public ReadOnlyMemory<ulong> Dependencies { get; }
}

/// <summary>
/// Resolves already-compiled PXML binding descriptors to typed runtime selectors.
/// The Runtime never parses PXML source or evaluates arbitrary C# expressions.
/// </summary>
public interface IPxmlBindingResolver
{
    UiSelector<string> ResolveString(PxmlBindingDescriptor binding);

    UiSelector<bool> ResolveBoolean(PxmlBindingDescriptor binding);
}

/// <summary>Thrown when a PXB artifact violates the PXML Runtime ABI.</summary>
public sealed class PxmlBinaryException : Exception
{
    public PxmlBinaryException(string message)
        : base(message)
    {
    }
}

internal static class PxmlBinaryBlueprint
{
    private const int HeaderSize = 36;
    private const int DirectoryEntrySize = 32;
    private const int NodeSize = 44;
    private const int PropertySize = 24;
    private const int BindingSize = 28;
    private const uint None = uint.MaxValue;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static UiBlueprint Read(
        ReadOnlySpan<byte> binary,
        string name,
        IPxmlBindingResolver? bindingResolver)
    {
        if (string.IsNullOrWhiteSpace(name))
            name = "PXML";

        Package package = Package.Parse(binary);
        StringTable strings = StringTable.Parse(package.Required(FourCc("STRS")));
        ReadOnlySpan<byte> propertiesSection = package.Required(FourCc("PROP"));
        ReadOnlySpan<byte> bindingsSection = package.Required(FourCc("BIND"));
        NodeDraft[] drafts = ReadNodes(package.Required(FourCc("NODE")));
        ReadProperties(propertiesSection, strings, drafts);
        BlueprintBinding[] bindings = ReadBindings(
            bindingsSection,
            package.Required(FourCc("DEPS")),
            strings,
            drafts,
            bindingResolver);
        ValidateMeta(
            package.Required(FourCc("META")),
            drafts.Length,
            checked((int)ReadCount(propertiesSection, PropertySize, "PROP")),
            checked((int)ReadCount(bindingsSection, BindingSize, "BIND")));
        int rootIndex = ValidateTree(drafts);

        BlueprintNode[] nodes = new BlueprintNode[drafts.Length];
        for (int index = 0; index < drafts.Length; index++)
            nodes[index] = drafts[index].Build();

        return new UiBlueprint(
            name,
            nodes,
            bindings,
            rootIndex,
            BlueprintDependencyIndex.Build(bindings));
    }

    private static NodeDraft[] ReadNodes(ReadOnlySpan<byte> section)
    {
        uint count = ReadCount(section, NodeSize, "NODE");
        if (count == 0)
            throw Error("NODE must contain a root node.");

        NodeDraft[] drafts = new NodeDraft[checked((int)count)];
        for (int index = 0; index < drafts.Length; index++)
        {
            ReadOnlySpan<byte> entry = section.Slice(4 + index * NodeSize, NodeSize);
            uint kindId = U32(entry, 12);
            UiNodeKind kind = kindId switch
            {
                1 => UiNodeKind.Column,
                2 => UiNodeKind.Row,
                3 => UiNodeKind.Container,
                4 => UiNodeKind.Text,
                7 => UiNodeKind.Grid,
                8 => UiNodeKind.Overlay,
                9 => UiNodeKind.Absolute,
                10 => UiNodeKind.Scroll,
                11 => UiNodeKind.VirtualList,
                12 => UiNodeKind.NativeHost,
                5 => throw Error(
                    "PXB contains legacy Button node kind 5. Framework controls must be expanded to primitive Node entries by the PXML Compiler."),
                6 => throw Error(
                    "PXB structural node kind 6 requires a STRU program, which this Runtime ABI revision does not accept."),
                _ => throw Error($"PXB contains unsupported node kind {kindId} at index {index}.")
            };

            drafts[index] = new NodeDraft
            {
                Kind = kind,
                Parent = U32(entry, 0),
                FirstChild = U32(entry, 4),
                ChildCount = U32(entry, 8),
                PropertyOffset = U32(entry, 16),
                PropertyCount = U32(entry, 20),
                BindingOffset = U32(entry, 24),
                BindingCount = U32(entry, 28),
                Layout = LayoutStyle.Default,
                TextFormat = TextFormat.Default,
                ScrollViewport = ScrollViewport.Vertical,
                Virtualization = Virtualization.Default,
                SemanticRole = kind == UiNodeKind.Text ? UiSemanticRole.StaticText : null
            };
        }
        return drafts;
    }

    private static void ReadProperties(
        ReadOnlySpan<byte> section,
        StringTable strings,
        NodeDraft[] drafts)
    {
        uint count = ReadCount(section, PropertySize, "PROP");
        ValidateRanges(drafts, count, bindings: false);
        for (int index = 0; index < checked((int)count); index++)
        {
            ReadOnlySpan<byte> entry = section.Slice(4 + index * PropertySize, PropertySize);
            int nodeIndex = ToIndex(U32(entry, 0), drafts.Length, "PROP node");
            NodeDraft owner = drafts[nodeIndex];
            if ((uint)index < owner.PropertyOffset ||
                (uint)index >= owner.PropertyOffset + owner.PropertyCount)
                throw Error($"PROP[{index}] is not inside NODE[{nodeIndex}] property range.");

            uint propertyId = U32(entry, 4);
            string propertyName = strings.Get(U32(entry, 12));
            string value = strings.Get(U32(entry, 16));
            if (propertyId != StableId("property", propertyName))
                throw Error($"PROP[{index}] has a stable property ID mismatch for '{propertyName}'.");
            ApplyProperty(owner, propertyName, value);
        }

        foreach (NodeDraft draft in drafts)
            draft.IsHitTestVisible |= draft.Behaviors != UiBehavior.None;
    }

    private static BlueprintBinding[] ReadBindings(
        ReadOnlySpan<byte> section,
        ReadOnlySpan<byte> dependenciesSection,
        StringTable strings,
        NodeDraft[] drafts,
        IPxmlBindingResolver? resolver)
    {
        uint count = ReadCount(section, BindingSize, "BIND");
        uint dependencyCount = ReadCount(dependenciesSection, 8, "DEPS");
        ValidateRanges(drafts, count, bindings: true);
        List<BlueprintBinding> runtimeBindings = [];

        for (int index = 0; index < checked((int)count); index++)
        {
            ReadOnlySpan<byte> entry = section.Slice(4 + index * BindingSize, BindingSize);
            int nodeIndex = ToIndex(U32(entry, 0), drafts.Length, "BIND node");
            NodeDraft owner = drafts[nodeIndex];
            if ((uint)index < owner.BindingOffset ||
                (uint)index >= owner.BindingOffset + owner.BindingCount)
                throw Error($"BIND[{index}] is not inside NODE[{nodeIndex}] binding range.");

            uint propertyId = U32(entry, 4);
            uint markupKind = U32(entry, 8);
            string propertyName = strings.Get(U32(entry, 12));
            string expression = strings.Get(U32(entry, 16));
            uint dependencyOffset = U32(entry, 20);
            uint itemDependencyCount = U32(entry, 24);
            if (propertyId != StableId("property", propertyName))
                throw Error($"BIND[{index}] has a stable property ID mismatch for '{propertyName}'.");
            if ((ulong)dependencyOffset + itemDependencyCount > dependencyCount)
                throw Error($"BIND[{index}] dependency range is outside DEPS.");

            if (markupKind == 2) // {cmd ...}
            {
                if (!string.Equals(propertyName, "Command", StringComparison.Ordinal))
                    throw Error($"BIND[{index}] applies a command to unsupported property '{propertyName}'.");
                owner.CommandId = string.Equals(expression, "None", StringComparison.Ordinal)
                    ? 0
                    : unchecked((int)StableId("command", expression));
                continue;
            }

            if (markupKind is not (1 or 5)) // {bind ...} / {loc ...}
                throw Error($"BIND[{index}] markup kind {markupKind} is not implemented by the current Runtime ABI.");
            if (resolver is null)
                throw Error($"PXB binding '{propertyName}={{{expression}}}' requires an IPxmlBindingResolver.");

            ulong[] stableDependencies = new ulong[itemDependencyCount];
            for (int dependencyIndex = 0; dependencyIndex < stableDependencies.Length; dependencyIndex++)
            {
                int offset = checked(4 + ((int)dependencyOffset + dependencyIndex) * 8);
                stableDependencies[dependencyIndex] = U64(dependenciesSection, offset);
            }
            PxmlBindingDescriptor descriptor = new(
                propertyId,
                propertyName,
                expression,
                stableDependencies);

            if (string.Equals(propertyName, "Text", StringComparison.Ordinal))
            {
                UiSelector<string> selector = resolver.ResolveString(descriptor);
                ValidateSelector(selector.Id, selector.DependencySlices, selector.Read, propertyName, stableDependencies);
                runtimeBindings.Add(new BlueprintBinding(
                    selector.Id,
                    nodeIndex,
                    selector.DependencySlices,
                    BlueprintBindingKind.Text,
                    readString: selector.Read));
            }
            else if (string.Equals(propertyName, "Value", StringComparison.Ordinal) &&
                     owner.Kind == UiNodeKind.NativeHost)
            {
                UiSelector<string> selector = resolver.ResolveString(descriptor);
                ValidateSelector(selector.Id, selector.DependencySlices, selector.Read, propertyName, stableDependencies);
                runtimeBindings.Add(new BlueprintBinding(
                    selector.Id,
                    nodeIndex,
                    selector.DependencySlices,
                    BlueprintBindingKind.NativeValue,
                    readString: selector.Read));
            }
            else if (string.Equals(propertyName, "Condition", StringComparison.Ordinal))
            {
                UiSelector<bool> selector = resolver.ResolveBoolean(descriptor);
                ValidateSelector(selector.Id, selector.DependencySlices, selector.Read, propertyName, stableDependencies);
                runtimeBindings.Add(new BlueprintBinding(
                    selector.Id,
                    nodeIndex,
                    selector.DependencySlices,
                    BlueprintBindingKind.Condition,
                    readBool: selector.Read));
            }
            else
            {
                throw Error($"Dynamic PXML property '{propertyName}' is not supported by UiBlueprint.");
            }
        }
        return runtimeBindings.ToArray();
    }

    private static void ValidateSelector<T>(
        int selectorId,
        ReadOnlySpan<int> dependencySlices,
        Func<PresentationStore, T>? read,
        string propertyName,
        ReadOnlySpan<ulong> compiledDependencies)
    {
        if (compiledDependencies.IsEmpty)
            throw Error($"Compiled binding '{propertyName}' has no stable dependencies.");
        if (selectorId <= 0 || dependencySlices.IsEmpty || read is null)
            throw Error($"Binding resolver returned an invalid selector for '{propertyName}'.");
    }

    private static void ApplyProperty(NodeDraft draft, string name, string value)
    {
        switch (name)
        {
            case "Text": draft.StaticText = value; break;
            case "Class": ApplyClasses(draft, value); break;
            case "Behaviors": draft.Behaviors |= ParseFlags<UiBehavior>(value, BehaviorAlias); break;
            case "Command": throw Error("Command must be emitted as a compiled {cmd ...} binding.");
            case "Width": draft.Layout.Width = ParseLength(value); break;
            case "Height": draft.Layout.Height = ParseLength(value); break;
            case "MinWidth": draft.Layout.MinSize = draft.Layout.MinSize with { Width = ParsePixels(value, name) }; break;
            case "MinHeight": draft.Layout.MinSize = draft.Layout.MinSize with { Height = ParsePixels(value, name) }; break;
            case "MaxWidth": draft.Layout.MaxSize = draft.Layout.MaxSize with { Width = ParsePixels(value, name) }; break;
            case "MaxHeight": draft.Layout.MaxSize = draft.Layout.MaxSize with { Height = ParsePixels(value, name) }; break;
            case "Padding": draft.Layout.Padding = ParseThickness(value); break;
            case "Margin": draft.Layout.Margin = ParseThickness(value); break;
            case "Gap": draft.LayoutGap = ParseNonNegative(value, name); break;
            case "Focusable":
                if (ParseBoolean(value, name)) draft.Behaviors |= UiBehavior.Focusable;
                break;
            case "Focus.Scope": draft.IsFocusScope = ParseBoolean(value, name); break;
            case "Focus.Trap": draft.IsFocusTrap = ParseBoolean(value, name); break;
            case "Focus.Restore": draft.RestorePreviousFocus = ParseBoolean(value, name); break;
            case "Kind": draft.NativeHost.Kind = ParseEnum<UiNativeHostKind>(value, name); break;
            case "Value": draft.NativeHost.Value = value; break;
            case "Placeholder": draft.NativeHost.Placeholder = value; break;
            case "EstimatedItemHeight": draft.Virtualization.EstimatedItemExtent = ParseNonNegative(value, name); break;
            case "OverscanBefore": draft.Virtualization.OverscanBefore = ParseU16(value, name); break;
            case "OverscanAfter": draft.Virtualization.OverscanAfter = ParseU16(value, name); break;
            case "AccessibleRole": draft.SemanticRole = ParseEnum<UiSemanticRole>(value, name); break;
            case "AccessibleName": draft.AccessibleName = value; break;
            case "AccessibleDescription": draft.AccessibleDescription = value; break;
            case "AccessibleValue": draft.AccessibleValue = value; break;
            case "AccessibleState":
                draft.AccessibleState = ParseFlags<UiAccessibleState>(value, static token => token);
                break;
            case "AccessibleActions":
                draft.AccessibleActions = ParseFlags<UiAccessibleAction>(value, static token => token);
                break;
            case "x:Name":
            case "x:Key":
            case "xmlns":
                break;
            default:
                throw Error($"Static PXML property '{name}' is not supported by UiBlueprint Runtime ABI.");
        }
    }

    private static void ApplyClasses(NodeDraft draft, string value)
    {
        foreach (string token in Tokens(value))
        {
            int id = token switch
            {
                "Button" => UiClass.Button.Id,
                "PageTitle" => UiClass.PageTitle.Id,
                "Body" => UiClass.Body.Id,
                "Card" => UiClass.Card.Id,
                "ModalBarrier" => UiClass.ModalBarrier.Id,
                _ => unchecked((int)(StableId("style-class", token) & 0x7fff_ffffU))
            };
            if (id == 0 || draft.StyleClassIds.Contains(id))
                continue;
            if (draft.StyleClassIds.Count >= StyleClassSet.MaxInlineCount)
                throw Error($"Node declares more than {StyleClassSet.MaxInlineCount} inline classes.");
            draft.StyleClassIds.Add(id);
        }
    }

    private static string BehaviorAlias(string token) => token switch
    {
        "Hover" => nameof(UiBehavior.Hoverable),
        "Press" => nameof(UiBehavior.Pressable),
        "Click" => nameof(UiBehavior.Clickable),
        "Focus" => nameof(UiBehavior.Focusable),
        _ => token
    };

    private static T ParseFlags<T>(string value, Func<string, string> normalize)
        where T : struct, Enum
    {
        ulong combined = 0;
        foreach (string token in Tokens(value))
        {
            string normalized = normalize(token);
            if (!Enum.TryParse(normalized, ignoreCase: false, out T parsed))
                throw Error($"'{token}' is not a valid {typeof(T).Name} value.");
            combined |= Convert.ToUInt64(parsed, CultureInfo.InvariantCulture);
        }
        return (T)Enum.ToObject(typeof(T), combined);
    }

    private static string[] Tokens(string value) =>
        value.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static T ParseEnum<T>(string value, string property)
        where T : struct, Enum =>
        Enum.TryParse(value, ignoreCase: false, out T parsed)
            ? parsed
            : throw Error($"Property '{property}' has invalid value '{value}'.");

    private static bool ParseBoolean(string value, string property) => value switch
    {
        "true" => true,
        "false" => false,
        _ => throw Error($"Property '{property}' has invalid boolean '{value}'.")
    };

    private static float ParseNonNegative(string value, string property)
    {
        if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed) ||
            !float.IsFinite(parsed) || parsed < 0f)
            throw Error($"Property '{property}' has invalid number '{value}'.");
        return parsed;
    }

    private static float ParsePixels(string value, string property)
    {
        UiLength parsed = ParseLength(value);
        if (parsed.Kind != UiLengthKind.Pixels)
            throw Error($"Property '{property}' currently requires a pixel length.");
        return parsed.Value;
    }

    private static UiLength ParseLength(string value)
    {
        if (string.Equals(value, "auto", StringComparison.OrdinalIgnoreCase)) return UiLength.Auto;
        if (string.Equals(value, "min-content", StringComparison.OrdinalIgnoreCase)) return new(UiLengthKind.MinContent, 0);
        if (string.Equals(value, "max-content", StringComparison.OrdinalIgnoreCase)) return new(UiLengthKind.MaxContent, 0);
        if (value.EndsWith('%')) return UiLength.Percent(ParseNonNegative(value[..^1], "length") / 100f);
        if (value.EndsWith('*'))
            return UiLength.Star(value.Length == 1 ? 1f : ParseNonNegative(value[..^1], "length"));
        return UiLength.Pixels(ParseNonNegative(value, "length"));
    }

    private static UiThickness ParseThickness(string value)
    {
        string[] parts = value.Split(',', StringSplitOptions.TrimEntries);
        float[] numbers = parts.Select(part => ParseNonNegative(part, "thickness")).ToArray();
        return numbers.Length switch
        {
            1 => new UiThickness(numbers[0]),
            2 => new UiThickness(numbers[0], numbers[1]),
            4 => new UiThickness(numbers[0], numbers[1], numbers[2], numbers[3]),
            _ => throw Error($"Invalid thickness '{value}'.")
        };
    }

    private static ushort ParseU16(string value, string property) =>
        ushort.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out ushort parsed)
            ? parsed
            : throw Error($"Property '{property}' has invalid integer '{value}'.");

    private static int ValidateTree(NodeDraft[] drafts)
    {
        int root = -1;
        bool[] referenced = new bool[drafts.Length];
        for (int index = 0; index < drafts.Length; index++)
        {
            NodeDraft draft = drafts[index];
            if (draft.Parent == None)
            {
                if (root >= 0) throw Error("NODE contains more than one root.");
                root = index;
            }
            else
            {
                _ = ToIndex(draft.Parent, drafts.Length, $"NODE[{index}] parent");
            }

            if (draft.ChildCount == 0)
            {
                if (draft.FirstChild != None)
                    throw Error($"NODE[{index}] has FirstChild without children.");
                continue;
            }
            if (draft.FirstChild == None || (ulong)draft.FirstChild + draft.ChildCount > (uint)drafts.Length)
                throw Error($"NODE[{index}] child range is invalid.");

            for (uint childOffset = 0; childOffset < draft.ChildCount; childOffset++)
            {
                int child = checked((int)(draft.FirstChild + childOffset));
                if (drafts[child].Parent != (uint)index || referenced[child])
                    throw Error($"NODE[{index}] has an inconsistent child edge to {child}.");
                referenced[child] = true;
                drafts[child].NextSiblingIndex = childOffset + 1 < draft.ChildCount ? child + 1 : -1;
            }
            draft.FirstChildIndex = checked((int)draft.FirstChild);
        }
        if (root < 0) throw Error("NODE has no root.");
        for (int index = 0; index < drafts.Length; index++)
        {
            if (index != root && !referenced[index])
                throw Error($"NODE[{index}] is unreachable from the root.");
        }
        return root;
    }

    private static void ValidateRanges(NodeDraft[] drafts, uint count, bool bindings)
    {
        bool[] covered = new bool[checked((int)count)];
        for (int nodeIndex = 0; nodeIndex < drafts.Length; nodeIndex++)
        {
            uint offset = bindings ? drafts[nodeIndex].BindingOffset : drafts[nodeIndex].PropertyOffset;
            uint length = bindings ? drafts[nodeIndex].BindingCount : drafts[nodeIndex].PropertyCount;
            if ((ulong)offset + length > count)
                throw Error($"NODE[{nodeIndex}] {(bindings ? "binding" : "property")} range is invalid.");
            for (uint item = offset; item < offset + length; item++)
            {
                if (covered[item])
                    throw Error($"NODE {(bindings ? "binding" : "property")} ranges overlap at {item}.");
                covered[item] = true;
            }
        }
        if (covered.Any(static value => !value))
            throw Error($"{(bindings ? "BIND" : "PROP")} contains entries not owned by a NODE range.");
    }

    private static uint ReadCount(ReadOnlySpan<byte> section, int itemSize, string name)
    {
        if (section.Length < 4) throw Error($"{name} section is truncated.");
        uint count = U32(section, 0);
        if ((ulong)count * (uint)itemSize + 4 != (ulong)section.Length)
            throw Error($"{name} section length does not match its item count.");
        return count;
    }

    private static void ValidateMeta(
        ReadOnlySpan<byte> section,
        int nodeCount,
        int propertyCount,
        int bindingCount)
    {
        if (section.Length != 40) throw Error("META section must contain ten u32 values.");
        if (U32(section, 12) != 1) throw Error("PXB language major version is not PXML 1.x.");
        if (U32(section, 28) != (uint)nodeCount ||
            U32(section, 32) != (uint)propertyCount ||
            U32(section, 36) != (uint)bindingCount)
            throw Error("META counts do not match Runtime sections.");
    }

    private static int ToIndex(uint value, int count, string field)
    {
        if (value >= (uint)count) throw Error($"{field} index {value} is out of range.");
        return checked((int)value);
    }

    private static uint StableId(string domain, string name)
    {
        ulong hash = Hash64(Encoding.UTF8.GetBytes(domain), 0x50584d4cUL);
        hash = Hash64(Encoding.UTF8.GetBytes(name), hash);
        return (uint)(hash ^ (hash >> 32));
    }

    private static ulong Hash64(ReadOnlySpan<byte> data, ulong seed)
    {
        ulong hash = 14695981039346656037UL ^ seed;
        foreach (byte value in data)
        {
            hash ^= value;
            hash *= 1099511628211UL;
        }
        hash ^= hash >> 32;
        hash *= 0xd6e8feb86659fd93UL;
        hash ^= hash >> 32;
        return hash;
    }

    private static uint FourCc(string value) =>
        (uint)value[0] | ((uint)value[1] << 8) | ((uint)value[2] << 16) | ((uint)value[3] << 24);

    private static uint U32(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));

    private static ulong U64(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset, 8));

    private static PxmlBinaryException Error(string message) => new(message);

    private sealed class NodeDraft
    {
        public UiNodeKind Kind;
        public uint Parent;
        public uint FirstChild;
        public uint ChildCount;
        public uint PropertyOffset;
        public uint PropertyCount;
        public uint BindingOffset;
        public uint BindingCount;
        public int FirstChildIndex = -1;
        public int NextSiblingIndex = -1;
        public List<int> StyleClassIds { get; } = [];
        public UiBehavior Behaviors;
        public int CommandId;
        public string? StaticText;
        public LayoutStyle Layout;
        public float LayoutGap;
        public TextFormat TextFormat;
        public bool IsHitTestVisible;
        public bool IsFocusScope;
        public bool IsFocusTrap;
        public bool RestorePreviousFocus = true;
        public ScrollViewport ScrollViewport;
        public Virtualization Virtualization;
        public NativeHostComponent NativeHost;
        public UiSemanticRole? SemanticRole;
        public string? AccessibleName;
        public string? AccessibleDescription;
        public string? AccessibleValue;
        public UiAccessibleState AccessibleState;
        public UiAccessibleAction AccessibleActions;

        public BlueprintNode Build() => new(
            Kind,
            Parent == None ? -1 : checked((int)Parent),
            FirstChildIndex,
            NextSiblingIndex,
            StyleClassIds.ToArray(),
            Behaviors,
            CommandId,
            StaticText,
            -1,
            -1,
            -1,
            Layout,
            LayoutGap,
            ReadOnlySpan<UiGridTrack>.Empty,
            ReadOnlySpan<UiGridTrack>.Empty,
            GridPlacement.Default,
            false,
            default,
            false,
            TextFormat,
            IsHitTestVisible,
            0,
            IsFocusScope,
            IsFocusTrap,
            RestorePreviousFocus,
            UiGestureMask.None,
            default,
            default,
            ScrollViewport,
            Virtualization,
            NativeHost,
            SemanticRole is UiSemanticRole role
                ? new SemanticDefinition(
                    role,
                    AccessibleName,
                    AccessibleDescription,
                    AccessibleValue,
                    AccessibleState,
                    AccessibleActions)
                : default);
    }

    private readonly ref struct Package
    {
        private readonly ReadOnlySpan<byte> _binary;
        private readonly ReadOnlySpan<byte> _directory;

        private Package(ReadOnlySpan<byte> binary, ReadOnlySpan<byte> directory)
        {
            _binary = binary;
            _directory = directory;
        }

        public static Package Parse(ReadOnlySpan<byte> binary)
        {
            if (binary.Length < HeaderSize || !binary[..4].SequenceEqual("PXB1"u8))
                throw Error("Input is not a PXB1 binary.");
            if (BinaryPrimitives.ReadUInt16LittleEndian(binary[4..]) != 1 ||
                BinaryPrimitives.ReadUInt16LittleEndian(binary[6..]) != 0 ||
                U32(binary, 32) != HeaderSize)
                throw Error("Unsupported PXB format version or header size.");

            uint count = U32(binary, 28);
            ulong directoryEnd = HeaderSize + (ulong)count * DirectoryEntrySize;
            if (directoryEnd > (ulong)binary.Length)
                throw Error("PXB section directory is truncated.");

            ReadOnlySpan<byte> directory = binary.Slice(HeaderSize, checked((int)(directoryEnd - HeaderSize)));
            List<(ulong Offset, ulong Size)> ranges = [];
            HashSet<uint> types = [];
            ulong payloadStart = Align(directoryEnd, 16);
            for (int index = 0; index < count; index++)
            {
                ReadOnlySpan<byte> entry = directory.Slice(index * DirectoryEntrySize, DirectoryEntrySize);
                uint type = U32(entry, 0);
                ulong offset = U64(entry, 8);
                ulong size = U64(entry, 16);
                uint alignment = U32(entry, 24);
                if (!types.Add(type)) throw Error("PXB contains a duplicate section type.");
                if (alignment == 0 || offset < payloadStart || offset % alignment != 0 ||
                    offset > (ulong)binary.Length || size > (ulong)binary.Length - offset)
                    throw Error($"PXB section directory entry {index} is invalid.");
                foreach ((ulong otherOffset, ulong otherSize) in ranges)
                {
                    if (size != 0 && otherSize != 0 &&
                        offset < otherOffset + otherSize && otherOffset < offset + size)
                        throw Error("PXB sections overlap.");
                }
                ranges.Add((offset, size));
            }

            ulong expectedLow = Hash64(binary[HeaderSize..], 0x50584231UL);
            ulong expectedHigh = Hash64(binary[HeaderSize..], 0x50584232UL);
            if (U64(binary, 12) != expectedLow || U64(binary, 20) != expectedHigh)
                throw Error("PXB content fingerprint mismatch.");
            return new Package(binary, directory);
        }

        public ReadOnlySpan<byte> Required(uint type)
        {
            for (int offset = 0; offset < _directory.Length; offset += DirectoryEntrySize)
            {
                ReadOnlySpan<byte> entry = _directory.Slice(offset, DirectoryEntrySize);
                if (U32(entry, 0) != type) continue;
                return _binary.Slice(checked((int)U64(entry, 8)), checked((int)U64(entry, 16)));
            }
            throw Error("PXB is missing a required Runtime section.");
        }

        private static ulong Align(ulong value, ulong alignment) =>
            (value + alignment - 1) / alignment * alignment;
    }

    private readonly ref struct StringTable
    {
        private readonly ReadOnlySpan<byte> _blob;

        private StringTable(ReadOnlySpan<byte> blob)
        {
            _blob = blob;
        }

        public static StringTable Parse(ReadOnlySpan<byte> section)
        {
            if (section.Length < 4) throw Error("STRS section is truncated.");
            uint count = U32(section, 0);
            ulong blobOffset = 4UL + (ulong)count * 4;
            if (blobOffset > (ulong)section.Length) throw Error("STRS offset table is truncated.");
            ReadOnlySpan<byte> blob = section[checked((int)blobOffset)..];
            for (int index = 0; index < count; index++)
            {
                uint offset = U32(section, 4 + index * 4);
                if (offset >= (uint)blob.Length || blob[(int)offset..].IndexOf((byte)0) < 0)
                    throw Error($"STRS[{index}] offset is invalid.");
            }
            return new StringTable(blob);
        }

        public string Get(uint offset)
        {
            if (offset >= (uint)_blob.Length) throw Error($"String offset {offset} is outside STRS.");
            ReadOnlySpan<byte> tail = _blob[(int)offset..];
            int length = tail.IndexOf((byte)0);
            if (length < 0) throw Error($"String offset {offset} has no NUL terminator.");
            try
            {
                return StrictUtf8.GetString(tail[..length]);
            }
            catch (DecoderFallbackException exception)
            {
                throw new PxmlBinaryException("STRS contains invalid UTF-8: " + exception.Message);
            }
        }
    }
}
