using Nexa.Xsr;

namespace Nexa.UI.Next;

public sealed partial class XsrUiRenderer
{
    public XsrUiEntityId FileDragTarget(XsrUiPoint point)
    {
        XsrUiEntityId entity = InputAt(point);
        return CanDrag(entity) ? entity : default;
    }

    private bool CanDrag(XsrUiEntityId entity) => entity.IsAssigned && _tree.IsAlive(entity)
        && IsInVisibleTree(entity) && IsEnabled(_tree.GetComponent<XsrUiInput>(entity))
        && _tree.GetComponent<XsrUiFileDrag>(entity) is { Paths.Count: > 0, Effects: not XsrUiFileDragEffects.None };

    public XsrUiFileDrag? BeginFileDrag(XsrUiEntityId entity)
    {
        if (!CanDrag(entity)) return null;
        CancelPointerGesture();
        return _tree.GetComponent<XsrUiFileDrag>(entity);
    }

    public void CompleteFileDrag(XsrUiEntityId entity, XsrUiFileDrag transfer)
    {
        // An entity generation cannot target a replacement page after navigation/discovery.
        if (_tree.IsAlive(entity) && IsInVisibleTree(entity) && transfer.Completed.IsAssigned)
            _sink?.Emit(transfer.Completed, entity, XsrCorrelationId.Create());
    }
}
