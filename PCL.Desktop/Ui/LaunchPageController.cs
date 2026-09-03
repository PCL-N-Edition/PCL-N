using System.IO;
using System.Text.Json.Nodes;
using PCL.Pxml;
using PCL.Services.Accounts;
using PCL.Services.Composition;
using PCL.Services.Minecraft;
using PCL.Services.Minecraft.Launch;
using PCL.UI.Next;
using PCL.Xsr;
using PCL.Xsr.Runtime;
using PCL.Xsr.State;

namespace PCL.Desktop.Ui;

/// <summary>
/// The product launch page: the first vertical slice attached to the shell's content host,
/// replicating the legacy experimental launch home's information architecture — an account
/// card (账户 / 实验 badge / name / summary), a version card (版本 header, instance picker row,
/// the big accent launch button, status line), and the community about card. It reads its
/// facts from host state cells, emits the launch intent through the renderer's normal sink,
/// and dispatches the real Minecraft launch command through the composed runtime routers.
/// Navigation intents route between this page and placeholders for destinations whose slices
/// have not landed yet.
/// </summary>
internal sealed class LaunchPageController
{
    private static readonly XsrSemanticId LaunchRoute = XsrSemanticId.Parse("ui.navigation.launch");

    private static readonly XsrSemanticId LaunchStartCommand = XsrSemanticId.Parse("ui.launch.start");

    private const string NavigationCommandPrefix = "ui.navigation.";

    // The legacy experimental launch-home palette (light theme).
    private static readonly XsrUiColor CardBackground = new(255, 255, 255, 241);
    private static readonly XsrUiColor CardBorder = new(224, 234, 253);
    private static readonly XsrUiColor PickerBackground = new(238, 242, 247);
    private static readonly XsrUiColor PrimaryText = new(52, 61, 74);
    private static readonly XsrUiColor SecondaryText = new(122, 138, 153);
    private static readonly XsrUiColor BadgeBackground = new(224, 234, 253);
    private static readonly XsrUiColor BadgeText = new(11, 91, 203);
    private static readonly XsrUiColor LaunchButtonBackground = new(11, 91, 203);
    private static readonly XsrUiColor LaunchButtonHover = new(19, 112, 243);

    private const string NoAccountName = "未选择账户";
    private const string NoAccountSummary = "登录与档案管理在这里完成。";
    private const string AccountReadySummary = "账户已就绪，可以开始游戏。";
    private const string AccountNeedLoginSummary = "请选择或创建一个账户档案后再启动。";
    private const string ScanningInstances = "正在扫描本地版本…";
    private const string NoInstances = "未找到可启动的游戏版本";
    private const string SelectFromToolbar = "使用右上角按钮选择或安装版本";
    private const string InstanceSettingsAction = "版本设置";
    private const string DownloadLabel = "下载游戏";
    private const string LaunchLabel = "启动游戏";

    private readonly XsrUiShell _shell;
    private readonly DesktopUiIntentSink _intents;
    private readonly MinecraftRuntime _minecraft;
    private readonly XsrStateStore _store;
    private readonly string _minecraftRootDirectory;
    private readonly XsrUiEntityId _launchPage;
    private readonly XsrUiEntityId _placeholderPage;
    private readonly Dictionary<string, XsrUiEntityId> _pageEntities;
    private Task _refreshTask = Task.CompletedTask;
    private MinecraftInstanceDescriptor? _selectedInstance;

    public LaunchPageController(
        XsrUiShell shell,
        DesktopUiIntentSink intents,
        MinecraftRuntime minecraft,
        XsrStateStore store,
        string minecraftRootDirectory)
    {
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(intents);
        ArgumentNullException.ThrowIfNull(minecraft);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(minecraftRootDirectory);
        _shell = shell;
        _intents = intents;
        _minecraft = minecraft;
        _store = store;
        _minecraftRootDirectory = minecraftRootDirectory;
        (_launchPage, _pageEntities) = LoadLaunchPage();
        _placeholderPage = BuildPlaceholderPage();
    }

    /// <summary>Subscribes to renderer intents and shows the initial launch page.</summary>
    public void Attach()
    {
        _intents.IntentEmitted += OnIntentEmitted;
        ShowLaunch();
        Publish(LaunchPageStateComposition.StatusKey, "就绪");
        Publish(LaunchPageStateComposition.ProfileNameKey, NoAccountName);
        Publish(LaunchPageStateComposition.ProfileSummaryKey, NoAccountSummary);
        Publish(LaunchPageStateComposition.InstanceSummaryKey, ScanningInstances);
        Publish(LaunchPageStateComposition.InstanceDetailKey, SelectFromToolbar);
        PublishProfileSummary();
        _refreshTask = RefreshInstancesAsync();
    }

