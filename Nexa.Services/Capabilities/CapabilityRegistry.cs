using System.Collections.Frozen;
using System.Globalization;

namespace Nexa.Services.Capabilities;

public enum CapabilityKind { Fact, Metric, Action, Constraint, Derived, Estimate, Policy }
public enum CapabilityAvailability { Available, Unknown, NotImplemented, PlatformUnsupported, DependencyMissing, PermissionDenied, TemporarilyUnavailable }
public enum CapabilityConfidence { Unknown, Low, Medium, High }
public enum CapabilityPermissionState { NotRequired, Granted, Required, Denied }
public enum CapabilityStability { Static, Session, Dynamic }

public interface ICapabilityDefinition
{
    string Id { get; }
    string Label { get; }
    string Group { get; }
    string Provider { get; }
    CapabilityKind Kind { get; }
    IReadOnlyList<string> Requirements { get; }
    CapabilityStability Stability { get; }
    bool Accepts(ICapability observation);
    ICapability Unavailable(CapabilityAvailability availability, DateTimeOffset timestamp, string reason);
}

public interface ICapability
{
    ICapabilityDefinition Definition { get; }
    string Id { get; }
    CapabilityAvailability Availability { get; }
    CapabilityConfidence Confidence { get; }
    CapabilityPermissionState Permission { get; }
    DateTimeOffset Timestamp { get; }
    string Source { get; }
    string Reason { get; }
    string DisplayValue { get; }
}

public sealed class CapabilityDefinition<T> : ICapabilityDefinition
{
    public CapabilityDefinition(string id, string label, string group, string provider, CapabilityKind kind = CapabilityKind.Fact,
        CapabilityStability stability = CapabilityStability.Session, IEnumerable<string>? requirements = null, string unit = "")
    {
        CapabilityRegistry.ValidateId(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        Id = id; Label = label; Group = group; Provider = provider; Kind = kind; Stability = stability; Unit = unit;
        Requirements = Array.AsReadOnly((requirements ?? []).ToArray());
    }
    public string Id { get; }
    public string Label { get; }
    public string Group { get; }
    public string Provider { get; }
    public CapabilityKind Kind { get; }
    public CapabilityStability Stability { get; }
    public IReadOnlyList<string> Requirements { get; }
    public string Unit { get; }
    public bool Accepts(ICapability observation) => observation is Capability<T> && ReferenceEquals(observation.Definition, this);
    public ICapability Unavailable(CapabilityAvailability availability, DateTimeOffset timestamp, string reason) =>
        new Capability<T>(this, default, availability, CapabilityConfidence.Unknown, timestamp, "", reason);
    public Capability<T> Observe(T value, DateTimeOffset timestamp, string source, CapabilityConfidence confidence = CapabilityConfidence.High) =>
        new(this, value, CapabilityAvailability.Available, confidence, timestamp, source, "");
}

public sealed record Capability<T>(CapabilityDefinition<T> TypedDefinition, T? Value, CapabilityAvailability Availability,
    CapabilityConfidence Confidence, DateTimeOffset Timestamp, string Source, string Reason,
    CapabilityPermissionState Permission = CapabilityPermissionState.NotRequired) : ICapability
{
    public ICapabilityDefinition Definition => TypedDefinition;
    public string Id => TypedDefinition.Id;
    public string DisplayValue => Availability != CapabilityAvailability.Available ? "—" : Value switch
    {
        bool flag => flag ? "是" : "否",
        long bytes when TypedDefinition.Unit == "bytes" => $"{bytes / 1073741824d:0.##} GiB",
        IFormattable number => number.ToString(null, CultureInfo.InvariantCulture) + (TypedDefinition.Unit.Length == 0 ? "" : " " + TypedDefinition.Unit),
        _ => Value?.ToString() ?? "—",
    };
}

/// <summary>Sealed machine observation schema, separate from executable Fabric offers.</summary>
public sealed class CapabilityRegistry
{
    private static readonly FrozenSet<string> Roots = "platform runtime jvmhost process cpu memory gpu storage filesystem display power thermal formfactor input handheld network shell security ai cloud minecraft java loader mod resource world account hook estimate diagnostics machine policy launch preflight change instance remediation observation"
        .Split(' ').ToFrozenSet(StringComparer.Ordinal);
    public CapabilityRegistry(IEnumerable<ICapabilityDefinition> definitions)
    {
        var items = definitions.OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
        if (items.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != items.Length)
            throw new ArgumentException("Duplicate capability ID.", nameof(definitions));
        Definitions = Array.AsReadOnly(items);
        ById = items.ToFrozenDictionary(item => item.Id, StringComparer.Ordinal);
        HashSet<string> visited = new(StringComparer.Ordinal), active = new(StringComparer.Ordinal);
        List<ICapabilityDefinition> ordered = [];
        void Visit(ICapabilityDefinition item)
        {
            if (visited.Contains(item.Id)) return;
            if (!active.Add(item.Id)) throw new ArgumentException("Capability dependency cycle.", nameof(definitions));
            foreach (string dependency in item.Requirements)
            {
                if (!ById.TryGetValue(dependency, out var required)) throw new ArgumentException("Missing capability dependency: " + dependency, nameof(definitions));
                Visit(required);
            }
            active.Remove(item.Id); visited.Add(item.Id); ordered.Add(item);
        }
        foreach (var item in items) { ValidateId(item.Id); Visit(item); }
        DependencyOrder = ordered.AsReadOnly();
    }
    public IReadOnlyList<ICapabilityDefinition> Definitions { get; }
    public FrozenDictionary<string, ICapabilityDefinition> ById { get; }
    internal IReadOnlyList<ICapabilityDefinition> DependencyOrder { get; }
    public static void ValidateId(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var parts = id.Split('.');
        if (parts.Length < 2 || !Roots.Contains(parts[0]) || parts.Any(part => part.Length == 0 || part.Any(c => !(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_'))))
            throw new ArgumentException("Invalid machine capability ID: " + id, nameof(id));
    }
}

public sealed class MachineCapabilitySnapshot
{
    public MachineCapabilitySnapshot(long revision, DateTimeOffset timestamp, IEnumerable<ICapability> values)
    {
        Revision = revision; Timestamp = timestamp;
        Values = Array.AsReadOnly(values.OrderBy(value => value.Id, StringComparer.Ordinal).ToArray());
        _byId = Values.ToFrozenDictionary(value => value.Id, StringComparer.Ordinal);
    }
    private readonly FrozenDictionary<string, ICapability> _byId;
    public long Revision { get; }
    public DateTimeOffset Timestamp { get; }
    public IReadOnlyList<ICapability> Values { get; }
    public Capability<T>? Get<T>(string id) => _byId.GetValueOrDefault(id) as Capability<T>;
}
