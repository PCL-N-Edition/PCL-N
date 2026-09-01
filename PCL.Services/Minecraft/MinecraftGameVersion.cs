namespace PCL.Services.Minecraft;

/// <summary>Numbering schemes used by Minecraft version identifiers.</summary>
public enum MinecraftVersionScheme
{
    /// <summary>The historical <c>1.x.y</c> release line.</summary>
    Legacy,

    /// <summary>The calendar-based release line introduced by Minecraft 26.1.</summary>
    Calendar,
}

/// <summary>
/// Minecraft's version coordinate, kept distinct from Java's <see cref="Version"/>.
/// Historical shorthand values such as <c>20.5</c> are normalized to <c>1.20.5</c> once at
/// the boundary, while calendar versions such as <c>26.1</c> retain their original coordinate.
/// </summary>
public readonly record struct MinecraftGameVersion : IComparable<MinecraftGameVersion>
{
    public MinecraftGameVersion(int major, int minor, int patch)
        : this(InferScheme(major, minor), major, minor, patch)
    {
    }

    public MinecraftGameVersion(MinecraftVersionScheme scheme, int major, int minor, int patch)
    {
        if (major < 0 || minor < 0 || patch < 0)
            throw new ArgumentOutOfRangeException(nameof(major), "Minecraft version components cannot be negative.");
        if (!Enum.IsDefined(scheme))
            throw new ArgumentOutOfRangeException(nameof(scheme));
        Scheme = scheme;
        Major = major;
        Minor = minor;
        Patch = patch;
    }

    public MinecraftGameVersion(int major, int minor, int patch, MinecraftVersionScheme scheme)
        : this(scheme, major, minor, patch)
    {
    }

    public MinecraftVersionScheme Scheme { get; }
    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }

    public bool IsLegacy => Scheme == MinecraftVersionScheme.Legacy;
    public bool IsCalendar => Scheme == MinecraftVersionScheme.Calendar;

    public static MinecraftGameVersion FromVersion(Version value)
    {
        ArgumentNullException.ThrowIfNull(value);
        int patch = Math.Max(0, value.Build);
        if (value.Major == 1)
            return new MinecraftGameVersion(MinecraftVersionScheme.Legacy, value.Major, value.Minor, patch);

        // 26.1 is the first calendar release. Values below that boundary retain the
        // long-standing shorthand interpretation (20.5 -> 1.20.5).
        if (IsCalendarCoordinate(value.Major, value.Minor))
            return new MinecraftGameVersion(MinecraftVersionScheme.Calendar, value.Major, value.Minor, patch);

        return new MinecraftGameVersion(MinecraftVersionScheme.Legacy, 1, value.Major, Math.Max(0, value.Minor));
    }

    public static bool TryParse(string? value, out MinecraftGameVersion result)
    {
        result = default;
        if (!Version.TryParse(value, out Version? parsed)) return false;
        result = FromVersion(parsed!);
        return true;
    }

    public Version ToVersion() => new(Major, Minor, Patch);

    public int CompareTo(MinecraftGameVersion other) =>
        Scheme != other.Scheme ? Scheme.CompareTo(other.Scheme) :
        Major != other.Major ? Major.CompareTo(other.Major) :
        Minor != other.Minor ? Minor.CompareTo(other.Minor) :
        Patch.CompareTo(other.Patch);

    public static bool operator <(MinecraftGameVersion left, MinecraftGameVersion right) => left.CompareTo(right) < 0;
    public static bool operator >(MinecraftGameVersion left, MinecraftGameVersion right) => left.CompareTo(right) > 0;
    public static bool operator <=(MinecraftGameVersion left, MinecraftGameVersion right) => left.CompareTo(right) <= 0;
    public static bool operator >=(MinecraftGameVersion left, MinecraftGameVersion right) => left.CompareTo(right) >= 0;
    public override string ToString() => $"{Major}.{Minor}.{Patch}";

    private const int CalendarReleaseYear = 26;

    private static MinecraftVersionScheme InferScheme(int major, int minor) =>
        IsCalendarCoordinate(major, minor) ? MinecraftVersionScheme.Calendar : MinecraftVersionScheme.Legacy;

    private static bool IsCalendarCoordinate(int major, int minor) =>
        major > CalendarReleaseYear || major == CalendarReleaseYear && minor >= 1;
}