    /// <summary>Completes when the in-flight instance scan has published its facts.</summary>
    public Task WaitUntilIdle() => _refreshTask;

    /// <summary>
    /// Re-queries the installed instances and re-commits the version card facts. Exposed for
    /// tests so the asynchronous scan can be awaited deterministically.
    /// </summary>
    public async Task RefreshInstancesAsync()
    {
        if (!_minecraft.Queries.TryResolve(MinecraftRouteIds.InstancesRead, out XsrQueryId queryId))
        {
            return;
        }

        XsrResult<IReadOnlyList<MinecraftInstanceDescriptor>> result = await _minecraft.Queries
            .QueryAsync<MinecraftInstancesQuery, IReadOnlyList<MinecraftInstanceDescriptor>>(
                queryId,
                new MinecraftInstancesQuery(_minecraftRootDirectory))
            .ConfigureAwait(true);
        if (result.IsSuccess && result.Value is { Count: > 0 } instances)
        {
            _selectedInstance = instances[0];
            Publish(LaunchPageStateComposition.InstanceSummaryKey, instances[0].Id);
            Publish(LaunchPageStateComposition.InstanceDetailKey, InstanceSettingsAction);
            SetLaunchButtonLabel(LaunchLabel);
        }
        else
        {
            _selectedInstance = null;
            Publish(LaunchPageStateComposition.InstanceSummaryKey, NoInstances);
            Publish(LaunchPageStateComposition.InstanceDetailKey, SelectFromToolbar);
            SetLaunchButtonLabel(DownloadLabel);
        }
    }

    private void OnIntentEmitted(object? sender, DesktopUiIntentEventArgs e)
    {
        XsrSemanticId command = e.Intent.Command;
        if (command == LaunchRoute)
        {
            ShowLaunch();
            PublishProfileSummary();
            _refreshTask = RefreshInstancesAsync();
        }
        else if (command.Value.StartsWith(NavigationCommandPrefix, StringComparison.Ordinal))
        {
            ShowPlaceholder();
        }
        else if (command == LaunchStartCommand)
        {
            _ = StartLaunchAsync();
        }
    }

    private void ShowLaunch()
    {
        if (!_shell.Stage.Navigation.Current.Equals(_launchPage))
        {
            // Destination switches replace the page: the navigator's back stack is reserved
            // for hierarchical drill-in, not for moving between primary destinations.
            _shell.Stage.Navigation.Replace(_launchPage);
        }
    }

    private void ShowPlaceholder()
    {
        if (!_shell.Stage.Navigation.Current.Equals(_placeholderPage))
        {
            _shell.Stage.Navigation.Replace(_placeholderPage);
        }
    }

    private void PublishProfileSummary()
    {
        IReadOnlyList<LaunchProfileView> profiles = ReadProfiles();
        if (profiles.Count > 0)
        {
            Publish(LaunchPageStateComposition.ProfileNameKey, profiles[0].Username);
            Publish(LaunchPageStateComposition.ProfileSummaryKey, AccountReadySummary);
        }
        else
        {
            Publish(LaunchPageStateComposition.ProfileNameKey, NoAccountName);
            Publish(LaunchPageStateComposition.ProfileSummaryKey, NoAccountSummary);
        }
    }

    private async Task StartLaunchAsync()
    {
        if (_selectedInstance is null)
        {
            Publish(LaunchPageStateComposition.StatusKey, "未找到可启动的实例");
            return;
        }

        IReadOnlyList<LaunchProfileView> profiles = ReadProfiles();
        if (profiles.Count == 0)
        {
            Publish(LaunchPageStateComposition.StatusKey, AccountNeedLoginSummary);
            return;
        }

        Publish(LaunchPageStateComposition.StatusKey, "正在启动 Minecraft…");
        try
        {
            string versionJsonPath = Path.Combine(
                _selectedInstance.DirectoryPath,
                $"{_selectedInstance.VersionId}.json");
            JsonObject versionJson = JsonNode.Parse(await File.ReadAllTextAsync(versionJsonPath).ConfigureAwait(true)) as JsonObject
                ?? throw new InvalidDataException($"The version JSON '{versionJsonPath}' is not an object.");

            LaunchProfileView profile = profiles[0];
            MinecraftLaunchRequest request = new()
            {
                VersionJson = versionJson,
                VersionId = _selectedInstance.VersionId,
                InstanceDirectory = _selectedInstance.DirectoryPath,
                MinecraftRootDirectory = _minecraftRootDirectory,
                PlayerName = profile.Username,
                PlayerUuid = profile.Uuid,
                IdentityMode = MinecraftLaunchIdentityMode.Offline,
            };

            if (!_minecraft.Commands.TryResolve(MinecraftRouteIds.Launch, out XsrCommandId commandId))
            {
                Publish(LaunchPageStateComposition.StatusKey, "启动失败：启动命令未注册。");
                return;
            }

            XsrCommandDispatch dispatch = _minecraft.Commands.Dispatch(
                commandId,
                new MinecraftLaunchCommand(request));
            XsrResult result = await dispatch.Completion.ConfigureAwait(true);
            Publish(
                LaunchPageStateComposition.StatusKey,
                result.IsSuccess ? "Minecraft 已启动" : $"启动失败：{result.Error?.Message}");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Publish(LaunchPageStateComposition.StatusKey, $"启动失败：{exception.Message}");
        }
    }

