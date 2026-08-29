using PCL.UI.Next;
using PCL.Xsr;
using PCL.Xsr.State;

namespace PCL.Pxml;

/// <summary>
/// Reports one deterministic load failure: unknown state paths are the runtime's discovery.
/// </summary>
public sealed class PxmlLoadException(string message) : InvalidOperationException(message)
{
}

/// <summary>
/// Loads a compiled PXML IR into a live entity tree. State paths resolve against the concrete
/// store, bindings become binding records, commands become semantic-ID command bindings. The
/// loader is a fixed mapping over closed IR data — no reflection, no runtime parsing.
/// </summary>
public sealed class PxmlUiLoader
{
    public static XsrUiEntityId Load(
        PxmlUiIr ir,
        XsrUiTree tree,
        XsrStateStore state,
        XsrUiEntityId parent)
    {
        ArgumentNullException.ThrowIfNull(ir);
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(state);
        if (!tree.IsAlive(parent))
        {
            throw new PxmlLoadException($"The load parent '{parent}' is not alive.");
        }

        return LoadNode(ir.Root, tree, state, parent);
    }

    private static XsrUiEntityId LoadNode(
        PxmlIrNode node,
        XsrUiTree tree,
        XsrStateStore state,
        XsrUiEntityId parent)
    {
        XsrUiEntityId entity = tree.Create(node.Kind.ToString());
        tree.Attach(entity, parent);

        if (node.Width is not null || node.Height is not null
            || node.Margin != default || node.Padding != default || !node.IsVisible)
        {
            tree.SetComponent(entity, new XsrUiElement
            {
                Width = node.Width,
                Height = node.Height,
                Margin = node.Margin,
                Padding = node.Padding,
                IsVisible = node.IsVisible,
            });
        }

        if (node.Kind == PxmlIrNodeKind.StackPanel)
        {
            tree.SetComponent(entity, new XsrUiStackPanel(node.Orientation) { Spacing = node.Spacing });
            if (node.Scrollable)
            {
                tree.SetComponent(entity, new XsrUiScroll());
            }
        }

        if (node.Kind == PxmlIrNodeKind.Text)
        {
            XsrUiText text = new(node.Content ?? string.Empty);
            PxmlIrBinding? textBinding = node.Bindings.FirstOrDefault(
                binding => binding.Property == XsrUiStateProperty.Text);
            if (textBinding is not null)
            {
                text.BoundState = ResolveState(state, textBinding.StatePath);
            }

            tree.SetComponent(entity, text);
        }

        if (node.Kind == PxmlIrNodeKind.Button)
        {
            tree.SetComponent(entity, new XsrUiInput
            {
                Focusable = node.Focusable,
                Clickable = node.Clickable,
            });

            if (node.Command is not null)
            {
                tree.SetComponent(entity, new XsrUiCommandBinding(XsrSemanticId.Parse(node.Command)));
            }
        }

        if (node.Kind == PxmlIrNodeKind.Image)
        {
            tree.SetComponent(entity, new XsrUiImage(node.ImageSource ?? string.Empty));
        }

        if (node.Role != XsrUiSemanticRole.None || node.Label is not null)
        {
            tree.SetComponent(entity, new XsrUiSemantic(node.Role, node.Label));
        }

        foreach (PxmlIrBinding binding in node.Bindings)
        {
            tree.BindState(entity, new XsrUiStateDependency(
                ResolveState(state, binding.StatePath),
                binding.Property,
                binding.DirtyKinds));
        }

        foreach (PxmlIrNode child in node.Children)
        {
            LoadNode(child, tree, state, entity);
        }

        return entity;
    }

    private static XsrStateId ResolveState(XsrStateStore state, string path)
    {
        XsrSemanticId semanticId = XsrSemanticId.Parse(path);
        if (!state.TryResolve(semanticId, out XsrStateId stateId))
        {
            throw new PxmlLoadException(
                $"The state path '{path}' is not registered in the target state store.");
        }

        return stateId;
    }
}
