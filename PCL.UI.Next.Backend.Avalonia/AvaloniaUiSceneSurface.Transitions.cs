using Avalonia;
using PCL.UI.Next;

namespace PCL.UI.Next.Backend.Avalonia;

public sealed partial class AvaloniaUiSceneSurface
{
    private void ApplyOutgoingLayers(XsrUiScene scene)
    {
        int index = 0;
        foreach (XsrUiOutgoingLayer layer in scene.Outgoing)
        {
            if (!_controls.TryGetValue(layer.Group, out AvaloniaUiSceneNodeControl? group)) continue;
            int insertion = Children.IndexOf(group) + (layer.BehindSelf ? 0 : 1);
            foreach (XsrUiSceneNode node in layer.Nodes)
            {
                if (index == _outgoingControls.Count)
                    _outgoingControls.Add(new(_ => { }, _ => { }, () => true));
                AvaloniaUiSceneNodeControl control = _outgoingControls[index++];
                control.Apply(node);
                Children.Insert(insertion++, control);
            }
        }
        while (_outgoingControls.Count > index)
        {
            _outgoingControls[^1].ReleasePresentation();
            _outgoingControls.RemoveAt(_outgoingControls.Count - 1);
        }
    }

    private void ArrangeOutgoingLayers()
    {
        foreach (AvaloniaUiSceneNodeControl control in _outgoingControls)
        {
            XsrUiRect rect = control.Node.Rect;
            control.Arrange(new Rect(rect.X, rect.Y, rect.Width, rect.Height));
        }
    }
}
