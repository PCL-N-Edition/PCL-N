namespace PCL.UI.Next;

public sealed partial class XsrUiRenderer
{
    private void PrepareTransition(XsrUiEntityId entity, XsrUiTransition transition, string? key,
        XsrUiRect bounds, XsrUiRect? parentClip)
    {
        bool changed = transition.HasPresentedKey && transition.PresentedKey != key
            || transition.MovesSelf && _scene is not null && transition.LastSceneVersion != _scene.Version;
        if (changed && !ReducedMotion)
        {
            transition.Outgoing = CaptureOutgoing(entity, transition);
            if (Math.Abs(transition.PresentedOffsetX) < .01) transition.PresentedOffsetX = transition.OffsetX;
            if (Math.Abs(transition.PresentedOffsetY) < .01) transition.PresentedOffsetY = transition.OffsetY;
            transition.StartOffsetX = transition.PresentedOffsetX;
            transition.StartOffsetY = transition.PresentedOffsetY;
        }
        transition.HasPresentedKey = true;
        transition.PresentedKey = key;
        transition.LastSceneVersion = _sceneVersion + 1;
        if (ReducedMotion) { transition.PresentedOffsetX = 0; transition.PresentedOffsetY = 0; }
        double remaining = Math.Clamp(Math.Max(
            transition.StartOffsetX == 0 ? 0 : transition.PresentedOffsetX / transition.StartOffsetX,
            transition.StartOffsetY == 0 ? 0 : transition.PresentedOffsetY / transition.StartOffsetY), 0, 1);
        if (remaining <= .0001) { transition.Outgoing = []; return; }
        XsrUiRect clip = parentClip is { } parent ? Intersect(bounds, parent) : bounds;
        double dx = -transition.OffsetX * (1 - remaining), dy = -transition.OffsetY * (1 - remaining);
        List<XsrUiSceneNode> outgoing = [];
        foreach (XsrUiSceneNode node in transition.Outgoing)
        {
            XsrUiRect moved = node.Rect with { X = node.Rect.X + dx, Y = node.Rect.Y + dy };
            XsrUiRect visible = Intersect(moved, clip);
            if (visible.Width <= 0 || visible.Height <= 0) continue;
            outgoing.Add(node with
            {
                Rect = moved,
                ClipRect = visible,
                PresentationOpacity = node.PresentationOpacity * remaining,
                IsAccessible = false,
                IsClickable = false,
                IsFocusable = false,
                IsFocused = false,
                IsFocusVisible = false,
                IsHovered = false,
                IsPressed = false,
                TextInput = null,
            });
        }
        if (outgoing.Count > 0) _outgoingLayers.Add(new(entity, outgoing, transition.MovesSelf));
    }

    private XsrUiSceneNode[] CaptureOutgoing(XsrUiEntityId entity, XsrUiTransition transition)
    {
        if (_scene is null) return [];
        List<XsrUiSceneNode> captured = [];
        int root = -1;
        for (int index = 0; index < _scene.Count; index++)
            if (_scene[index].Entity == entity) { root = index; break; }
        if (root < 0 && transition.Source.IsAssigned)
            for (int index = 0; index < _scene.Count; index++)
                if (_scene[index].Entity == transition.Source) { root = index; break; }
        if (root < 0) return [];
        if (transition.MovesSelf)
        {
            if (_scene[root].TextInput is not { IsPassword: true }) captured.Add(_scene[root]);
        }
        else
            for (int index = root + 1; index < _scene.Count && _scene[index].Depth > _scene[root].Depth; index++)
                if (_scene[index].TextInput is not { IsPassword: true }) captured.Add(_scene[index]);
        // Preserve the last presented outgoing pixels on rapid reversal, with a hard lifetime/size bound.
        foreach (XsrUiOutgoingLayer layer in _scene.Outgoing.Where(layer => layer.Group == entity))
            captured.AddRange(layer.Nodes);
        return captured.OrderByDescending(node => node.PresentationOpacity).Take(transition.MovesSelf ? 3 : 256).ToArray();
    }
}
