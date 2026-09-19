namespace Nexa.Services.Capabilities;

public enum PreflightSeverity { None, Information, Warning, Critical, Blocked }
public enum PreflightCertainty { Verified, Estimated, Inferred, Unknown }

/// <summary>Issues are policy outputs, never the same object as a machine observation.</summary>
public sealed class CapabilityPreflightIssue
{
    public CapabilityPreflightIssue(string code, PreflightSeverity severity, string category, PreflightCertainty certainty,
        bool hardConstraint, IEnumerable<string> evidence, IEnumerable<string>? causes = null, IEnumerable<string>? remediations = null,
        bool canBypass = false, bool suppressible = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        if (severity == PreflightSeverity.Blocked && (certainty != PreflightCertainty.Verified || !hardConstraint || canBypass || suppressible))
            throw new ArgumentException("Only verified hard constraints can block and cannot be bypassed or suppressed.", nameof(severity));
        Code = code; Severity = severity; Category = category; Certainty = certainty; HardConstraint = hardConstraint;
        Evidence = Array.AsReadOnly(evidence.ToArray()); Causes = Array.AsReadOnly((causes ?? []).ToArray());
        Remediations = Array.AsReadOnly((remediations ?? []).ToArray()); CanBypass = canBypass; Suppressible = suppressible;
        if (severity == PreflightSeverity.Blocked && Evidence.Count == 0) throw new ArgumentException("Blocked issues require evidence.", nameof(evidence));
    }
    public string Code { get; }
    public PreflightSeverity Severity { get; }
    public string Category { get; }
    public PreflightCertainty Certainty { get; }
    public bool HardConstraint { get; }
    public IReadOnlyList<string> Evidence { get; }
    public IReadOnlyList<string> Causes { get; }
    public IReadOnlyList<string> Remediations { get; }
    public bool CanBypass { get; }
    public bool Suppressible { get; }
    public static PreflightSeverity OverallSeverity(IEnumerable<CapabilityPreflightIssue> issues) => issues
        .Where(issue => issue.Severity != PreflightSeverity.Information).Select(issue => issue.Severity).DefaultIfEmpty(PreflightSeverity.None).Max();
}
