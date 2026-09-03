using PCL.Xsr;
using PCL.Xsr.State;

namespace PCL.UI.Next;

/// <summary>
/// Selects the product shell presentation. Both variants share the same semantic tree and
/// navigation IDs; only the palette and material tokens change.
/// </summary>
public enum XsrUiShellStyle
{
    Experimental = 0,
    LiquidGlass = 1,
}

/// <summary>
/// One stable destination in the product's primary navigation.
/// </summary>
public sealed class XsrUiShellNavigationItem
{
    public XsrUiShellNavigationItem(string id, string label, string icon)
        : this(XsrSemanticId.Parse(id), label, icon)
    {
    }

    public XsrUiShellNavigationItem(
        XsrSemanticId id,
        string label,
        string icon,
        XsrSemanticId? command = null)
    {
        if (!id.IsAssigned)
        {
            throw new ArgumentException("A navigation item requires an assigned semantic ID.", nameof(id));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentException.ThrowIfNullOrWhiteSpace(icon);
        if (command is { IsAssigned: false })
        {
            throw new ArgumentException("A navigation command must be assigned.", nameof(command));
        }

        Id = id;
        Label = label;
        Icon = icon;
        Command = command ?? XsrSemanticId.Parse($"ui.{id.Value}");
    }

    public XsrSemanticId Id { get; }

    public string Label { get; }

    public string Icon { get; }

    public XsrSemanticId Command { get; }
}

/// <summary>
/// Options for the framework-neutral shell composition.
/// </summary>
public sealed class XsrUiShellOptions
{
    public XsrUiShellStyle Style { get; init; } = XsrUiShellStyle.Experimental;

    public string Title { get; init; } = "PCL Nexa";

    public string Version { get; init; } = "2.0.0.alpha.1";

    public IReadOnlyList<XsrUiShellNavigationItem>? NavigationItems { get; init; }

    public XsrSemanticId? InitialNavigationId { get; init; }
}

/// <summary>
/// Backend-neutral material tokens for the two product shell variants.
/// </summary>
public readonly record struct XsrUiShellPalette(
    XsrUiColor WindowBackground,
    XsrUiColor TitleBarBackground,
    XsrUiColor TitleBarText,
    XsrUiColor NavigationBackground,
    XsrUiColor ContentBackground,
    XsrUiColor SurfaceBorder,
    XsrUiColor PrimaryText,
    XsrUiColor SecondaryText,
    XsrUiColor Accent,
    XsrUiColor NavigationHover,
    XsrUiColor SelectedNavigationText,
    XsrUiColor NavigationIcon,
    XsrUiSurfaceKind TitleBarSurface,
    XsrUiSurfaceKind NavigationSurface,
    XsrUiSurfaceKind ContentSurface,
    double CornerRadius,
    double BlurRadius,
    double BorderWidth)
{
    public static XsrUiShellPalette For(XsrUiShellStyle style) => style switch
    {
        // The Experimental style mirrors the legacy experimental base plate: a solid accent title
        // bar with white text over a light window, an icon rail, and a soft accent hover tint.
        XsrUiShellStyle.Experimental => new(
            WindowBackground: new XsrUiColor(251, 251, 251),
            TitleBarBackground: new XsrUiColor(19, 112, 243),
            TitleBarText: new XsrUiColor(255, 255, 255),
            NavigationBackground: new XsrUiColor(243, 247, 252),
            ContentBackground: new XsrUiColor(251, 251, 251),
            SurfaceBorder: new XsrUiColor(224, 234, 253),
            PrimaryText: new XsrUiColor(52, 61, 74),
            SecondaryText: new XsrUiColor(122, 138, 153),
            Accent: new XsrUiColor(19, 112, 243),
            NavigationHover: new XsrUiColor(213, 230, 253),
            SelectedNavigationText: new XsrUiColor(11, 91, 203),
            NavigationIcon: new XsrUiColor(52, 61, 74),
            TitleBarSurface: XsrUiSurfaceKind.Solid,
            NavigationSurface: XsrUiSurfaceKind.Solid,
            ContentSurface: XsrUiSurfaceKind.Solid,
            CornerRadius: 8,
            BlurRadius: 0,
            BorderWidth: 1),
        XsrUiShellStyle.LiquidGlass => new(
            WindowBackground: new XsrUiColor(8, 17, 30),
            TitleBarBackground: new XsrUiColor(32, 47, 66, 224),
            TitleBarText: new XsrUiColor(246, 249, 255),
            NavigationBackground: new XsrUiColor(255, 255, 255, 24),
            ContentBackground: new XsrUiColor(11, 21, 35, 232),
            SurfaceBorder: new XsrUiColor(255, 255, 255, 54),
            PrimaryText: new XsrUiColor(246, 249, 255),
            SecondaryText: new XsrUiColor(191, 207, 224),
            Accent: new XsrUiColor(106, 169, 255),
            NavigationHover: new XsrUiColor(106, 169, 255, 78),
            SelectedNavigationText: new XsrUiColor(255, 255, 255),
            NavigationIcon: new XsrUiColor(207, 222, 240),
            TitleBarSurface: XsrUiSurfaceKind.Glass,
            NavigationSurface: XsrUiSurfaceKind.Translucent,
            ContentSurface: XsrUiSurfaceKind.Solid,
            CornerRadius: 14,
            BlurRadius: 24,
            BorderWidth: 1),
        _ => throw new ArgumentOutOfRangeException(nameof(style), style, "Unknown shell style."),
    };
}

/// <summary>
/// Stable semantic IDs used by the shell itself.
/// </summary>
public static class XsrUiShellIds
{
    public static readonly XsrSemanticId NavigationSelect = XsrSemanticId.Parse("ui.navigation.select");

