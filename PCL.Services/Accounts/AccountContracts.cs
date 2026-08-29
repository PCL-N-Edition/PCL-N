using System.Text.Json.Serialization;

namespace PCL.Services.Accounts;

/// <summary>
/// The kind of account backing one launch profile, serialized as its string name to stay
/// compatible with legacy profile files.
/// </summary>
public enum LaunchProfileKind
{
    Microsoft,
    ThirdParty,
    Offline,
    LittleSkin,
    NCloud,
}

/// <summary>
/// One launch profile: the full persisted account fact, including credentials. This record
/// never enters published state; renderers observe <see cref="LaunchProfileView"/> instead.
/// Field set and defaults are the legacy profile contract.
/// </summary>
public sealed record LaunchProfile
{
    public required string Username { get; init; }

    public string Info { get; init; } = string.Empty;

    [JsonConverter(typeof(JsonStringEnumConverter<LaunchProfileKind>))]
    public LaunchProfileKind Kind { get; init; }

    public string Uuid { get; init; } = string.Empty;

    public string Logo { get; init; } = string.Empty;

    public string SvgIcon { get; init; } = "lucide/user";

    public string? SkinAddress { get; init; }

    public string AuthServer { get; init; } = string.Empty;

    public string AccessToken { get; init; } = string.Empty;

    public string RefreshToken { get; init; } = string.Empty;

    /// <summary>Provider OAuth token used for account/appearance APIs.</summary>
    public string ProviderAccessToken { get; init; } = string.Empty;

    /// <summary>Provider OAuth access-token expiration as a Unix timestamp.</summary>
    public long ProviderTokenExpiresAtUnix { get; init; }

    /// <summary>Yggdrasil clientToken used for validate/refresh (third-party).</summary>
    public string ClientToken { get; init; } = string.Empty;
}

/// <summary>
/// The persisted profile file body. Schema version 1 is the legacy contract; newer schemas
/// are rejected and quarantined, older ones cannot exist.
/// </summary>
public sealed record LaunchProfileSet
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public IReadOnlyList<LaunchProfile> Profiles { get; init; } = [];
}

/// <summary>
/// The published, credential-free view of one profile. Everything that identifies or
/// describes the account for rendering is here; tokens never are.
/// </summary>
public readonly record struct LaunchProfileView(
    int Index,
    string Username,
    string Info,
    LaunchProfileKind Kind,
    string Uuid,
    string Logo,
    string SvgIcon,
    string? SkinAddress,
    string AuthServer);

/// <summary>
/// Identifies an account provider by its stable, case-insensitive name. Equality, the
/// equality operators, and hashing are all case-insensitive — the generated record members
/// are overridden precisely so `Parse("Microsoft") == Parse("microsoft")` holds.
/// </summary>
public readonly struct AccountProviderId : IEquatable<AccountProviderId>
{
    public AccountProviderId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("The account provider ID cannot be empty.", nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;

    public bool Equals(string? value) =>
        string.Equals(Value, value, StringComparison.OrdinalIgnoreCase);

    public bool Equals(AccountProviderId other) =>
        string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase);

    public override bool Equals(object? obj) =>
        obj is AccountProviderId other && Equals(other);

    public override int GetHashCode() =>
        StringComparer.OrdinalIgnoreCase.GetHashCode(Value);

    public static bool operator ==(AccountProviderId left, AccountProviderId right) => left.Equals(right);

    public static bool operator !=(AccountProviderId left, AccountProviderId right) => !left.Equals(right);

    public static AccountProviderId Parse(string value) => new(value);
}
