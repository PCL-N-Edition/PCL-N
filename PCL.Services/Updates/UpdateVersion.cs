using System.Globalization;

namespace PCL.Services.Updates;

/// <summary>
/// The release stage of one launcher version, ordered from lowest to highest within the same
/// numeric version.
/// </summary>
public enum UpdateVersionStage
{
    /// <summary>A CI build: `2.0.0.ci.ffffff` or a legacy `-ci` suffix. Ordered by commit.</summary>
    Ci = 0,

    /// <summary>An alpha build: `2.0.0.alpha.N` or a legacy `-alpha` suffix.</summary>
    Alpha = 1,

    /// <summary>A beta build: `2.0.0.beta.N`, a legacy `-beta`/`-rc` suffix, or an unknown prerelease.</summary>
    Beta = 2,

    /// <summary>A stable build: a bare numeric version or a legacy `-release`-family suffix.</summary>
    Stable = 3,
}

/// <summary>
/// One parsed launcher version. Both the canonical XSR dotted forms (`2.0.0`,
/// `2.0.0.alpha.1`, `2.0.0.beta.1`, `2.0.0.ci.ffffff`) and the legacy display/tag forms
/// (`1.4.11`, `v1.1.8-release`, `1.4_2 beta`) normalize into this shape, so the one-way
/// upgrade gate compares them on one scale: numeric version first, then stage, then
/// sequence/commit.
/// </summary>
public readonly record struct UpdateVersion : IComparable<UpdateVersion>
{
    private readonly string? _commit;

    public UpdateVersion(long major, long minor, long patch, UpdateVersionStage stage, long sequence, string? commit = null)
    {
        if (!Enum.IsDefined(stage))
        {
            throw new ArgumentOutOfRangeException(nameof(stage));
        }

        if (stage == UpdateVersionStage.Ci && commit is null)
        {
            throw new ArgumentException("A CI version requires its commit identifier.", nameof(commit));
        }

        Major = major;
        Minor = minor;
        Patch = patch;
        Stage = stage;
        Sequence = sequence;
        _commit = NormalizeCommit(commit);
    }

    public long Major { get; }

    public long Minor { get; }

    public long Patch { get; }

    public UpdateVersionStage Stage { get; }

    /// <summary>The alpha/beta sequence number; zero for stable.</summary>
    public long Sequence { get; }

    /// <summary>The commit identifier of a CI build; null otherwise.</summary>
    public string? Commit => _commit;

    public static bool TryParse(string? value, out UpdateVersion parsed)
    {
        parsed = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string text = value.Trim();
        if (text.StartsWith('v') || text.StartsWith('V'))
        {
            text = text[1..];
        }

        int plus = text.IndexOf('+');
        if (plus >= 0)
        {
            text = text[..plus];
        }

        text = text.Replace('_', '-');
        int space = text.IndexOf(' ');
        if (space > 0)
        {
            text = text[..space];
        }

        int dash = text.IndexOf('-');
        string tail = dash > 0 ? text[..dash] : text;
        string dashPrerelease = dash > 0 && dash < text.Length - 1 ? text[(dash + 1)..].ToLowerInvariant() : string.Empty;

        // The numeric core and the prerelease can be separated by a dash (legacy `1.1.8-beta`)
        // or by dots (the canonical XSR form `2.0.0.alpha.1`). Take the leading numeric
        // segments as the core; everything after belongs to the prerelease. A four-segment
        // numeric legacy version drops its revision.
        string[] segments = tail.Split('.');
        int numericCount = segments.TakeWhile(static part => part.Length > 0 && part.All(char.IsAsciiDigit)).Count();
        if (numericCount == segments.Length && numericCount == 4)
        {
            numericCount = 3;
        }

        if (numericCount is < 1 or > 3)
        {
            return false;
        }

        string core = string.Join('.', segments.Take(numericCount));
        string prerelease = string.Join('.', segments.Skip(numericCount)).ToLowerInvariant();
        if (prerelease.Length == 0)
        {
            prerelease = dashPrerelease;
        }
        else if (dashPrerelease.Length > 0)
        {
            prerelease = $"{prerelease}-{dashPrerelease}";
        }

        if (!TryParseCore(core, out long major, out long minor, out long patch))
        {
            return false;
        }

        if (prerelease.Length == 0)
        {
            parsed = new UpdateVersion(major, minor, patch, UpdateVersionStage.Stable, 0);
            return true;
        }

        if (prerelease is "release" or "stable" or "final" or "ga")
        {
            parsed = new UpdateVersion(major, minor, patch, UpdateVersionStage.Stable, 0);
            return true;
        }

        if (prerelease == "alpha" || prerelease.StartsWith("alpha.", StringComparison.Ordinal))
        {
            long sequence = prerelease == "alpha" ? 1 : ParseSequence(prerelease["alpha.".Length..]);
            parsed = new UpdateVersion(major, minor, patch, UpdateVersionStage.Alpha, sequence);
            return true;
        }

        if (prerelease == "beta" || prerelease.StartsWith("beta.", StringComparison.Ordinal)
            || prerelease == "rc" || prerelease.StartsWith("rc", StringComparison.Ordinal))
        {
            parsed = new UpdateVersion(
                major, minor, patch, UpdateVersionStage.Beta, ParseSequence(TrimStage(prerelease)));
            return true;
        }

        if (prerelease.StartsWith("ci.", StringComparison.Ordinal))
        {
            string commit = prerelease["ci.".Length..];
            if (commit.Length is < 1 or > 40 || !commit.All(Uri.IsHexDigit))
            {
                return false;
            }

            parsed = new UpdateVersion(major, minor, patch, UpdateVersionStage.Ci, 0, commit);
            return true;
        }

        // An unrecognized prerelease ranks as beta so it can never beat a stable build.
        parsed = new UpdateVersion(major, minor, patch, UpdateVersionStage.Beta, 1);
        return true;
    }

    public int CompareTo(UpdateVersion other)
    {
        int core = (Major, Minor, Patch).CompareTo((other.Major, other.Minor, other.Patch));
        if (core != 0)
        {
            return core;
        }

        int stage = ((int)Stage).CompareTo((int)other.Stage);
        if (stage != 0)
        {
            return stage;
        }

        int sequence = Sequence.CompareTo(other.Sequence);
        if (sequence != 0)
        {
            return sequence;
        }

        return string.CompareOrdinal(Commit ?? string.Empty, other.Commit ?? string.Empty);
    }

    public static bool operator <(UpdateVersion left, UpdateVersion right) => left.CompareTo(right) < 0;

    public static bool operator <=(UpdateVersion left, UpdateVersion right) => left.CompareTo(right) <= 0;

    public static bool operator >(UpdateVersion left, UpdateVersion right) => left.CompareTo(right) > 0;

    public static bool operator >=(UpdateVersion left, UpdateVersion right) => left.CompareTo(right) >= 0;

    public override string ToString() => Stage switch
    {
        UpdateVersionStage.Stable => $"{Major}.{Minor}.{Patch}",
        UpdateVersionStage.Ci => $"{Major}.{Minor}.{Patch}.ci.{Commit}",
        _ => $"{Major}.{Minor}.{Patch}.{Stage.ToString().ToLowerInvariant()}.{Sequence}",
    };

    private static bool TryParseCore(string core, out long major, out long minor, out long patch)
    {
        major = minor = patch = 0;
        string[] parts = core.Split('.');
        if (parts.Length is < 1 or > 3)
        {
            return false;
        }

        foreach (string part in parts)
        {
            if (part.Length == 0 || !part.All(char.IsAsciiDigit) || !long.TryParse(part, out _))
            {
                return false;
            }
        }

        major = long.Parse(parts[0], CultureInfo.InvariantCulture);
        minor = parts.Length > 1 ? long.Parse(parts[1], CultureInfo.InvariantCulture) : 0;
        patch = parts.Length > 2 ? long.Parse(parts[2], CultureInfo.InvariantCulture) : 0;
        return major >= 0 && minor >= 0 && patch >= 0;
    }

    private static long ParseSequence(string tail)
    {
        string digits = new([.. tail.TakeWhile(char.IsAsciiDigit)]);
        return digits.Length > 0 && long.TryParse(digits, out long sequence) ? sequence : 1;
    }

    private static string TrimStage(string prerelease)
    {
        string tail = prerelease.StartsWith("beta", StringComparison.Ordinal)
            ? prerelease["beta".Length..]
            : prerelease["rc".Length..];
        return tail.TrimStart('.');
    }

    private static string? NormalizeCommit(string? commit) =>
        commit is null ? null : commit.ToLowerInvariant();
}