    private IReadOnlyList<LaunchProfileView> ReadProfiles() =>
        _store.ReadCollection<LaunchProfileView>(_store.Resolve(AccountService.ProfilesKey)).Items;

    private void Publish(XsrSemanticId key, string value)
    {
        _store.Publish(_store.Resolve(key), value);
    }

    private void SetLaunchButtonLabel(string label)
    {
        if (_pageEntities.TryGetValue("LaunchButton", out XsrUiEntityId button)
            && _shell.Tree.GetComponent<XsrUiText>(button) is { } text
            && !string.Equals(text.Content, label, StringComparison.Ordinal))
        {
            text.Content = label;
            _shell.Tree.MarkDirty(button, XsrUiDirtyKinds.Layout | XsrUiDirtyKinds.Paint);
        }
    }

    private (XsrUiEntityId Page, Dictionary<string, XsrUiEntityId> Entities) LoadLaunchPage()
    {
        PxmlDocument document = PxmlParser.Parse(ReadEmbeddedResource("Ui.LaunchPage.pxml"));
        PxmlHostIr ir = PxmlCompiler.Compile(document);
        XsrUiEntityId host = _shell.Tree.Create("launch-page-host");
        XsrUiEntityId page = PxmlUiLoader.Load(ir, _shell.Tree, _store, host);
        _shell.Tree.Detach(page);
        _shell.Tree.Destroy(host);

        Dictionary<string, XsrUiEntityId> entities = [];
        _shell.Tree.Walk(
            page,
            entity =>
            {
                if (_shell.Tree.GetComponent<XsrUiSemantic>(entity)?.Label is { Length: > 0 } label)
                {
                    entities[label] = entity;
                }

                return true;
            });
        StyleLaunchPage(page, entities);
        return (page, entities);
    }

    /// <summary>
    /// Applies the legacy experimental launch-home styling that the PXML control vocabulary
    /// cannot express: card surfaces, section typography, the badge, the picker row, and the
    /// accent launch button. Labels name the roles; facts stay in state cells.
    /// </summary>
    private void StyleLaunchPage(XsrUiEntityId page, Dictionary<string, XsrUiEntityId> entities)
    {
        StyleCard(entities, "CardAccount", cornerRadius: 16);
        StyleCard(entities, "CardVersion", cornerRadius: 16);
        StyleCard(entities, "CardAbout", cornerRadius: 18);
        StyleText(entities, "AccountHeader", SecondaryText, fontSize: 12, weight: 600);
        StyleText(entities, "VersionHeader", SecondaryText, fontSize: 12, weight: 600);
        StyleText(entities, "AboutTitle", SecondaryText, fontSize: 12, weight: 600);
        StyleText(entities, "AboutMessage", PrimaryText, fontSize: 14, weight: 600);
        StyleText(entities, "AccountSummary", SecondaryText, fontSize: 13);
        StyleText(entities, "LaunchStatus", SecondaryText, fontSize: 13);
        StyleText(entities, "VersionAction", SecondaryText, fontSize: 11);
        if (entities.TryGetValue("AccountBadge", out XsrUiEntityId badge))
        {
            ApplyVisual(badge, BadgeBackground, BadgeText, cornerRadius: 6);
            StyleText(entities, "AccountBadgeText", BadgeText, fontSize: 10, weight: 600);
        }

        if (entities.TryGetValue("AccountName", out XsrUiEntityId accountName))
        {
            StyleText(accountName, PrimaryText, fontSize: 16, weight: 600);
        }

        if (entities.TryGetValue("VersionName", out XsrUiEntityId versionName))
        {
            StyleText(versionName, PrimaryText, fontSize: 16, weight: 600);
        }

        if (entities.TryGetValue("InstanceRow", out XsrUiEntityId pickerRow))
        {
            ApplyVisual(pickerRow, PickerBackground, PrimaryText, cornerRadius: 12);
        }

        if (entities.TryGetValue("InstanceListButton", out XsrUiEntityId instanceListButton))
        {
            ApplyVisual(
                instanceListButton,
                XsrUiColor.Transparent,
                SecondaryText,
                cornerRadius: 14,
                hover: BadgeBackground);
            StyleText(instanceListButton, SecondaryText, fontSize: 16, weight: 600);
            AlignText(instanceListButton, XsrUiTextAlignment.Center);
        }

        if (entities.TryGetValue("InstanceChevron", out XsrUiEntityId instanceChevron))
        {
            ApplyVisual(
                instanceChevron,
                CardBackground,
                SecondaryText,
                cornerRadius: 13,
                border: CardBorder);
            StyleText(entities, "InstanceChevronText", SecondaryText, fontSize: 18);
        }

        if (entities.TryGetValue("LaunchButton", out XsrUiEntityId button))
        {
            // Legacy accent button: normal #0b5bcb, hover #1370f3, white 13 px semibold label.
            ApplyVisual(
                button,
                LaunchButtonBackground,
                new XsrUiColor(255, 255, 255),
                cornerRadius: 11,
                hover: LaunchButtonHover);
            StyleText(button, new XsrUiColor(255, 255, 255), fontSize: 13, weight: 600);
            AlignText(button, XsrUiTextAlignment.Center);
        }
    }

