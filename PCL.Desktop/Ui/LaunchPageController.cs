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
/// The product launch page: the first vertical slice attached to the shell's content host. It
/// reads launch summaries and status from host state cells, emits the launch intent through the
/// renderer's normal sink, and dispatches the real Minecraft launch command through the
/// composed runtime routers. Navigation intents route between this page and placeholders for
/// destinations whose slices have not landed yet.
/// </summary>
internal sealed class LaunchPageController
{
    private static readonly XsrSemanticId LaunchRoute = XsrSemanticId.Parse("ui.navigation.launch");

    private static readonly XsrSemanticId LaunchStartCommand = XsrSemanticId.Parse("ui.launch.start");

    private const string NavigationCommandPrefix = "ui.navigation.";

    private readonly XsrUiShell _shell;
    private readonly DesktopUiIntentSink _intents;
    private readonly MinecraftRuntime _minecraft;
    private readonly XsrStateStore _store;
    private readonly string _minecraftRootDirectory;
    private readonly XsrUiEntityId _launchPage;
    private readonly XsrUiEntityId _placeholderPage;
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
        _launchPage = LoadLaunchPage();
        _placeholderPage = BuildPlaceholderPage();
    }

    /// <summary>Subscribes to renderer intents and shows the initial launch page.</summary>
    public void Attach()
    {
        _intents.IntentEmitted += OnIntentEmitted;
        ShowLaunch();
        Publish(LaunchPageStateComposition.StatusKey, "就绪");
        Publish(LaunchPageStateComposition.InstanceSummaryKey, "正在扫描实例…");
        PublishProfileSummary();
        _ = RefreshInstancesAsync();
    }

    private void OnIntentEmitted(object? sender, DesktopUiIntentEventArgs e)
    {
        XsrSemanticId command = e.Intent.Command;
        if (command == LaunchRoute)
        {
            ShowLaunch();
            PublishProfileSummary();
            _ = RefreshInstancesAsync();
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
        IReadOnlyList<LaunchProfileView> profiles = _store
            .ReadCollection<LaunchProfileView>(_store.Resolve(AccountService.ProfilesKey))
            .Items;
        LaunchProfileView profile = profiles.Count > 0 ? profiles[0] : default;
        string summary = profiles.Count > 0
            ? $"{profile.Username}（{profile.Info}）"
            : "未选择账户";
        Publish(LaunchPageStateComposition.ProfileSummaryKey, summary);
    }

    private async Task RefreshInstancesAsync()
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
            Publish(
                LaunchPageStateComposition.InstanceSummaryKey,
                $"{instances[0].Metadata.Description}（{instances[0].VersionId}）");
        }
        else
        {
            _selectedInstance = null;
            Publish(LaunchPageStateComposition.InstanceSummaryKey, "未找到实例");
        }
    }

    private async Task StartLaunchAsync()
    {
        if (_selectedInstance is null)
        {
            Publish(LaunchPageStateComposition.StatusKey, "未找到可启动的实例");
            return;
        }

        Publish(LaunchPageStateComposition.StatusKey, "正在启动…");
        try
        {
            string versionJsonPath = Path.Combine(
                _selectedInstance.DirectoryPath,
                $"{_selectedInstance.VersionId}.json");
            JsonObject versionJson = JsonNode.Parse(await File.ReadAllTextAsync(versionJsonPath).ConfigureAwait(true)) as JsonObject
                ?? throw new InvalidDataException($"The version JSON '{versionJsonPath}' is not an object.");

            (string playerName, string playerUuid) = ResolveIdentity();
            MinecraftLaunchRequest request = new()
            {
                VersionJson = versionJson,
                VersionId = _selectedInstance.VersionId,
                InstanceDirectory = _selectedInstance.DirectoryPath,
                MinecraftRootDirectory = _minecraftRootDirectory,
                PlayerName = playerName,
                PlayerUuid = playerUuid,
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
                result.IsSuccess ? "已启动" : $"启动失败：{result.Error?.Message}");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Publish(LaunchPageStateComposition.StatusKey, $"启动失败：{exception.Message}");
        }
    }

    // CA5351: the vanilla offline identifier is specified as an MD5-based v3 UUID; the hash
    // derives a player identifier and protects nothing.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5351:Do Not Use Broken Cryptographic Algorithms", Justification = "Minecraft offline UUID v3 derivation")]
    private (string Name, string Uuid) ResolveIdentity()
    {
        IReadOnlyList<LaunchProfileView> profiles = _store
            .ReadCollection<LaunchProfileView>(_store.Resolve(AccountService.ProfilesKey))
            .Items;
        if (profiles.Count > 0)
        {
            LaunchProfileView profile = profiles[0];
            return (profile.Username, profile.Uuid);
        }

        // Offline identity fallback: the vanilla offline UUID is a v3 (MD5) UUID over
        // "OfflinePlayer:" + name.
        string name = "Player";
        // The vanilla offline identifier is a v3 (MD5) UUID by specification; this is an
        // identifier derivation, not a cryptographic primitive.
        byte[] hash = System.Security.Cryptography.MD5.HashData(
            System.Text.Encoding.UTF8.GetBytes($"OfflinePlayer:{name}"));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x30);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return (name, new Guid(hash).ToString("N"));
    }

    private void Publish(XsrSemanticId key, string value)
    {
        _store.Publish(_store.Resolve(key), value);
    }

    private XsrUiEntityId LoadLaunchPage()
    {
        PxmlDocument document = PxmlParser.Parse(ReadEmbeddedResource("Ui.LaunchPage.pxml"));
        PxmlHostIr ir = PxmlCompiler.Compile(document);
        XsrUiEntityId host = _shell.Tree.Create("launch-page-host");
        XsrUiEntityId page = PxmlUiLoader.Load(ir, _shell.Tree, _store, host);
        _shell.Tree.Detach(page);
        _shell.Tree.Destroy(host);
        return page;
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