    public static readonly XsrSemanticId NavigationExpand = XsrSemanticId.Parse("ui.navigation.expand");

    public static readonly XsrSemanticId StyleToggle = XsrSemanticId.Parse("ui.shell.style.toggle");

    public static readonly XsrSemanticId WindowMinimize = XsrSemanticId.Parse("ui.window.minimize");

    public static readonly XsrSemanticId WindowMaximize = XsrSemanticId.Parse("ui.window.maximize");

    public static readonly XsrSemanticId WindowClose = XsrSemanticId.Parse("ui.window.close");
}

/// <summary>
/// Event data for a primary-navigation selection change.
/// </summary>
public sealed class XsrUiShellNavigationChangedEventArgs(
    XsrSemanticId previous,
    XsrSemanticId current) : EventArgs
{
    public XsrSemanticId Previous { get; } = previous;

    public XsrSemanticId Current { get; } = current;
}

/// <summary>
/// Handles returned by a PXML shell template. The template describes structure; XsrUiShell adds
/// selection, palette, and intent behavior without coupling UI.Next to the PXML compiler.
/// </summary>
public sealed class XsrUiShellTemplate
{
    public XsrUiShellTemplate(
        XsrUiTree tree,
        XsrUiEntityId root,
        XsrUiEntityId titleBar,
        XsrUiEntityId body,
        XsrUiEntityId navigation,
        XsrUiEntityId content,
        IReadOnlyList<XsrUiShellNavigationItem> navigationItems,
        IReadOnlyDictionary<XsrSemanticId, XsrUiEntityId> navigationEntities)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(navigationItems);
        ArgumentNullException.ThrowIfNull(navigationEntities);
        if (!tree.IsAlive(root)
            || !tree.IsAlive(titleBar)
            || !tree.IsAlive(body)
            || !tree.IsAlive(navigation)
            || !tree.IsAlive(content))
        {
            throw new ArgumentException("A shell template can contain only live entities.", nameof(tree));
        }

        if (!tree.Parent(titleBar).Equals(root)
            || !tree.Parent(body).Equals(root)
            || !tree.Parent(navigation).Equals(body)
            || !tree.Parent(content).Equals(body))
        {
            throw new ArgumentException("A shell template has an invalid chrome hierarchy.", nameof(tree));
        }

