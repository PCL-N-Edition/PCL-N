using System.Reflection;
using PCL.Pxml;
using PCL.UI.Next;
using PCL.Xsr;
using PCL.Xsr.State;

namespace PCL.Desktop.Ui;

/// <summary>
/// Loads the Desktop shell from the checked-in PXML template and adapts its compiled handles to
/// the UI.Next shell contract. PXML owns structure and order; the platform backend owns pixels.
/// </summary>
public static class PxmlShellComposer
{
    private const string ResourceSuffix = "Ui.Shell.pxml";

    public static XsrUiShell Compose(
        XsrStateStore state,
        XsrUiShellOptions? options = null,
        IXsrUiIntentSink? intentSink = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        options ??= new XsrUiShellOptions();

        PxmlDocument document = PxmlParser.Parse(ReadTemplate());
        PxmlHostIr ir = PxmlCompiler.Compile(document);
        XsrUiTree tree = new();
        XsrUiEntityId templateHost = tree.Create("pxml-shell-host");
        XsrUiEntityId root = PxmlUiLoader.Load(ir, tree, state, templateHost);
        tree.Detach(root);
        tree.Destroy(templateHost);

        XsrUiEntityId titleBar = FindDirectChild(tree, root, XsrUiSemanticRole.TitleBar);
        XsrUiEntityId body = tree.Children(root).Single(child =>
            !child.Equals(titleBar) && tree.GetComponent<XsrUiStackPanel>(child) is not null);
        XsrUiEntityId navigation = FindDirectChild(tree, body, XsrUiSemanticRole.Navigation);
        XsrUiEntityId content = FindDescendant(tree, body, XsrUiSemanticRole.Content);

        IReadOnlyList<XsrUiShellNavigationItem> defaults = XsrUiShell.DefaultNavigationItems;
        XsrUiEntityId[] itemEntities = [.. tree.Children(navigation)];
        if (itemEntities.Length != defaults.Count)
        {
            throw new InvalidOperationException(
                $"The PXML shell declares {itemEntities.Length} navigation items; expected {defaults.Count}.");
        }

        Dictionary<XsrSemanticId, XsrUiEntityId> navigationEntities = [];
        for (int index = 0; index < itemEntities.Length; index++)
        {
            XsrUiEntityId entity = itemEntities[index];
            XsrUiSemantic semantic = tree.GetComponent<XsrUiSemantic>(entity)
                ?? throw new InvalidOperationException("A PXML navigation item has no semantic component.");
            XsrUiCommandBinding command = tree.GetComponent<XsrUiCommandBinding>(entity)
                ?? throw new InvalidOperationException("A PXML navigation item has no command binding.");
            XsrUiShellNavigationItem expected = defaults[index];
            if (!string.Equals(semantic.Label, expected.Label, StringComparison.Ordinal)
                || command.Command != expected.Command)
            {
                throw new InvalidOperationException(
                    $"PXML navigation item {index} does not match '{expected.Id}'.");
            }

            navigationEntities.Add(expected.Id, entity);
        }

        XsrUiEntityId[] titleChildren = [.. tree.Children(titleBar)];
        if (titleChildren.Length >= 2)
        {
            if (tree.GetComponent<XsrUiText>(titleChildren[0]) is { } titleText)
            {
                titleText.Content = options.Title;
                tree.MarkDirty(titleChildren[0], XsrUiDirtyKinds.Paint);
            }

            if (tree.GetComponent<XsrUiText>(titleChildren[1]) is { } versionText)
            {
                versionText.Content = options.Version;
                tree.MarkDirty(titleChildren[1], XsrUiDirtyKinds.Paint);
            }
        }

        XsrUiShellTemplate template = new(
            tree,
            root,
            titleBar,
            body,
            navigation,
            content,
            defaults,
            navigationEntities);
        return XsrUiShellComposer.Compose(state, template, options, intentSink);
    }

    private static XsrUiEntityId FindDirectChild(XsrUiTree tree, XsrUiEntityId parent, XsrUiSemanticRole role) =>
        tree.Children(parent).Single(child => tree.GetComponent<XsrUiSemantic>(child)?.Role == role);

    private static XsrUiEntityId FindDescendant(XsrUiTree tree, XsrUiEntityId root, XsrUiSemanticRole role)
    {
        XsrUiEntityId found = default;
        tree.Walk(
            root,
            entity =>
            {
                if (tree.GetComponent<XsrUiSemantic>(entity)?.Role == role)
                {
                    found = entity;
                    return false;
                }

                return true;
            });
        return found.IsAssigned
            ? found
            : throw new InvalidOperationException($"The PXML shell has no '{role}' entity.");
    }

    private static string ReadTemplate()
    {
        Assembly assembly = typeof(PxmlShellComposer).Assembly;
        string resourceName = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith(ResourceSuffix, StringComparison.Ordinal));
        using Stream stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"The embedded PXML shell resource '{resourceName}' is missing.");
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }
}
