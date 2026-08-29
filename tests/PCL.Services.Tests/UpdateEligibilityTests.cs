using PCL.Services.Updates;

namespace PCL.Services.Tests;

// One-way upgrade policy: the legacy 1.4.x line may cross into any 2.0.0 build, and no
// launcher is ever offered a downgrade. These tests lock the headline cross-major rule and
// the ordering of every stage pair the updater can meet.
internal static partial class Program
{
    private static UpdateEligibilityResult Evaluate(string current, string candidate) =>
        UpdateEligibility.Evaluate(current, candidate);

    private static void AssertAllowed(string current, string candidate) =>
        AssertEqual(UpdateEligibilityDecision.Allowed, Evaluate(current, candidate).Decision);

    private static void AssertDowngrade(string current, string candidate) =>
        AssertEqual(UpdateEligibilityDecision.Downgrade, Evaluate(current, candidate).Decision);

    internal static void LegacyOneFourCrossesIntoTwoPointZero()
    {
        AssertAllowed("1.4.11", "2.0.0.alpha.1");
        AssertAllowed("1.4.11", "2.0.0.alpha.7");
        AssertAllowed("1.4.11", "2.0.0.beta.1");
        AssertAllowed("1.4.11", "2.0.0");
        AssertAllowed("v1.4.11-release", "2.0.0.alpha.1");
        AssertAllowed("1.4.11 release", "2.0.0.alpha.1");
        AssertAllowed("1.4", "2.0.0.alpha.1");
        AssertAllowed("0.9.9", "2.0.0.alpha.1");

        // And the reverse direction is always a downgrade, never offered.
        AssertDowngrade("2.0.0.alpha.1", "1.4.11");
        AssertDowngrade("2.0.0", "1.4.11");
        AssertDowngrade("2.0.0.beta.1", "v1.4.11-release");
    }

    internal static void AlphaBetaStableOrderingIsMonotonic()
    {
        AssertAllowed("2.0.0.alpha.1", "2.0.0.alpha.2");
        AssertDowngrade("2.0.0.alpha.2", "2.0.0.alpha.1");
        AssertAllowed("2.0.0.alpha.9", "2.0.0.beta.1");
        AssertDowngrade("2.0.0.beta.1", "2.0.0.alpha.9");
        AssertAllowed("2.0.0.beta.9", "2.0.0");
        AssertDowngrade("2.0.0", "2.0.0.beta.9");
        AssertDowngrade("2.0.0", "2.0.0.alpha.99");

        AssertAllowed("1.9.9", "2.0.0");
        AssertDowngrade("2.0.0", "1.9.9");
        AssertAllowed("2.0.0", "2.0.1.alpha.1");
        AssertDowngrade("2.1.0", "2.0.0.beta.2");
    }

    internal static void SameVersionIsANoOp()
    {
        AssertEqual(UpdateEligibilityDecision.SameVersion, Evaluate("2.0.0.alpha.1", "2.0.0.alpha.1").Decision);
        AssertEqual(UpdateEligibilityDecision.SameVersion, Evaluate("2.0.0", "2.0.0").Decision);
        AssertEqual(UpdateEligibilityDecision.SameVersion, Evaluate("v1.4.11-release", "1.4.11").Decision);
    }

    internal static void CiBuildsHopByCommit()
    {
        AssertAllowed("2.0.0.ci.aaaaaaa", "2.0.0.ci.ffffff");
        AssertAllowed("2.0.0.ci.ffffff", "2.0.0.ci.aaaaaaa");
        AssertEqual(UpdateEligibilityDecision.SameVersion, Evaluate("2.0.0.ci.ffffff", "2.0.0.ci.ffffff").Decision);
        // A CI build ranks below every prerelease channel of the same version.
        AssertDowngrade("2.0.0.alpha.3", "2.0.0.ci.ffffff");
        AssertAllowed("2.0.0.ci.ffffff", "2.0.0.alpha.3");
        AssertDowngrade("2.0.1.ci.ffffff", "2.0.0.ci.ffffff");
    }

    internal static void UnrecognizedVersionsAreRefused()
    {
        AssertEqual(UpdateEligibilityDecision.Unrecognized, UpdateEligibility.Evaluate(null, "2.0.0.alpha.1").Decision);
        AssertEqual(UpdateEligibilityDecision.Unrecognized, UpdateEligibility.Evaluate("2.0.0.alpha.1", "nonsense").Decision);
        AssertEqual(UpdateEligibilityDecision.Unrecognized, UpdateEligibility.Evaluate("2.0.0.ci.ZZZZZZ", "2.0.0.alpha.1").Decision);
    }

    internal static void VersionParsingNormalizesLegacyShapes()
    {
        AssertTrue(UpdateVersion.TryParse("1.4.11", out UpdateVersion plain));
        AssertEqual(1L, plain.Major);
        AssertEqual(4L, plain.Minor);
        AssertEqual(11L, plain.Patch);
        AssertEqual(UpdateVersionStage.Stable, plain.Stage);

        AssertTrue(UpdateVersion.TryParse("V2.0.0+BUNDLE", out UpdateVersion tagged));
        AssertEqual(UpdateVersionStage.Stable, tagged.Stage);
        AssertEqual(2L, tagged.Major);

        AssertTrue(UpdateVersion.TryParse("2.0.0.alpha.7", out UpdateVersion alpha));
        AssertEqual(UpdateVersionStage.Alpha, alpha.Stage);
        AssertEqual(7L, alpha.Sequence);
        AssertEqual("2.0.0.alpha.7", alpha.ToString());

        AssertTrue(UpdateVersion.TryParse("2.0.0.ci.ffffff", out UpdateVersion ci));
        AssertEqual(UpdateVersionStage.Ci, ci.Stage);
        AssertEqual("ffffff", ci.Commit);

        AssertFalse(UpdateVersion.TryParse("2.0.0.ci.ZZZZZZ", out _));
        AssertFalse(UpdateVersion.TryParse("one.two.three", out _));
        AssertFalse(UpdateVersion.TryParse("1.2.3.4.5", out _));
    }
}