        if (navigationItems.Count == 0 || navigationItems.Count != navigationEntities.Count)
        {
            throw new ArgumentException("A shell template requires one entity for every navigation item.", nameof(navigationItems));
        }

        Tree = tree;
        Root = root;
        TitleBar = titleBar;
        Body = body;
        Navigation = navigation;
        Content = content;
        NavigationItems = [.. navigationItems];
        NavigationEntities = new Dictionary<XsrSemanticId, XsrUiEntityId>(navigationEntities);
        foreach (XsrUiShellNavigationItem item in NavigationItems)
        {
            ArgumentNullException.ThrowIfNull(item);
            if (!NavigationEntities.TryGetValue(item.Id, out XsrUiEntityId entity)
                || !tree.IsAlive(entity)
                || !tree.Parent(entity).Equals(navigation))
            {
                throw new ArgumentException(
                    $"Navigation item '{item.Id}' is not attached to the template navigation rail.",
                    nameof(navigationEntities));
            }
        }
    }

    public XsrUiTree Tree { get; }

    public XsrUiEntityId Root { get; }

    public XsrUiEntityId TitleBar { get; }

    public XsrUiEntityId Body { get; }

    public XsrUiEntityId Navigation { get; }

    public XsrUiEntityId Content { get; }

    public IReadOnlyList<XsrUiShellNavigationItem> NavigationItems { get; }

    public IReadOnlyDictionary<XsrSemanticId, XsrUiEntityId> NavigationEntities { get; }
}

/// <summary>
/// Shared product chrome: title bar, primary navigation, and content host. The shell is a
/// framework-neutral UI.Next tree so tests and non-Avalonia hosts see the same semantics and
/// layout. Avalonia is only a presentation edge over this contract.
/// </summary>
public sealed class XsrUiShell
{
    /// <summary>The canonical title bar height in logical pixels.</summary>
    public const double TitleBarHeight = 52;

    /// <summary>The collapsed icon-rail width in logical pixels.</summary>
    public const double CollapsedRailWidth = 48;

    /// <summary>The expanded rail width in logical pixels.</summary>
    public const double ExpandedRailWidth = 120;

    /// <summary>The canonical navigation item height in logical pixels.</summary>
    public const double NavigationItemHeight = 42;

    private static readonly XsrUiShellNavigationItem[] BuiltInNavigationItems =
    [
        new("navigation.launch", "启动", "lucide/play"),
        new("navigation.download", "安装", "lucide/package-plus"),
        new("navigation.community", "资源", "lucide/blocks"),
        new("navigation.settings", "设置", "lucide/settings"),
    ];

    public static IReadOnlyList<XsrUiShellNavigationItem> DefaultNavigationItems =>
        [.. BuiltInNavigationItems];

    private readonly Dictionary<XsrSemanticId, XsrUiEntityId> _navigationEntities = [];
    private readonly Dictionary<XsrUiEntityId, XsrSemanticId> _navigationIds = [];
    private readonly IXsrUiIntentSink? _externalIntentSink;
    private XsrUiEntityId _navigationToggle;

