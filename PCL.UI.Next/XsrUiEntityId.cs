namespace PCL.UI.Next;

/// <summary>
/// Identifies one UI entity inside its tree. Entity IDs are handles, not stable contracts: they
/// are recycled when entities are destroyed and never cross the plugin compatibility boundary.
/// </summary>
public readonly record struct XsrUiEntityId(int Value)
{
    /// <summary>
    /// Gets a value indicating whether this handle refers to a live entity.
    /// </summary>
    public bool IsAssigned => Value > 0;

    /// <inheritdoc />
    public override string ToString() => IsAssigned ? $"entity:{Value}" : "entity:none";
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
