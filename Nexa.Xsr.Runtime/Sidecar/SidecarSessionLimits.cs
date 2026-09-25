namespace Nexa.Xsr.Runtime;

/// <summary>Host-owned budgets, never negotiated upward by a Sidecar.</summary>
public sealed record SidecarSessionLimits
{
    public int MaximumItems { get; init; } = 4096;
    public int MaximumSemanticIdCharacters { get; init; } = 256;
    public long MaximumRegistrationBytes { get; init; } = 32 * 1024 * 1024;
    public long MaximumSnapshotBytes { get; init; } = 32 * 1024 * 1024;
    public TimeSpan RegistrationTimeout { get; init; } = TimeSpan.FromSeconds(30);
    internal void Validate()
    {
        if (MaximumItems is <= 0 or > 4096 || MaximumSemanticIdCharacters is <= 0 or > 256
            || MaximumRegistrationBytes is <= 0 or > 32 * 1024 * 1024
            || MaximumSnapshotBytes is <= 0 or > 32 * 1024 * 1024 || RegistrationTimeout <= TimeSpan.Zero || RegistrationTimeout > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(SidecarSessionLimits));
    }
}
