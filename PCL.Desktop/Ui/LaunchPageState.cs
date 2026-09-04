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
    public static readonly XsrSemanticId ProfileKindKey = XsrSemanticId.Parse("launch.profile.kind");
    public static readonly XsrSemanticId AccountPickerKey = XsrSemanticId.Parse("launch.account.picker");
    public static readonly XsrSemanticId AccountRosterVisibleKey = XsrSemanticId.Parse("launch.account.roster-visible");
    public static readonly XsrSemanticId AccountSelectedVisibleKey = XsrSemanticId.Parse("launch.account.selected-visible");
    public static readonly XsrSemanticId AccountCanReturnKey = XsrSemanticId.Parse("launch.account.can-return");
    public static readonly XsrSemanticId AccountHintKey = XsrSemanticId.Parse("launch.account.hint");
    public static readonly XsrSemanticId InstanceSummaryKey = XsrSemanticId.Parse("launch.instance.summary");
    public static readonly XsrSemanticId InstanceAvailableKey = XsrSemanticId.Parse("launch.instance.available");
    public static readonly XsrSemanticId SelectedInstanceKey = XsrSemanticId.Parse("launch.selected.instance");
    public static readonly XsrSemanticId ActionLabelKey = XsrSemanticId.Parse("launch.action.label");
    public static readonly XsrSemanticId ActionEnabledKey = XsrSemanticId.Parse("launch.action.enabled");
    public static readonly XsrSemanticId StatusKey = XsrSemanticId.Parse("launch.status");
    public static readonly XsrSemanticId StatusVisibleKey = XsrSemanticId.Parse("launch.status.visible");
    public static readonly XsrSemanticId WidgetAboutLabelKey = XsrSemanticId.Parse("launch.widget.about-label");
    public static readonly XsrSemanticId WidgetTriviaLabelKey = XsrSemanticId.Parse("launch.widget.trivia-label");
    public static readonly XsrSemanticId WidgetHintKey = XsrSemanticId.Parse("launch.widget.hint");

    public static void DeclareState(XsrStateStoreBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Cell<string>(ProfileNameKey, OwnerName);
        builder.Cell<string>(ProfileKindKey, OwnerName);
        builder.Cell<bool>(AccountPickerKey, OwnerName);
        builder.Cell<bool>(AccountRosterVisibleKey, OwnerName);
        builder.Cell<bool>(AccountSelectedVisibleKey, OwnerName);
        builder.Cell<bool>(AccountCanReturnKey, OwnerName);
        builder.Cell<string>(AccountHintKey, OwnerName);
        builder.Cell<string>(InstanceSummaryKey, OwnerName);
        builder.Cell<bool>(InstanceAvailableKey, OwnerName);
        builder.Cell<string>(SelectedInstanceKey, OwnerName);
        builder.Cell<string>(ActionLabelKey, OwnerName);
        builder.Cell<bool>(ActionEnabledKey, OwnerName);
        builder.Cell<string>(StatusKey, OwnerName);
        builder.Cell<bool>(StatusVisibleKey, OwnerName);
        builder.Cell<string>(WidgetAboutLabelKey, OwnerName);
        builder.Cell<string>(WidgetTriviaLabelKey, OwnerName);
        builder.Cell<string>(WidgetHintKey, OwnerName);
    }
}
