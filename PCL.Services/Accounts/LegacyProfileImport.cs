using System.Text.Json;

namespace PCL.Services.Accounts;

/// <summary>Read-only compatibility discovery/import. Never uses the quarantining live-store port.</summary>
public sealed class LegacyProfileImport
{
    private readonly Func<IEnumerable<string>> _locations;
    public LegacyProfileImport(Func<IEnumerable<string>>? locations = null) => _locations = locations ?? DefaultLocations;

    public IReadOnlyList<AccountImportCandidate> Discover()
    {
        List<AccountImportCandidate> result = [];
        HashSet<string> seen = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (string candidate in _locations())
        {
            try
            {
                string path = Path.GetFullPath(candidate);
                if (File.Exists(path) && seen.Add(path)) result.Add(new($"legacy-{result.Count}", path));
            }
            catch (Exception failure) when (failure is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // One inaccessible documented location does not hide the other candidates.
            }
        }
        return result;
    }

    public static async Task<IReadOnlyList<LaunchProfile>> ReadAsync(string path, CancellationToken cancellationToken)
    {
        const int maximumBytes = 4 * 1024 * 1024;
        using FileStream stream = new(Path.GetFullPath(path), FileMode.Open, FileAccess.Read, FileShare.Read,
            16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > maximumBytes) throw new InvalidDataException("Profile import is too large.");
        byte[] bytes = new byte[checked((int)stream.Length)];
        try
        {
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            LaunchProfileSet? profiles = JsonSerializer.Deserialize(bytes, LaunchProfileJsonContext.Default.LaunchProfileSet);
            if (profiles is null || profiles.SchemaVersion != LaunchProfileSet.CurrentSchemaVersion || profiles.Profiles is null)
                throw new InvalidDataException("Unsupported profile schema.");
            return profiles.Profiles;
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
    }

    private static IEnumerable<string> DefaultLocations()
    {
        string? explicitPath = Environment.GetEnvironmentVariable("PCLN_LAUNCH_PROFILES_PATH");
        if (!string.IsNullOrWhiteSpace(explicitPath)) yield return explicitPath;
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string data = OperatingSystem.IsWindows() ? roaming : OperatingSystem.IsMacOS()
            ? Path.Combine(user, "Library", "Application Support")
            : Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } xdg ? xdg : Path.Combine(user, ".local", "share");
        yield return Path.Combine(data, "PCL-N", "launch-profiles.json");
        foreach (string root in new[] { local, roaming }.Where(root => root.Length > 0).Distinct())
        {
            string? overridden = ReadDataOverride(Path.Combine(root, "PCL-N", "pcln-paths.json"));
            if (!string.IsNullOrWhiteSpace(overridden)) yield return Path.Combine(overridden, "launch-profiles.json");
        }
    }

    private static string? ReadDataOverride(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 64 * 1024) return null;
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
                if (property.Name.Equals("applicationDataDirectory", StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.String)
                {
                    string? directory = property.Value.GetString();
                    return directory is { Length: > 0 } && Path.IsPathFullyQualified(directory) ? directory : null;
                }
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            // Discovery never repairs or rewrites a legacy path override.
        }
        return null;
    }
}
