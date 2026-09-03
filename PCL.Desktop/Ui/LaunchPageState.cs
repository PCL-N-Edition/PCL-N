using PCL.Xsr;
using PCL.Xsr.State;

namespace PCL.Desktop.Ui;

/// <summary>
/// Desktop-owned projection state for the launch page. These cells live in the shared host store
/// but describe product presentation, so Services neither declares nor names them.
/// </summary>
internal static class LaunchPageState
{
    public const string OwnerName = "PCL.Desktop.LaunchPage";

    public static readonly XsrSemanticId ProfileNameKey = XsrSemanticId.Parse("launch.profile.name");
    public static readonly XsrSemanticId ProfileSummaryKey = XsrSemanticId.Parse("launch.profile.summary");
    public static readonly XsrSemanticId InstanceSummaryKey = XsrSemanticId.Parse("launch.instance.summary");
    public static readonly XsrSemanticId InstanceDetailKey = XsrSemanticId.Parse("launch.instance.detail");
    public static readonly XsrSemanticId SelectedInstanceKey = XsrSemanticId.Parse("launch.selected.instance");
    public static readonly XsrSemanticId ActionLabelKey = XsrSemanticId.Parse("launch.action.label");
    public static readonly XsrSemanticId StatusKey = XsrSemanticId.Parse("launch.status");

    public static void DeclareState(XsrStateStoreBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Cell<string>(ProfileNameKey, OwnerName);
        builder.Cell<string>(ProfileSummaryKey, OwnerName);
        builder.Cell<string>(InstanceSummaryKey, OwnerName);
        builder.Cell<string>(InstanceDetailKey, OwnerName);
        builder.Cell<string>(SelectedInstanceKey, OwnerName);
        builder.Cell<string>(ActionLabelKey, OwnerName);
        builder.Cell<string>(StatusKey, OwnerName);
    }
}
