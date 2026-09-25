using System.Globalization;

namespace Nexa.Services.Updates;

/// <summary>One immutable version identity for update, rollout and diagnostic policy.</summary>
public sealed record LauncherBuildIdentity(string InformationalVersion, string ProductVersion, string CoreVersion,
    string Channel, string UpdateChannel, long Sequence, string? Commit, bool DiagnosticsRequired)
{
    public static LauncherBuildIdentity Parse(string informational)
    {
        if (!UpdateVersion.TryParse(informational, out var version))
            return new(informational, informational, informational, "unknown", "stable", 0, null, false);
        string channel = version.Stage switch
        {
            UpdateVersionStage.Ci => "ci",
            UpdateVersionStage.Alpha => "alpha",
            UpdateVersionStage.Beta => "beta",
            _ => "stable",
        };
        return new(informational, version.ToString(), string.Create(CultureInfo.InvariantCulture, $"{version.Major}.{version.Minor}.{version.Patch}"),
            channel, channel == "ci" ? "alpha" : channel, version.Sequence, version.Commit,
            version.Major == 2 && version.Stage != UpdateVersionStage.Stable);
    }
}
