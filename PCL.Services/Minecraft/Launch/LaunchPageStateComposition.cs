using PCL.Xsr;
using PCL.Xsr.State;

namespace PCL.Services.Minecraft.Launch;

/// <summary>
/// State of the product launch page. These cells exist so the launch page binds its summaries
/// and status through the one host state store like every other capability; the page controller
/// publishes them, and the renderer only reads.
/// </summary>
public static class LaunchPageStateComposition
{
    public static readonly XsrSemanticId ProfileSummaryKey =
        XsrSemanticId.Parse("launch.profile.summary");

    public static readonly XsrSemanticId InstanceSummaryKey =
        XsrSemanticId.Parse("launch.instance.summary");

    public static readonly XsrSemanticId StatusKey =
        XsrSemanticId.Parse("launch.status");

    public const string Owner = "PCL.Desktop.LaunchPage";

    public static void DeclareState(XsrStateStoreBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Cell<string>(ProfileSummaryKey, Owner);
        builder.Cell<string>(InstanceSummaryKey, Owner);
        builder.Cell<string>(StatusKey, Owner);
    }
}
