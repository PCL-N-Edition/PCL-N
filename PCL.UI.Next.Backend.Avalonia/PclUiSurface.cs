// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Automation.Peers;
using Avalonia.Media;
using PCL.UI.Next;

namespace PCL.UI.Next.Backend.Avalonia;

/// <summary>
/// Single Avalonia visual that paints the retained ECS scene. Runtime entities do not
/// become Avalonia Controls; native hosts can be layered separately by the window host.
/// </summary>
public sealed class PclUiSurface : Control
{
    private readonly AvaloniaTextEngine _textEngine;
    private readonly HeadlessUiBackend _retained = new();
    private readonly Dictionary<UiColor, SolidColorBrush> _brushes = [];
    private readonly AvaloniaAccessibilityBridge _accessibility;

    public PclUiSurface(AvaloniaTextEngine textEngine)
    {
        _textEngine = textEngine ?? throw new ArgumentNullException(nameof(textEngine));
        IsHitTestVisible = true;
        Focusable = true;
        ClipToBounds = true;
        _accessibility = new AvaloniaAccessibilityBridge(this);
    }

    public int RetainedNodeCount => _retained.NodeCount;

    public int CommitCount => _retained.CommitCount;

    public UiSemanticTreeSnapshot AccessibilityTree => _accessibility.Tree;

    internal Action<UiAccessibilityActionRequest>? AccessibilityActionSink { get; set; }

    internal HeadlessUiBackend RetainedState => _retained;

    internal void Initialize(in UiBackendContext context) => _retained.Initialize(in context);

    internal void Apply(in UiCommitBatch batch) => _retained.Commit(in batch);

    internal void ApplyAccessibility(UiSemanticTreeSnapshot tree) => _accessibility.Update(tree);

    internal void RaiseAccessibilityAction(UiAccessibilityActionRequest request) =>
        AccessibilityActionSink?.Invoke(request);

    protected override AutomationPeer OnCreateAutomationPeer() => _accessibility.CreatePeer();

    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        base.Render(context);
        IReadOnlyList<RenderNodeId> roots = _retained.Roots;
        for (int i = 0; i < roots.Count; i++)
            RenderNode(context, roots[i]);
    }

    private void RenderNode(DrawingContext context, RenderNodeId id)
    {
        if (!_retained.TryGetNode(id, out UiRenderNodeSnapshot node) || node.Opacity <= 0f)
            return;

        Matrix3x2 transform = node.Transform;
        Matrix matrix = new(
            transform.M11,
            transform.M12,
            transform.M21,
            transform.M22,
            transform.M31,
            transform.M32);
        using (context.PushTransform(matrix))
        using (context.PushOpacity(node.Opacity))
        {
            DrawPrimitive(context, in node);
            IReadOnlyList<RenderNodeId> children = _retained.GetChildren(id);
            if (node.Kind == UiRenderNodeKind.Clip)
            {
                Rect clip = new(node.Bounds.X, node.Bounds.Y, node.Bounds.Width, node.Bounds.Height);
                using (context.PushClip(clip))
                {
                    for (int i = 0; i < children.Count; i++)
                        RenderNode(context, children[i]);
                }
            }
            else
            {
                for (int i = 0; i < children.Count; i++)
                    RenderNode(context, children[i]);
            }
        }
    }

    private void DrawPrimitive(DrawingContext context, in UiRenderNodeSnapshot node)
    {
        if (node.Kind == UiRenderNodeKind.Text)
        {
            if (!node.TextLayout.IsNone && node.Brush.A > 0)
                _textEngine.Draw(node.TextLayout, context, new Point(node.Bounds.X, node.Bounds.Y), node.Brush);
            return;
        }

        if (node.Kind is not (UiRenderNodeKind.Rectangle or UiRenderNodeKind.RoundedRectangle or UiRenderNodeKind.Clip) ||
            node.Brush.A == 0 ||
            node.Bounds.Width <= 0f ||
            node.Bounds.Height <= 0f)
        {
            return;
        }

        Rect bounds = new(node.Bounds.X, node.Bounds.Y, node.Bounds.Width, node.Bounds.Height);
        double radius = node.Kind is UiRenderNodeKind.RoundedRectangle or UiRenderNodeKind.Clip
            ? node.CornerRadius
            : 0d;
        context.DrawRectangle(GetBrush(node.Brush), null, bounds, radius, radius);
    }

    private SolidColorBrush GetBrush(UiColor color)
    {
        if (_brushes.TryGetValue(color, out SolidColorBrush? brush))
            return brush;
        brush = new SolidColorBrush(Color.FromArgb(color.A, color.R, color.G, color.B));
        _brushes.Add(color, brush);
        return brush;
    }
}
