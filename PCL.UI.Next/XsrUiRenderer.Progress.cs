using System.Globalization;

namespace PCL.UI.Next;

public sealed partial class XsrUiRenderer
{
    /// <summary>
    /// Advances the presented fill fraction from a backend clock. The state-bound target is
    /// never written directly into the layout: fast stage jumps catch up through this
    /// presented value, and reduced motion snaps straight to the target.
    /// </summary>
    public void SetProgressPresentation(XsrUiEntityId entity, double value)
    {
        if (!double.IsFinite(value) || !_tree.IsAlive(entity)
            || _tree.GetComponent<XsrUiProgress>(entity) is not { } progress) return;
        double clamped = Math.Clamp(value, 0, 1);
        if (progress.Presented == clamped) return;
        progress.Presented = clamped;
        _tree.MarkDirty(entity, XsrUiDirtyKinds.Layout);
    }

    /// <summary>Reads the state-bound fill target for the backend's catch-up animation.</summary>
    public double GetProgressTarget(XsrUiEntityId entity)
    {
        if (!entity.IsAssigned || !_tree.IsAlive(entity)
            || _tree.GetComponent<XsrUiProgress>(entity) is not { } progress) return 0;
        if (progress.BoundState.IsAssigned)
        {
            object? value = _state.ReadAppliedValue(progress.BoundState);
            if (value is not null
                && double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
            {
                progress.Target = Math.Clamp(parsed, 0, 1);
            }
        }

        return progress.Target;
    }
}
