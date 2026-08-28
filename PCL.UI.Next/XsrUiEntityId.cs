namespace PCL.UI.Next;

/// <summary>
/// Identifies one UI entity inside its tree. Handles are generational: the index is recycled on
/// destroy while the generation advances, so a stale handle can never silently resolve to a
/// different entity. Handles are renderer-local and never cross the plugin compatibility boundary.
/// </summary>
public readonly record struct XsrUiEntityId(int Index, uint Generation)
{
    /// <summary>
    /// Gets a value indicating whether this handle refers to a live entity.
    /// </summary>
    public bool IsAssigned => Index > 0 && Generation > 0;

    /// <inheritdoc />
    public override string ToString() => IsAssigned ? $"entity:{Index}@{Generation}" : "entity:none";
}

/// <summary>
/// Classifies what changed on an entity so renderer passes touch only what they need.
/// </summary>
[Flags]
public enum XsrUiDirtyKinds
{
    None = 0,

    /// <summary>
    /// The entity hierarchy or component set changed; structure and layout must rebuild.
    /// </summary>
    Structure = 1,

    /// <summary>
    /// Measure or arrange results are stale.
    /// </summary>
    Layout = 2,

    /// <summary>
    /// Paintable content changed without affecting measure or arrange results.
    /// </summary>
    Paint = 4,

    /// <summary>
    /// A bound state entry changed; layout and paint are stale.
    /// </summary>
    State = 8,
}