    public XsrUiShell(
        XsrStateStore state,
        XsrUiShellOptions? options = null,
        IXsrUiIntentSink? intentSink = null,
        XsrUiStateBridge? stateBridge = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        options ??= new XsrUiShellOptions();
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Title);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Version);

        XsrUiShellNavigationItem[] navigationItems =
            options.NavigationItems is null ? [.. BuiltInNavigationItems] : [.. options.NavigationItems];
        if (navigationItems.Length == 0)
        {
            throw new ArgumentException("The shell requires at least one navigation item.", nameof(options));
        }

        foreach (XsrUiShellNavigationItem item in navigationItems)
        {
            ArgumentNullException.ThrowIfNull(item);
            if (!_navigationEntities.TryAdd(item.Id, default))
            {
                throw new ArgumentException($"Duplicate navigation ID '{item.Id}'.", nameof(options));
            }
        }

        _externalIntentSink = intentSink;
        NavigationItems = navigationItems;
        Title = options.Title;
        Version = options.Version;
        Style = options.Style;
        Palette = XsrUiShellPalette.For(Style);

        Tree = stateBridge?.Tree ?? new XsrUiTree();
        StateBridge = stateBridge;
        Stage = new XsrUiStage(Tree, state, new ShellIntentSink(this), stateBridge);
        Root = Stage.Root;
        Content = Stage.ContentHost;

        Tree.SetComponent(Root, new XsrUiElement());
        Tree.SetComponent(Root, new XsrUiStackPanel(XsrUiOrientation.Vertical) { StretchLastChild = true });
        Tree.SetComponent(Root, new XsrUiSemantic(XsrUiSemanticRole.Page, options.Title));
        Tree.SetComponent(Root, new XsrUiVisualStyle());

        TitleBar = Tree.Create("title-bar");
        Tree.SetComponent(TitleBar, new XsrUiElement
        {
            Height = TitleBarHeight,
        });
        Tree.SetComponent(TitleBar, new XsrUiStackPanel(XsrUiOrientation.Horizontal));
        Tree.SetComponent(TitleBar, new XsrUiSemantic(XsrUiSemanticRole.TitleBar, "标题栏"));
        Tree.SetComponent(TitleBar, new XsrUiVisualStyle());

        XsrUiEntityId title = Tree.Create("title");
        Tree.SetComponent(title, new XsrUiElement
        {
            Margin = new XsrUiThickness(20, 0, 0, 0),
            VerticalAlignment = XsrUiAlignment.Center,
        });
        Tree.SetComponent(title, new XsrUiText(options.Title));
        Tree.SetComponent(title, new XsrUiSemantic(XsrUiSemanticRole.Text, options.Title));
        Tree.Attach(title, TitleBar);

        XsrUiEntityId version = Tree.Create("version");
        Tree.SetComponent(version, new XsrUiElement
        {
            Margin = new XsrUiThickness(12, 0, 0, 0),
            VerticalAlignment = XsrUiAlignment.Center,
        });
        Tree.SetComponent(version, new XsrUiText($"{options.Version}"));
        Tree.SetComponent(version, new XsrUiSemantic(XsrUiSemanticRole.Text, "版本"));
        Tree.Attach(version, TitleBar);

        Body = Tree.Create("body");
        Tree.SetComponent(Body, new XsrUiElement());
        Tree.SetComponent(Body, new XsrUiStackPanel(XsrUiOrientation.Horizontal) { StretchLastChild = true });
        Tree.Attach(TitleBar, Root);
        Tree.Attach(Body, Root);

        Navigation = Tree.Create("main-navigation");
        Tree.SetComponent(Navigation, new XsrUiElement
        {
            Width = CollapsedRailWidth,
        });
        Tree.SetComponent(Navigation, new XsrUiStackPanel(XsrUiOrientation.Vertical) { Spacing = 6 });
        Tree.SetComponent(Navigation, new XsrUiSemantic(XsrUiSemanticRole.Navigation, "主导航"));
        Tree.SetComponent(Navigation, new XsrUiVisualStyle());
        Tree.Attach(Navigation, Body);

        Tree.Detach(Content);
        Tree.SetComponent(Content, new XsrUiElement());
        Tree.SetComponent(Content, new XsrUiSemantic(XsrUiSemanticRole.Content, "内容区域"));
        Tree.SetComponent(Content, new XsrUiVisualStyle());
        Tree.Attach(Content, Body);

        XsrSemanticId initial = options.InitialNavigationId ?? navigationItems[0].Id;
        if (!_navigationEntities.ContainsKey(initial))
        {
            throw new ArgumentException($"Initial navigation ID '{initial}' is not registered.", nameof(options));
        }

        for (int index = 0; index < navigationItems.Length; index++)
        {
            XsrUiShellNavigationItem item = navigationItems[index];
            XsrUiEntityId entity = Tree.Create($"navigation-item:{item.Id.Value}");
            Tree.SetComponent(entity, new XsrUiElement
            {
                Height = NavigationItemHeight,
                HorizontalAlignment = XsrUiAlignment.Stretch,
                VerticalAlignment = XsrUiAlignment.Center,
            });
            Tree.SetComponent(entity, new XsrUiImage(item.Icon));
            Tree.SetComponent(entity, new XsrUiText(item.Label));
            Tree.SetComponent(entity, new XsrUiSemantic(XsrUiSemanticRole.NavigationItem, item.Label));
            Tree.SetComponent(entity, new XsrUiInput { Focusable = true, Clickable = true });
            Tree.SetComponent(entity, new XsrUiCommandBinding(item.Command));
            Tree.SetComponent(entity, new XsrUiSelection { IsSelected = item.Id == initial });
            Tree.SetComponent(entity, new XsrUiVisualStyle());
            Tree.Attach(entity, Navigation);
            _navigationEntities[item.Id] = entity;
            _navigationIds[entity] = item.Id;
        }

        SelectedNavigationId = initial;
        FinishComposition();
    }

    /// <summary>
    /// Creates a shell over a structure produced by PXML. UI.Next receives only the compiled
    /// entity handles, so the PXML compiler remains an outer composition concern.
    /// </summary>
    public XsrUiShell(
        XsrStateStore state,
        XsrUiShellTemplate template,
        XsrUiShellOptions? options = null,
        IXsrUiIntentSink? intentSink = null,
        XsrUiStateBridge? stateBridge = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(template);
        options ??= new XsrUiShellOptions();
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Title);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Version);
        if (options.NavigationItems is not null
            && !options.NavigationItems.Select(item => item.Id).SequenceEqual(
                template.NavigationItems.Select(item => item.Id)))
        {
            throw new ArgumentException(
                "PXML shell navigation IDs must match the supplied shell options in order.",
                nameof(options));
        }

        XsrUiShellNavigationItem[] navigationItems = [.. template.NavigationItems];
        XsrSemanticId initial = options.InitialNavigationId ?? navigationItems[0].Id;
        if (!template.NavigationEntities.ContainsKey(initial))
        {
            throw new ArgumentException($"Initial navigation ID '{initial}' is not registered.", nameof(options));
        }

        _externalIntentSink = intentSink;
        NavigationItems = navigationItems;
        Title = options.Title;
        Version = options.Version;
        Style = options.Style;
        Palette = XsrUiShellPalette.For(Style);
        Tree = template.Tree;
        if (stateBridge is not null && !ReferenceEquals(stateBridge.Tree, Tree))
        {
            throw new ArgumentException(
                "The state bridge must observe the PXML template tree.",
                nameof(stateBridge));
        }
        StateBridge = stateBridge;
        Root = template.Root;
        TitleBar = template.TitleBar;
        Body = template.Body;
        Navigation = template.Navigation;
        Content = template.Content;
        foreach ((XsrSemanticId id, XsrUiEntityId entity) in template.NavigationEntities)
        {
            _navigationEntities.Add(id, entity);
            _navigationIds.Add(entity, id);
            XsrUiSelection? selection = Tree.GetComponent<XsrUiSelection>(entity);
            if (selection is null)
            {
                selection = new XsrUiSelection();
                Tree.SetComponent(entity, selection);
            }

            selection.IsSelected = id == initial;

            if (Tree.GetComponent<XsrUiVisualStyle>(entity) is null)
            {
                Tree.SetComponent(entity, new XsrUiVisualStyle());
            }
        }

        SelectedNavigationId = initial;
        Stage = new XsrUiStage(Tree, state, Root, Content, new ShellIntentSink(this), stateBridge);
        FinishComposition();
    }

    public event EventHandler<XsrUiShellNavigationChangedEventArgs>? NavigationChanged;

    public event EventHandler? NavigationExpandedChanged;

    public event EventHandler? StyleChanged;

    public XsrUiTree Tree { get; }

    /// <summary>
    /// The optional host-store observer bound to this shell's render tree. A native backend uses
    /// its render request signal only to schedule a frame; the renderer remains the sole drain
    /// point.
    /// </summary>
    public XsrUiStateBridge? StateBridge { get; }

    public XsrUiStage Stage { get; }

    public XsrUiRenderer Renderer => Stage.Renderer;

    public XsrUiEntityId Root { get; }

    public XsrUiEntityId TitleBar { get; }

    public XsrUiEntityId Body { get; }

    public XsrUiEntityId Navigation { get; }

    public XsrUiEntityId Content { get; }

    /// <summary>The shell-owned rail expand/collapse affordance.</summary>
    public XsrUiEntityId NavigationToggle => _navigationToggle;

    public IReadOnlyList<XsrUiShellNavigationItem> NavigationItems { get; }

    public string Title { get; }

    public string Version { get; }

    public IReadOnlyDictionary<XsrSemanticId, XsrUiEntityId> NavigationEntities => _navigationEntities;

    public XsrSemanticId SelectedNavigationId { get; private set; }

    public XsrUiShellStyle Style { get; private set; }

    public XsrUiShellPalette Palette { get; private set; }

    /// <summary>Whether the navigation rail currently shows item labels beside the icons.</summary>
    public bool IsNavigationExpanded { get; private set; }

    private double RailWidth => IsNavigationExpanded ? ExpandedRailWidth : CollapsedRailWidth;

    /// <summary>
    /// Changes the presentation palette while preserving the semantic tree and current route.
    /// </summary>
    public void SetStyle(XsrUiShellStyle style)
    {
        if (Style == style)
        {
            return;
        }

        Style = style;
        Palette = XsrUiShellPalette.For(style);
        ApplyPalette();
        StyleChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Selects one primary navigation destination. Returns false for an unknown ID.
    /// </summary>
    public bool Select(XsrSemanticId id)
    {
        if (!_navigationEntities.TryGetValue(id, out XsrUiEntityId entity))
        {
            return false;
        }

        XsrSemanticId previous = SelectedNavigationId;
        if (previous == id)
        {
            return true;
        }

        if (_navigationEntities.TryGetValue(previous, out XsrUiEntityId previousEntity))
        {
            SetSelection(previousEntity, selected: false);
        }

        SetSelection(entity, selected: true);
        SelectedNavigationId = id;
        NavigationChanged?.Invoke(
            this,
            new XsrUiShellNavigationChangedEventArgs(previous, id));
        return true;
    }

    /// <summary>
    /// Selects a navigation destination by its renderer entity handle.
    /// </summary>
    public bool Select(XsrUiEntityId entity) =>
        _navigationIds.TryGetValue(entity, out XsrSemanticId id) && Select(id);

    /// <summary>
    /// Expands or collapses the navigation rail. Expansion is ephemeral presentation mechanics
    /// owned by the shell; it never becomes product state.
    /// </summary>
    public void SetNavigationExpanded(bool expanded)
    {
        if (IsNavigationExpanded == expanded)
        {
            return;
        }

        IsNavigationExpanded = expanded;
        ApplyNavigationExpansion();
        NavigationExpandedChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Flips the navigation rail between its icon rail and expanded forms.</summary>
    public void ToggleNavigationExpanded() => SetNavigationExpanded(!IsNavigationExpanded);

    /// <summary>
    /// Runs the deterministic UI.Next layout pass for one viewport.
    /// </summary>
    public XsrUiScene Render(XsrUiSize viewport)
    {
        Renderer.Viewport = viewport;
        return Renderer.Render();
    }

    /// <summary>
    /// Shared composition tail for both construction paths: the shell-owned rail toggle, the
    /// palette, and the canonical collapsed rail layout.
    /// </summary>
    private void FinishComposition()
    {
        _navigationToggle = CreateNavigationToggle();
        ApplyPalette();
        ApplyNavigationExpansion();
    }

    private XsrUiEntityId CreateNavigationToggle()
    {
        XsrUiEntityId toggle = Tree.Create("navigation-toggle");
        Tree.SetComponent(toggle, new XsrUiElement
        {
            Height = NavigationItemHeight,
            Margin = new XsrUiThickness(0, 6, 0, RailBottomInset),
            HorizontalAlignment = XsrUiAlignment.Stretch,
            VerticalAlignment = XsrUiAlignment.Center,
        });
        Tree.SetComponent(toggle, new XsrUiImage("lucide/menu"));
        Tree.SetComponent(toggle, new XsrUiSemantic(XsrUiSemanticRole.Button, "导航开关"));
        Tree.SetComponent(toggle, new XsrUiInput { Focusable = true, Clickable = true });
        Tree.SetComponent(toggle, new XsrUiCommandBinding(XsrUiShellIds.NavigationExpand));
        Tree.SetComponent(toggle, new XsrUiVisualStyle());
        Tree.Attach(toggle, Navigation);
        return toggle;
    }

    /// <summary>Insets the first rail item from the title bar, mirroring the legacy rail.</summary>
    public const double RailTopInset = 10;

    /// <summary>Insets the rail toggle from the bottom edge.</summary>
    public const double RailBottomInset = 12;

    private static XsrUiThickness ItemMargin(int index, bool expanded)
    {
        double horizontal = expanded ? 8 : 0;
        double top = index == 0 ? RailTopInset : 0;
        return new XsrUiThickness(horizontal, top, horizontal, 0);
    }

    private void ApplyNavigationExpansion()
    {
        XsrUiElement? rail = Tree.GetComponent<XsrUiElement>(Navigation);
        if (rail is not null && rail.Width != RailWidth)
        {
            rail.Width = RailWidth;
            Tree.MarkDirty(Navigation, XsrUiDirtyKinds.Layout);
        }

        // Item text stays the destination label; whether the label is drawn next to the icon is
        // a presentation decision the backend derives from the committed item width.
        for (int index = 0; index < NavigationItems.Count; index++)
        {
            XsrUiEntityId entity = _navigationEntities[NavigationItems[index].Id];
            XsrUiElement? element = Tree.GetComponent<XsrUiElement>(entity);
            XsrUiThickness margin = ItemMargin(index, IsNavigationExpanded);
            if (element is not null && element.Margin != margin)
            {
                element.Margin = margin;
                Tree.MarkDirty(entity, XsrUiDirtyKinds.Layout);
            }
        }
    }

    private void ApplyPalette()
    {
        ApplyVisual(
            Root,
            Palette.WindowBackground,
            Palette.PrimaryText,
            XsrUiColor.Transparent,
            XsrUiColor.Transparent,
            XsrUiSurfaceKind.Solid,
            cornerRadius: 0,
            blurRadius: 0,
            borderWidth: 0);
        ApplyVisual(
            TitleBar,
            Palette.TitleBarBackground,
            Palette.TitleBarText,
            Palette.SurfaceBorder,
            XsrUiColor.Transparent,
            Palette.TitleBarSurface,
            Palette.CornerRadius,
            Palette.BlurRadius,
            Palette.BorderWidth);
        ApplyVisual(
            Navigation,
            Palette.NavigationBackground,
            Palette.PrimaryText,
            Palette.SurfaceBorder,
            XsrUiColor.Transparent,
            Palette.NavigationSurface,
            Palette.CornerRadius,
            Palette.BlurRadius,
            Palette.BorderWidth);
        ApplyVisual(
            Content,
            Palette.ContentBackground,
            Palette.PrimaryText,
            XsrUiColor.Transparent,
            XsrUiColor.Transparent,
            Palette.ContentSurface,
            0,
            0,
            0);

        foreach (XsrUiShellNavigationItem item in NavigationItems)
        {
            ApplyItemVisual(_navigationEntities[item.Id], item.Id == SelectedNavigationId);
        }

        if (_navigationToggle != default)
        {
            ApplyVisual(
                _navigationToggle,
                XsrUiColor.Transparent,
                Palette.PrimaryText,
                XsrUiColor.Transparent,
                Palette.NavigationHover,
                XsrUiSurfaceKind.None,
                cornerRadius: 0,
                blurRadius: 0,
                borderWidth: 0);
        }
    }

    private void ApplyItemVisual(XsrUiEntityId entity, bool selected)
    {
        ApplyVisual(
            entity,
            XsrUiColor.Transparent,
            selected ? Palette.SelectedNavigationText : Palette.PrimaryText,
            selected ? Palette.Accent : XsrUiColor.Transparent,
            Palette.NavigationHover,
            XsrUiSurfaceKind.None,
            cornerRadius: 0,
            blurRadius: 0,
            borderWidth: 0);
    }

    private void SetSelection(XsrUiEntityId entity, bool selected)
    {
        XsrUiSelection selection = Tree.GetComponent<XsrUiSelection>(entity)
            ?? throw new InvalidOperationException("A shell navigation entity lost its selection component.");
        selection.IsSelected = selected;
        Tree.MarkDirty(entity, XsrUiDirtyKinds.Paint);
        ApplyItemVisual(entity, selected);
    }

    private void ApplyVisual(
        XsrUiEntityId entity,
        XsrUiColor background,
        XsrUiColor foreground,
        XsrUiColor border,
        XsrUiColor hover,
        XsrUiSurfaceKind surface,
        double cornerRadius,
        double blurRadius,
        double borderWidth)
    {
        XsrUiVisualStyle? visual = Tree.GetComponent<XsrUiVisualStyle>(entity);
        if (visual is null)
        {
            visual = new XsrUiVisualStyle();
            Tree.SetComponent(entity, visual);
        }
        visual.Background = background;
        visual.Foreground = foreground;
        visual.Border = border;
        visual.Hover = hover;
        visual.Surface = surface;
        // Background alpha expresses translucency; the element itself remains opaque so a
        // transparent navigation highlight does not make its label disappear.
        visual.Opacity = 1;
        visual.CornerRadius = cornerRadius;
        visual.BlurRadius = blurRadius;
        visual.BorderWidth = borderWidth;
        Tree.MarkDirty(entity, XsrUiDirtyKinds.Paint);
    }

    private sealed class ShellIntentSink(XsrUiShell owner) : IXsrUiIntentSink
    {
        public void Emit(XsrSemanticId command, XsrUiEntityId source, XsrCorrelationId correlationId)
        {
            _ = owner.Select(source);
            if (command == XsrUiShellIds.NavigationExpand)
            {
                owner.ToggleNavigationExpanded();
            }

            if (command == XsrUiShellIds.StyleToggle)
            {
                owner.SetStyle(
                    owner.Style == XsrUiShellStyle.Experimental
                        ? XsrUiShellStyle.LiquidGlass
                        : XsrUiShellStyle.Experimental);
            }

            owner._externalIntentSink?.Emit(command, source, correlationId);
        }
    }
}

/// <summary>
/// Convenience entry point for composition roots that want the default shell contract.
/// </summary>
public static class XsrUiShellComposer
{
    public static XsrUiShell Compose(
        XsrStateStore state,
        XsrUiShellOptions? options = null,
        IXsrUiIntentSink? intentSink = null,
        XsrUiStateBridge? stateBridge = null) =>
        new(state, options, intentSink, stateBridge);

    public static XsrUiShell Compose(
        XsrStateStore state,
        XsrUiShellTemplate template,
        XsrUiShellOptions? options = null,
        IXsrUiIntentSink? intentSink = null,
        XsrUiStateBridge? stateBridge = null) =>
        new(state, template, options, intentSink, stateBridge);
}
