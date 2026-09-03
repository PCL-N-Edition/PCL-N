namespace PCL.UI.Next;

/// <summary>
/// Owns the render-thread tree and the state observer that is attached while the host state
/// store is built. Creating this context before service composition guarantees that one
/// <see cref="XsrUiStateBridge"/> observes the same store later rendered by the PXML shell.
/// </summary>
public sealed class XsrUiRuntimeContext
{
    public XsrUiRuntimeContext()
    {
        Tree = new XsrUiTree();
        StateBridge = new XsrUiStateBridge(Tree);
    }

    /// <summary>The single render-thread entity tree for this UI surface.</summary>
    public XsrUiTree Tree { get; }

    /// <summary>The host-store observer and frame-boundary dirty bridge for <see cref="Tree"/>.</summary>
    public XsrUiStateBridge StateBridge { get; }
}
