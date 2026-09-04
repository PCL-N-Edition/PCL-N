using PCL.Xsr;
using PCL.Xsr.State;

namespace PCL.UI.Next;

/// <summary>
/// Targets one renderer property slot of an entity for a state binding.
/// </summary>
public enum XsrUiStateProperty
{
    Generic = 0,
    Text = 1,
    Visibility = 2,
    Enabled = 3,
    SemanticLabel = 4,
    TransitionKey = 5,
}

/// <summary>
/// One state binding record: which state entry feeds which property slot of an entity, and what
/// dirty kinds the entity receives when the entry changes. This is the runtime form of the
/// PXML binding table; an entity carries as many bindings as it needs.
/// </summary>
public readonly record struct XsrUiStateDependency(
    XsrStateId State,
    XsrUiStateProperty Property,
    XsrUiDirtyKinds DirtyKinds)
{
    public bool IsValid => State.IsAssigned;
}

/// <summary>
/// Convenience component binding one entity to one state entry with one property slot. For
/// several bindings on one entity, bind through <see cref="XsrUiTree.BindState"/> directly.
/// </summary>
public sealed class XsrUiStateBinding
{
    public XsrUiStateBinding(XsrStateId state, XsrUiStateProperty property = XsrUiStateProperty.Generic)
    {
        State = state;
        Property = property;
    }

    public XsrStateId State { get; }

    public XsrUiStateProperty Property { get; }

    public XsrUiStateDependency Dependency => new(State, Property, XsrUiDirtyKinds.State);
}
