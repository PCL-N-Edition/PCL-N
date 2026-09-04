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
/// Loads a compiled PXML host IR into a live entity tree. Validated semantic IDs resolve
/// against the concrete store, bindings become binding records, commands become semantic-ID
/// command bindings. The loader is a fixed mapping over closed IR data — no reflection, no
/// runtime parsing.
/// </summary>
public sealed class PxmlUiLoader
{
    public static XsrUiEntityId Load(
        PxmlHostIr ir,
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

        XsrUiEntityId root = tree.Create(ir.Root.Key ?? ir.Root.Kind.ToString());
        bool committed = false;
        try
        {
            PopulateNode(ir.Root, root, tree, state);
            tree.Attach(root, parent);
            committed = true;
            return root;
        }
        finally
        {
            if (!committed && tree.IsAlive(root))
            {
                tree.Destroy(root);
            }
        }
    }

    private static void PopulateNode(
        PxmlIrNode node,
        XsrUiEntityId entity,
        XsrUiTree tree,
        XsrStateStore state)
    {
        PxmlIrBinding? visibilityBinding = node.Bindings.FirstOrDefault(
            binding => binding.Property == XsrUiStateProperty.Visibility);
        if (node.Width is not null || node.Height is not null
            || node.MinWidth is not null || node.MaxWidth is not null
            || node.MinHeight is not null || node.MaxHeight is not null
            || node.Weight != 0
            || node.HorizontalAlignment != XsrUiAlignment.Stretch
            || node.VerticalAlignment != XsrUiAlignment.Stretch
            || node.Margin != default || node.Padding != default || !node.IsVisible
            || visibilityBinding is not null)
        {
            tree.SetComponent(entity, new XsrUiElement
            {
                Width = node.Width,
                Height = node.Height,
                MinWidth = node.MinWidth,
                MaxWidth = node.MaxWidth,
                MinHeight = node.MinHeight,
                MaxHeight = node.MaxHeight,
                Weight = node.Weight,
                HorizontalAlignment = node.HorizontalAlignment,
                VerticalAlignment = node.VerticalAlignment,
                Margin = node.Margin,
                Padding = node.Padding,
                IsVisible = node.IsVisible,
                BoundVisibility = visibilityBinding is null
                    ? default
                    : ResolveState(state, visibilityBinding.State),
            });
        }

        switch (node.Recipe)
        {
            case PxmlRuntimeRecipe.TextInput:
                tree.SetComponent(entity, new XsrUiTextInput { Placeholder = node.Placeholder ?? string.Empty, IsPassword = node.IsPassword });
                PxmlIrBinding? inputEnabled = node.Bindings.FirstOrDefault(binding => binding.Property == XsrUiStateProperty.Enabled);
                tree.SetComponent(entity, new XsrUiInput
                {
                    Focusable = true,
                    Enabled = node.Enabled,
                    BoundEnabled = inputEnabled is null ? default : ResolveState(state, inputEnabled.State),
                });
                break;
            case PxmlRuntimeRecipe.Element:
                break;
            case PxmlRuntimeRecipe.VerticalPager:
                tree.SetComponent(entity, new XsrUiPager());
                tree.SetComponent(entity, new XsrUiInput { Focusable = true });
                break;
            case PxmlRuntimeRecipe.Progress:
                XsrUiProgress progress = new();
                PxmlIrBinding? valueBinding = node.Bindings.FirstOrDefault(
                    binding => binding.Property == XsrUiStateProperty.Value);
                if (valueBinding is not null)
                {
                    progress.BoundState = ResolveState(state, valueBinding.State);
                }

                tree.SetComponent(entity, progress);
                break;
            case PxmlRuntimeRecipe.StackLayout:
                tree.SetComponent(entity, new XsrUiStackPanel(node.Orientation)
                {
                    Spacing = node.Spacing,
                    StretchLastChild = node.StretchLastChild,
                });
                if (node.Scrollable)
                {
                    tree.SetComponent(entity, new XsrUiScroll());
                }

                break;
            case PxmlRuntimeRecipe.Text:
                XsrUiText text = new(node.Content ?? string.Empty);
                PxmlIrBinding? textBinding = node.Bindings.FirstOrDefault(
                    binding => binding.Property == XsrUiStateProperty.Text);
                if (textBinding is not null)
                {
                    text.BoundState = ResolveState(state, textBinding.State);
                }

                tree.SetComponent(entity, text);
                break;
            case PxmlRuntimeRecipe.CommandInput:
                XsrUiInput commandInput = new()
                {
                    Focusable = node.Focusable,
                    Clickable = node.Clickable,
                };
                PxmlIrBinding? enabledBinding = node.Bindings.FirstOrDefault(
                    binding => binding.Property == XsrUiStateProperty.Enabled);
                if (enabledBinding is not null)
                {
                    // Clickability follows the bound state fact, mirroring BoundVisibility.
                    commandInput.BoundEnabled = ResolveState(state, enabledBinding.State);
                }

                tree.SetComponent(entity, commandInput);

                if (node.Command is not null)
                {
                    // The IR carries a validated semantic ID; no repeated parsing.
                    tree.SetComponent(entity, new XsrUiCommandBinding(node.Command.Value));
                }

                PxmlIrBinding? commandTextBinding = node.Bindings.FirstOrDefault(
                    binding => binding.Property == XsrUiStateProperty.Text);
                if (node.Content is not null || commandTextBinding is not null)
                {
                    XsrUiText commandText = new(node.Content ?? string.Empty);
                    if (commandTextBinding is not null)
                    {
                        commandText.BoundState = ResolveState(state, commandTextBinding.State);
                    }

                    tree.SetComponent(entity, commandText);
                }

                if (node.ImageSource is not null)
                {
                    tree.SetComponent(entity, new XsrUiImage(node.ImageSource));
                }

                break;
            case PxmlRuntimeRecipe.Image:
                tree.SetComponent(entity, new XsrUiImage(node.ImageSource ?? string.Empty));
                break;
            default:
                throw new PxmlLoadException($"The runtime recipe '{node.Recipe}' is unsupported.");
        }

        PxmlIrBinding? transitionBinding = node.Bindings.FirstOrDefault(binding => binding.Property == XsrUiStateProperty.TransitionKey);
        if (node.TransitionKey is not null || transitionBinding is not null)
            tree.SetComponent(entity, new XsrUiTransition
            {
                Key = node.TransitionKey,
                OffsetX = node.TransitionOffsetX,
                OffsetY = node.TransitionOffsetY,
                MovesSelf = node.Recipe is PxmlRuntimeRecipe.Text or PxmlRuntimeRecipe.CommandInput or PxmlRuntimeRecipe.Image,
                BoundKey = transitionBinding is null ? default : ResolveState(state, transitionBinding.State),
            });

        PxmlIrBinding? semanticBinding = node.Bindings.FirstOrDefault(
            binding => binding.Property == XsrUiStateProperty.SemanticLabel);
        if (node.Role != XsrUiSemanticRole.None || node.Label is not null || semanticBinding is not null)
        {
            XsrUiSemantic semantic = new(node.Role, node.Label);
            if (semanticBinding is not null)
            {
                semantic.BoundLabel = ResolveState(state, semanticBinding.State);
            }

            tree.SetComponent(entity, semantic);
        }

        foreach (PxmlIrBinding binding in node.Bindings)
        {
            tree.BindState(entity, new XsrUiStateDependency(
                ResolveState(state, binding.State),
                binding.Property,
                binding.DirtyKinds));
        }

        foreach (PxmlIrNode child in node.Children)
        {
            XsrUiEntityId childEntity = tree.Create(child.Key ?? child.Kind.ToString());
            tree.Attach(childEntity, entity);
            PopulateNode(child, childEntity, tree, state);
        }
    }

    private static XsrStateId ResolveState(XsrStateStore state, XsrSemanticId semanticId)
    {
        // The IR already carries a validated semantic ID; load time only resolves it through
        // the registry — no repeated parsing.
        if (!state.TryResolve(semanticId, out XsrStateId stateId))
        {
            throw new PxmlLoadException(
                $"The state '{semanticId}' is not registered in the target state store.");
        }

        return stateId;
    }
}
