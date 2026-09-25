using Nexa.Services.Telemetry;
using Nexa.Services.Updates;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static void BuildIdentityPreservesPolicyChannels()
    {
        foreach (var (version, channel, update, required) in new[]
        {
            ("2.0.0.alpha.5", "alpha", "alpha", true), ("2.0.0.beta.2", "beta", "beta", true),
            ("2.0.0.ci.abcdef", "ci", "alpha", true), ("2.0.0", "stable", "stable", false),
        })
        {
            var identity = LauncherBuildIdentity.Parse(version + "+build-metadata");
            AssertEqual(version, identity.ProductVersion);
            AssertEqual("2.0.0", identity.CoreVersion);
            AssertEqual(channel, identity.Channel); AssertEqual(update, identity.UpdateChannel);
            AssertEqual(required, identity.DiagnosticsRequired);
            AssertEqual(required, LauncherTelemetryPolicy.IsRequired(identity.InformationalVersion));
        }
        var current = LauncherBuildIdentity.Parse("2.0.0.alpha.4");
        AssertTrue(UpdateVersion.TryParse(current.ProductVersion, out var parsed));
        AssertTrue(UpdateVersion.TryParse("2.0.0.alpha.5", out var next));
        AssertTrue(next.CompareTo(parsed) > 0);
    }
}