    private void StyleCard(
        Dictionary<string, XsrUiEntityId> entities,
        string label,
        double cornerRadius)
    {
        if (entities.TryGetValue(label, out XsrUiEntityId card))
        {
            ApplyVisual(card, CardBackground, PrimaryText, cornerRadius, border: CardBorder);
        }
    }

    private void StyleText(Dictionary<string, XsrUiEntityId> entities, string label, XsrUiColor foreground, double fontSize, double weight = 400)
    {
        if (entities.TryGetValue(label, out XsrUiEntityId entity))
        {
            StyleText(entity, foreground, fontSize, weight);
        }
    }

    private void StyleText(XsrUiEntityId entity, XsrUiColor foreground, double fontSize, double weight = 400)
    {
        XsrUiVisualStyle visual = RequireVisual(entity);
        visual.Foreground = foreground;
        visual.FontSize = fontSize;
        visual.FontWeight = weight;
        _shell.Tree.MarkDirty(entity, XsrUiDirtyKinds.Paint | XsrUiDirtyKinds.Layout);
    }

    private void AlignText(XsrUiEntityId entity, XsrUiTextAlignment alignment)
    {
        RequireVisual(entity).TextAlignment = alignment;
        _shell.Tree.MarkDirty(entity, XsrUiDirtyKinds.Paint);
    }

    private void ApplyVisual(
        XsrUiEntityId entity,
        XsrUiColor background,
        XsrUiColor foreground,
        double cornerRadius,
        XsrUiColor? border = null,
        XsrUiColor? hover = null)
    {
        XsrUiVisualStyle visual = RequireVisual(entity);
        visual.Background = background;
        visual.Foreground = foreground;
        visual.Border = border ?? XsrUiColor.Transparent;
        visual.BorderWidth = border is null ? 0 : 1;
        visual.Hover = hover ?? XsrUiColor.Transparent;
        visual.Surface = XsrUiSurfaceKind.Solid;
        visual.CornerRadius = cornerRadius;
        _shell.Tree.MarkDirty(entity, XsrUiDirtyKinds.Paint);
    }

    private XsrUiVisualStyle RequireVisual(XsrUiEntityId entity)
    {
        XsrUiVisualStyle? visual = _shell.Tree.GetComponent<XsrUiVisualStyle>(entity);
        if (visual is null)
        {
            visual = new XsrUiVisualStyle();
            _shell.Tree.SetComponent(entity, visual);
        }

        return visual;
    }

    private XsrUiEntityId BuildPlaceholderPage()
    {
        XsrUiTree tree = _shell.Tree;
        XsrUiEntityId page = tree.Create("placeholder-page");
        tree.SetComponent(page, new XsrUiStackPanel(XsrUiOrientation.Vertical) { Spacing = 12 });
        tree.SetComponent(page, new XsrUiSemantic(XsrUiSemanticRole.Page, "建设中"));
        XsrUiEntityId text = tree.Create("placeholder-text");
        tree.SetComponent(text, new XsrUiText("该分区将在后续单元中迁移。"));
        tree.SetComponent(text, new XsrUiSemantic(XsrUiSemanticRole.Text, "建设中"));
        tree.Attach(text, page);
        tree.MarkDirty(page, XsrUiDirtyKinds.Structure);
        return page;
    }

    private static string ReadEmbeddedResource(string suffix)
    {
        System.Reflection.Assembly assembly = typeof(LaunchPageController).Assembly;
        string resourceName = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith(suffix, StringComparison.Ordinal));
        using Stream stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"The embedded resource '{resourceName}' is missing.");
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }
}
