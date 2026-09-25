using Nexa.Services.Updates;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    internal static async ValueTask UpdateDiscoveryBindsEveryVersion()
    {
        foreach (var identity in new[] { ScatterIdentity(), ScatterIdentity("1.4.2"),
                     ScatterIdentity() with { DistributionLayout = UpdateDistributionLayout.SingleFile } })
        {
            var handler = new StubHandler();
            var result = await CreateService(handler).ResolveAsync("v1.4.10", "2.0.0.alpha.4", UpdateChannel.Release, identity);
            AssertFalse(result.IsAllowed);
            AssertEqual(0, handler.Requests.Count);
        }
        foreach (string bogus in new[] { TargetIndexJson.Replace("1.4.12", "1.4.9", StringComparison.Ordinal),
                     TargetIndexJson.Replace("\"targetTag\": \"v1.4.12\"", "\"targetTag\": \"v1.4.9\"", StringComparison.Ordinal) })
        {
            var handler = new StubHandler();
            handler.Serve("https://dist.example/v1/updates/releases/v1.4.12/patch-index.json", bogus);
            var result = await CreateService(handler).ResolveAsync("v1.4.12", "1.4.12", UpdateChannel.Release, ScatterIdentity());
            AssertTrue(result.IsAllowed);
            AssertEqual("1.4.12", result.Package!.TargetVersion);
            AssertFalse(result.Package.UsesPatch);
        }
        var planner = new UpdatePackagePlanner(PlannerOptions());
        var mismatched = SampleIndex();
        mismatched.Index.TargetVersion = "1.4.9";
        AssertNull(planner.PlanFromIndex("v1.4.12", ScatterIdentity(), "1.4.11", [mismatched]));
        AssertNull(planner.PlanFromIndex("v1.4.13", ScatterIdentity(), "1.4.11", [SampleIndex()]));
        var displayAlias = SampleIndex();
        displayAlias.Index.TargetVersion = "2.0.0 beta";
        displayAlias.Index.TargetTag = "v2.0.0";
        var displaySource = new UpdatePatchIndexSource("v2.0.0", displayAlias.Index);
        AssertNull(planner.PlanFromIndex("v2.0.0", ScatterIdentity("2.0.0.beta.2"), "2.0.0.beta.2", [displaySource]));
        var equivalent = await CreateService(new StubHandler()).ResolveAsync("v1.4.12-release", "1.4.12", UpdateChannel.Release, ScatterIdentity());
        AssertTrue(equivalent.IsAllowed);
        var ci = await CreateService(new StubHandler()).ResolveAsync("2.0.0.ci.abcdef", "2.0.0.ci.abcdef", UpdateChannel.CI, ScatterIdentity("2.0.0.ci.123456"));
        AssertTrue(ci.IsAllowed);
        var sameCi = await CreateService(new StubHandler()).ResolveAsync("2.0.0.ci.abcdef", "2.0.0.ci.abcdef", UpdateChannel.CI, ScatterIdentity("2.0.0.ci.abcdef"));
        AssertEqual(UpdateEligibilityDecision.SameVersion, sameCi.Decision);
    }
}
