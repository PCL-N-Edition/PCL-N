namespace PCL.Services.Minecraft;

/// <summary>
/// Minecraft's 1.x version coordinate, kept distinct from Java's <see cref="Version"/>.
/// Shorthand values such as <c>20.5</c> are normalized to <c>1.20.5</c> once at the boundary.
/// </summary>
public readonly record struct MinecraftGameVersion : IComparable<MinecraftGameVersion>
{
    public MinecraftGameVersion(int major, int minor, int patch)
    {
        if (major < 0 || minor < 0 || patch < 0)
            throw new ArgumentOutOfRangeException(nameof(major), "Minecraft version components cannot be negative.");
        Major = major;
        Minor = minor;
        Patch = patch;
    }

    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }

    public static MinecraftGameVersion FromVersion(Version value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Major == 1
            ? new MinecraftGameVersion(value.Major, value.Minor, Math.Max(0, value.Build))
            : new MinecraftGameVersion(1, value.Major, Math.Max(0, value.Minor));
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
        Major != other.Major ? Major.CompareTo(other.Major) :
        Minor != other.Minor ? Minor.CompareTo(other.Minor) :
        Patch.CompareTo(other.Patch);

    public static bool operator <(MinecraftGameVersion left, MinecraftGameVersion right) => left.CompareTo(right) < 0;
    public static bool operator >(MinecraftGameVersion left, MinecraftGameVersion right) => left.CompareTo(right) > 0;
    public static bool operator <=(MinecraftGameVersion left, MinecraftGameVersion right) => left.CompareTo(right) <= 0;
    public static bool operator >=(MinecraftGameVersion left, MinecraftGameVersion right) => left.CompareTo(right) >= 0;
    public override string ToString() => $"{Major}.{Minor}.{Patch}";
}
