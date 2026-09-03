using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;

namespace PCL.UI.Next.Backend.Avalonia;

/// <summary>
/// Base automation peer for one immutable UI.Next scene node. The peer exposes scene metadata
/// through Avalonia while focus and activation remain renderer operations.
/// </summary>
internal class AvaloniaUiSceneNodeAutomationPeer : ControlAutomationPeer
{
    internal AvaloniaUiSceneNodeAutomationPeer(AvaloniaUiSceneNodeControl owner)
        : base(owner)
    {
    }

    protected AvaloniaUiSceneNodeControl SceneOwner => (AvaloniaUiSceneNodeControl)Owner;

    protected override string GetClassNameCore() => nameof(AvaloniaUiSceneNodeControl);

    protected override bool IsKeyboardFocusableCore() => SceneOwner.Node.IsFocusable;

    protected override void SetFocusCore() => SceneOwner.FocusFromAutomation();
}

/// <summary>Automation peer for a clickable scene node.</summary>
internal sealed class AvaloniaUiSceneInvokeAutomationPeer : AvaloniaUiSceneNodeAutomationPeer, IInvokeProvider
{
    public AvaloniaUiSceneInvokeAutomationPeer(AvaloniaUiSceneNodeControl owner)
        : base(owner)
    {
    }

    public void Invoke() => SceneOwner.InvokeFromAutomation();
}

/// <summary>Automation peer for a scene navigation container.</summary>
internal sealed class AvaloniaUiSceneNavigationAutomationPeer : AvaloniaUiSceneNodeAutomationPeer, ISelectionProvider
{
    public AvaloniaUiSceneNavigationAutomationPeer(AvaloniaUiSceneNodeControl owner)
        : base(owner)
    {
    }

    public bool CanSelectMultiple => false;

    public bool IsSelectionRequired => true;

    public IReadOnlyList<AutomationPeer> GetSelection() =>
        [.. SceneOwner.SelectionItems
            .Where(item => item.Node.IsSelected)
            .Select(ControlAutomationPeer.CreatePeerForElement)];
}

/// <summary>Automation peer for one selected-or-selectable scene navigation item.</summary>
internal sealed class AvaloniaUiSceneNavigationItemAutomationPeer
    : AvaloniaUiSceneNodeAutomationPeer, IInvokeProvider, ISelectionItemProvider
{
    public AvaloniaUiSceneNavigationItemAutomationPeer(AvaloniaUiSceneNodeControl owner)
        : base(owner)
    {
    }

    public bool IsSelected => SceneOwner.Node.IsSelected;

    public ISelectionProvider SelectionContainer
    {
        get
        {
            AvaloniaUiSceneNodeControl container = SceneOwner.SelectionContainer
                ?? throw new InvalidOperationException("A navigation item must have a scene navigation container.");
            return ControlAutomationPeer.CreatePeerForElement(container) as ISelectionProvider
                ?? throw new InvalidOperationException("The scene navigation container has no selection provider.");
        }
    }

    public void Invoke() => SceneOwner.InvokeFromAutomation();

    public void AddToSelection() => Invoke();

    public void RemoveFromSelection()
    {
        // Primary navigation requires one selected destination. Removing its only selection is
        // intentionally unsupported, matching ISelectionProvider.IsSelectionRequired.
    }

    public void Select() => Invoke();
}
