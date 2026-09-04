using PCL.Xsr;

namespace PCL.UI.Next;

/// <summary>Identifies a presentation change without backend-specific timing.</summary>
public sealed class XsrUiTransition
{
    public string? Key { get; set; }
    public XsrStateId BoundKey { get; set; }
    /// <summary>Signed horizontal entry distance in scene units. Both axes zero opt into a simple fade.</summary>
    public double OffsetX { get; set; }
    public double OffsetY { get; set; }
    public bool MovesSelf { get; set; }
    public bool StaggerEntry { get; set; }
    public XsrUiEntityId Source { get; set; }
    internal long LastSceneVersion { get; set; }
    internal double PresentedOffsetX { get; set; }
    internal double PresentedOffsetY { get; set; }
    internal double StartOffsetX { get; set; }
    internal double StartOffsetY { get; set; }
    internal IReadOnlyList<XsrUiSceneNode> Outgoing { get; set; } = [];
    internal string? PresentedKey { get; set; }
    internal bool HasPresentedKey { get; set; }

    /// <summary>Gives content leaves independent entry tracks; containers and explicitly bound motion stay untouched.</summary>
    public static void ConfigureIndependent(XsrUiTree tree, XsrUiEntityId root, string key, double distance = 6)
    {
        tree.Walk(root, entity =>
        {
            if (tree.Children(entity).Count != 0) return true;
            bool input = tree.GetComponent<XsrUiInput>(entity) is not null;
            bool image = tree.GetComponent<XsrUiImage>(entity) is not null;
            if (!input && !image && tree.GetComponent<XsrUiText>(entity) is null) return true;
            XsrUiTransition? transition = tree.GetComponent<XsrUiTransition>(entity);
            if (transition?.BoundKey.IsAssigned == true) return true;
            if (transition is null)
            {
                transition = new XsrUiTransition { MovesSelf = true, StaggerEntry = true };
                tree.SetComponent(entity, transition);
            }
            transition.Key = key;
            transition.OffsetY = distance * (input ? 1d / 3 : image ? 2d / 3 : 1);
            tree.MarkDirty(entity, XsrUiDirtyKinds.Paint);
            return true;
        });
    }
}
