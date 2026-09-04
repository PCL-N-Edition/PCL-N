using PCL.Desktop.Ui;
using PCL.Services.Accounts;
using PCL.Services.Composition;
using PCL.Services.Foundation;
using PCL.Services.Minecraft;
using PCL.Services.Minecraft.Process;
using PCL.UI.Next;
using PCL.Xsr;
using PCL.Xsr.Runtime;
using PCL.Xsr.State;

namespace PCL.Desktop.Tests;

internal static partial class Program
{
    public static void Main()
    {
        foreach ((string name, Action body) in TestCases)
        {
            body();
            Console.WriteLine($"PASS: {name}");
        }

        Console.WriteLine($"Desktop composition tests passed: {TestCases.Length}.");
    }

    private static readonly (string Name, Action Body)[] TestCases =
    [
        ("launch page replicates the legacy card layout with bound facts", LaunchPageReplicatesLegacyLayout),
        ("launch page matches legacy geometry across wide, default, and minimum windows", LaunchPageMatchesLegacyGeometry),
        ("navigation intents route between launch and placeholder pages", NavigationIntentsRouteBetweenPages),
        ("download and instance actions route to version management", DownloadAndInstanceActionsRouteToVersionManagement),
        ("launch page semantics never expose internal entity keys", LaunchPageSemanticsNeverExposeInternalKeys),
        ("instance scan publishes state without mutating the tree from its worker", InstanceScanPublishesWithoutForeignTreeMutation),
        ("an older instance scan cannot overwrite the latest generation", OlderInstanceScanCannotOverwriteLatestGeneration),
        ("launch primary dispatches the product start command", LaunchPrimaryDispatchesProductStartCommand),
        ("expand navigation never replaces the launch page", ExpandNavigationNeverReplacesTheLaunchPage),
        ("account card lists profiles and switches the selection", AccountCardListsProfilesAndSwitchesSelection),
    ];

    private static void LaunchPageReplicatesLegacyLayout()
    {
        using LaunchPageFixture fixture = new(new ImmediateInstanceSource([]));
        fixture.Controller.WaitUntilIdle().GetAwaiter().GetResult();
        XsrUiScene scene = fixture.Shell.Render(new XsrUiSize(1280, 800));

        AssertEqual("下载游戏", FindByKey(fixture.Shell, scene, "LaunchButton").Text);
        AssertEqual("账户", FindByKey(fixture.Shell, scene, "AccountHeader").Text);
        AssertEqual("实验", FindByKey(fixture.Shell, scene, "AccountBadgeText").Text);
        AssertEqual("版本", FindByKey(fixture.Shell, scene, "VersionHeader").Text);
        AssertEqual("关于 PCL N Edition", FindByKey(fixture.Shell, scene, "AboutTitle").Text);

        AssertEqual(
            ReadCell(fixture.Store, LaunchPageState.ProfileNameKey),
            FindByKey(fixture.Shell, scene, "AccountName").Text);
        AssertEqual(
            ReadCell(fixture.Store, LaunchPageState.InstanceSummaryKey),
            FindByKey(fixture.Shell, scene, "VersionName").Text);

        XsrUiSceneNode header = FindByKey(fixture.Shell, scene, "AccountHeader");
        AssertEqual(12, header.VisualStyle.FontSize);
        AssertEqual(600, header.VisualStyle.FontWeight);
        XsrUiSceneNode versionName = FindByKey(fixture.Shell, scene, "VersionName");
        AssertEqual(16, versionName.VisualStyle.FontSize);
        AssertEqual(600, versionName.VisualStyle.FontWeight);
        AssertEqual(
            XsrUiTextAlignment.Center,
            FindByKey(fixture.Shell, scene, "LaunchButton").VisualStyle.TextAlignment);

        AssertEqual("未找到可启动的游戏版本", ReadCell(fixture.Store, LaunchPageState.InstanceSummaryKey));
        AssertEqual("使用右上角按钮选择或安装版本", ReadCell(fixture.Store, LaunchPageState.InstanceDetailKey));
        AssertEqual("下载游戏", ReadCell(fixture.Store, LaunchPageState.ActionLabelKey));
        AssertEqual(string.Empty, ReadCell(fixture.Store, LaunchPageState.SelectedInstanceKey));
        AssertEqual("就绪", ReadCell(fixture.Store, LaunchPageState.StatusKey));
    }

    private static void LaunchPageMatchesLegacyGeometry()
    {
        using LaunchPageFixture fixture = new(new ImmediateInstanceSource([]));
        fixture.Controller.WaitUntilIdle().GetAwaiter().GetResult();

        AssertLegacyGeometry(fixture.Shell, fixture.Shell.Render(new XsrUiSize(1280, 800)), contentWidth: 1176, contentHeight: 700);
        AssertLegacyGeometry(fixture.Shell, fixture.Shell.Render(new XsrUiSize(850, 500)), contentWidth: 746, contentHeight: 400);
        AssertLegacyGeometry(fixture.Shell, fixture.Shell.Render(new XsrUiSize(810, 470)), contentWidth: 706, contentHeight: 370);
    }

    private static void AssertLegacyGeometry(
        XsrUiShell shell,
        XsrUiScene scene,
        double contentWidth,
        double contentHeight)
    {
        const double contentX = 76;
        const double contentY = 76;
        const double columnGap = 16;
        const double rightCardGap = 12;
        const double versionCardHeight = 176;
        double distributableWidth = contentWidth - columnGap;
        double expectedAccountWidth = Math.Min(360, distributableWidth * 0.92 / (0.92 + 1.35));
        double expectedRightWidth = distributableWidth - expectedAccountWidth;

        XsrUiRect account = FindByKey(shell, scene, "CardAccount").Rect;
        XsrUiRect version = FindByKey(shell, scene, "CardVersion").Rect;
        XsrUiRect about = FindByKey(shell, scene, "CardAbout").Rect;
        XsrUiRect accountBadge = FindByKey(shell, scene, "AccountBadge").Rect;
        XsrUiRect accountHeader = FindByKey(shell, scene, "AccountHeaderRow").Rect;
        XsrUiRect accountContent = FindByKey(shell, scene, "AccountContent").Rect;
        XsrUiRect accountSummary = FindByKey(shell, scene, "AccountSummary").Rect;

        AssertRectClose(new XsrUiRect(contentX, contentY, expectedAccountWidth, contentHeight), account);
        AssertRectClose(
            new XsrUiRect(contentX + expectedAccountWidth + columnGap, contentY, expectedRightWidth, versionCardHeight),
            version);
        AssertRectClose(
            new XsrUiRect(version.X, contentY + versionCardHeight + rightCardGap, expectedRightWidth, contentHeight - versionCardHeight - rightCardGap),
            about);
        AssertClose(accountContent.X + accountContent.Width, accountBadge.X + accountBadge.Width);
        AssertClose(16, accountHeader.Height);
        AssertClose(18, accountSummary.Height);
        AssertClose(accountContent.Y + accountContent.Height, accountSummary.Y + accountSummary.Height);
        AssertTrue(version.Y + version.Height <= about.Y);
    }

    private static void NavigationIntentsRouteBetweenPages()
    {
        using LaunchPageFixture fixture = new(new ImmediateInstanceSource([]));
        fixture.Controller.WaitUntilIdle().GetAwaiter().GetResult();

        Emit(fixture.Intents, "ui.navigation.settings");
        XsrUiScene placeholder = fixture.Shell.Render(new XsrUiSize(1280, 800));
        AssertTrue(placeholder.Nodes.Any(node => node.Text == "该分区将在后续单元中迁移。"));
        AssertFalse(HasKey(fixture.Shell, placeholder, "LaunchButton"));

        Emit(fixture.Intents, "ui.navigation.launch");
        fixture.Controller.WaitUntilIdle().GetAwaiter().GetResult();
        XsrUiScene launch = fixture.Shell.Render(new XsrUiSize(1280, 800));
        AssertTrue(HasKey(fixture.Shell, launch, "LaunchButton"));
        AssertFalse(launch.Nodes.Any(node => node.Text == "该分区将在后续单元中迁移。"));
    }

    private static void DownloadAndInstanceActionsRouteToVersionManagement()
    {
        using LaunchPageFixture fixture = new(new ImmediateInstanceSource([]));
        fixture.Controller.WaitUntilIdle().GetAwaiter().GetResult();

        Emit(fixture.Intents, "ui.launch.primary");
        AssertEqual(XsrSemanticId.Parse("navigation.download"), fixture.Shell.SelectedNavigationId);
        AssertTrue(fixture.Shell.Render(new XsrUiSize(1280, 800)).Nodes.Any(
            node => node.Text == "该分区将在后续单元中迁移。"));
        AssertEqual("请在安装页选择或下载游戏版本", ReadCell(fixture.Store, LaunchPageState.StatusKey));

        Emit(fixture.Intents, "ui.navigation.launch");
        fixture.Controller.WaitUntilIdle().GetAwaiter().GetResult();
        Emit(fixture.Intents, "ui.launch.instances");
        AssertEqual(XsrSemanticId.Parse("navigation.download"), fixture.Shell.SelectedNavigationId);
        AssertTrue(fixture.Shell.Render(new XsrUiSize(1280, 800)).Nodes.Any(
            node => node.Text == "该分区将在后续单元中迁移。"));
    }

    private static void LaunchPageSemanticsNeverExposeInternalKeys()
    {
        using LaunchPageFixture fixture = new(new ImmediateInstanceSource([]));
        fixture.Controller.WaitUntilIdle().GetAwaiter().GetResult();
        XsrUiScene scene = fixture.Shell.Render(new XsrUiSize(1280, 800));
        XsrUiSceneNode button = FindByKey(fixture.Shell, scene, "LaunchButton");

        AssertEqual("下载游戏", button.Text);
        AssertEqual("下载游戏", button.Label);
        AssertEqual("版本列表", FindByKey(fixture.Shell, scene, "InstanceListButton").Label);
        AssertFalse(scene.Nodes.Any(node => node.Label is "LaunchButton" or "InstanceListButton" or "VersionName" or "CardAccount"));
    }

    private static void InstanceScanPublishesWithoutForeignTreeMutation()
    {
        ControllableInstanceSource source = new();
        using LaunchPageFixture fixture = new(source);
        _ = fixture.Shell.Render(new XsrUiSize(1280, 800));
        XsrUiEntityId[] dirtyBeforeWorker = [.. fixture.Shell.Tree.DirtyEntities()];

        Task.Run(() => source.Complete(0, [Instance("worker-result")])).GetAwaiter().GetResult();
        fixture.Controller.WaitUntilIdle().GetAwaiter().GetResult();
        AssertTrue(dirtyBeforeWorker.SequenceEqual(fixture.Shell.Tree.DirtyEntities()));
        AssertEqual("worker-result", ReadCell(fixture.Store, LaunchPageState.SelectedInstanceKey));

        XsrUiScene scene = fixture.Shell.Render(new XsrUiSize(1280, 800));
        AssertEqual("启动游戏", FindByKey(fixture.Shell, scene, "LaunchButton").Text);
        AssertEqual("启动游戏", FindByKey(fixture.Shell, scene, "LaunchButton").Label);
    }

    private static void OlderInstanceScanCannotOverwriteLatestGeneration()
    {
        ControllableInstanceSource source = new();
        using LaunchPageFixture fixture = new(source);
        Task latest = fixture.Controller.RefreshInstancesAsync();
        AssertEqual(2, source.Count);
        AssertTrue(source.TokenAt(0).IsCancellationRequested);

        source.Complete(1, [Instance("new-result")]);
        latest.GetAwaiter().GetResult();
        source.Complete(0, [Instance("old-result")]);
        AssertTrue(SpinWait.SpinUntil(() => source.ReturnedCount == 2, TimeSpan.FromSeconds(2)));
        AssertEqual("new-result", ReadCell(fixture.Store, LaunchPageState.SelectedInstanceKey));
        AssertEqual("new-result", ReadCell(fixture.Store, LaunchPageState.InstanceSummaryKey));
    }

    private static void LaunchPrimaryDispatchesProductStartCommand()
    {
        RecordingStartRoute recording = new();
        using MinecraftRuntime runtime = CreateRecordingRuntime(recording);
        using LaunchPageFixture fixture = new(
            new ImmediateInstanceSource([Instance("playable")]),
            runtime,
            addProfile: true,
            ownsMinecraftRuntime: false);
        fixture.Controller.WaitUntilIdle().GetAwaiter().GetResult();

        Emit(fixture.Intents, "ui.launch.primary");
        AssertTrue(SpinWait.SpinUntil(() => recording.LastCommand is not null, TimeSpan.FromSeconds(2)));
        AssertEqual("playable", recording.LastCommand!.InstanceId);
        AssertEqual(0, recording.LastCommand.AccountIndex);
        AssertTrue(SpinWait.SpinUntil(
            () => ReadCell(fixture.Store, LaunchPageState.StatusKey) == "Minecraft 已启动",
            TimeSpan.FromSeconds(2)));
    }

    private static void ExpandNavigationNeverReplacesTheLaunchPage()
    {
        using LaunchPageFixture fixture = new(new ImmediateInstanceSource([]));
        fixture.Controller.WaitUntilIdle().GetAwaiter().GetResult();

        // The rail expand/collapse toggle is shell presentation, not a destination: it must
        // never route the content host to the placeholder page.
        Emit(fixture.Intents, "ui.navigation.expand");
        XsrUiScene scene = fixture.Shell.Render(new XsrUiSize(1280, 800));
        AssertTrue(HasKey(fixture.Shell, scene, "LaunchButton"));
        AssertFalse(scene.Nodes.Any(node => node.Text == "该分区将在后续单元中迁移。"));
    }

    private static void AccountCardListsProfilesAndSwitchesSelection()
    {
        using LaunchPageFixture fixture = new(new ImmediateInstanceSource([]), addProfile: true);
        AssertTrue(fixture.Service.AddProfile(new LaunchProfile
        {
            Username = "Second",
            Kind = LaunchProfileKind.Microsoft,
        }).IsSuccess);

        // Re-activating the page rebuilds the account rows from the roster.
        Emit(fixture.Intents, "ui.navigation.settings");
        Emit(fixture.Intents, "ui.navigation.launch");
        fixture.Controller.WaitUntilIdle().GetAwaiter().GetResult();

        XsrUiScene scene = fixture.Shell.Render(new XsrUiSize(1280, 800));
        AssertTrue(HasKey(fixture.Shell, scene, "account-row:0"));
        AssertTrue(HasKey(fixture.Shell, scene, "account-row:1"));

        // The first profile is selected by default; switching publishes the fact and updates
        // the account presentation.
        AssertEqual(0, fixture.Service.SelectedIndex);
        AssertEqual("Player", ReadCell(fixture.Store, LaunchPageState.ProfileNameKey));

        XsrUiEntityId secondRow = FindEntity(fixture.Shell, "account-row:1");
        AssertTrue(secondRow.IsAssigned);
        Emit(fixture.Intents, "ui.account.select", secondRow);
        AssertEqual(1, fixture.Service.SelectedIndex);
        AssertEqual(1, ReadCellInt(fixture.Store, AccountService.SelectedKey));
        AssertEqual("Second", ReadCell(fixture.Store, LaunchPageState.ProfileNameKey));
        AssertEqual("账户已就绪，可以开始游戏。", ReadCell(fixture.Store, LaunchPageState.ProfileSummaryKey));

        scene = fixture.Shell.Render(new XsrUiSize(1280, 800));
        AssertTrue(scene.Nodes.Single(node =>
            string.Equals(fixture.Shell.Tree.Name(node.Entity), "account-row:1", StringComparison.Ordinal)).IsSelected);
    }

    private static int ReadCellInt(XsrStateStore store, XsrSemanticId key) =>
        (int?)store.ReadAppliedValue(store.Resolve(key)) ?? -1;

    private static MinecraftRuntime CreateRecordingRuntime(RecordingStartRoute recording)
    {
        NoopDispatchObserver observer = new();
        XsrCommandRouterBuilder commands = new();
        commands.Register<MinecraftStartCommand>(MinecraftRouteIds.Start, recording.Handle);
        XsrQueryRouterBuilder queries = new();
        return new MinecraftRuntime(
            new MinecraftVersionDiscovery(),
            new MinecraftInstanceDiscovery(),
            new MinecraftProcessService(),
            commands.Build(observer),
            queries.Build(observer));
    }

    private static MinecraftInstanceDescriptor Instance(string id)
    {
        MinecraftVersionClassification classification = new(
            id,
            "release",
            MinecraftVersionCategory.Release,
            null);
        MinecraftVersionDescriptor version = new(
            id,
            Path.Combine(Path.GetTempPath(), id),
            Path.Combine(Path.GetTempPath(), id, id + ".json"),
            null,
            null,
            "example.Main",
            null,
            classification);
        return new MinecraftInstanceDescriptor(id, version.DirectoryPath, id, version, new MinecraftInstanceMetadata());
    }

    private static XsrUiSceneNode FindByKey(XsrUiShell shell, XsrUiScene scene, string key) =>
        scene.Nodes.Single(node => string.Equals(shell.Tree.Name(node.Entity), key, StringComparison.Ordinal));

    private static bool HasKey(XsrUiShell shell, XsrUiScene scene, string key) =>
        scene.Nodes.Any(node => string.Equals(shell.Tree.Name(node.Entity), key, StringComparison.Ordinal));

    private static void Emit(DesktopUiIntentSink intents, string command) =>
        intents.Emit(XsrSemanticId.Parse(command), default, XsrCorrelationId.Create());

    private static void Emit(
        DesktopUiIntentSink intents,
        string command,
        XsrUiEntityId source) =>
        intents.Emit(XsrSemanticId.Parse(command), source, XsrCorrelationId.Create());

    private static XsrUiEntityId FindEntity(XsrUiShell shell, string key)
    {
        XsrUiEntityId found = default;
        shell.Tree.Walk(
            shell.Stage.Root,
            entity =>
            {
                if (string.Equals(shell.Tree.Name(entity), key, StringComparison.Ordinal))
                {
                    found = entity;
                    return false;
                }

                return true;
            });
        return found;
    }

    private static string ReadCell(XsrStateStore store, XsrSemanticId key) =>
        (string?)store.ReadAppliedValue(store.Resolve(key)) ?? string.Empty;

    private sealed class LaunchPageFixture : IDisposable
    {
        private readonly string _temporaryDirectory;
        private readonly bool _ownsMinecraftRuntime;

        public LaunchPageFixture(
            ILaunchPageInstanceSource source,
            MinecraftRuntime? minecraft = null,
            bool addProfile = false,
            bool ownsMinecraftRuntime = true)
        {
            _temporaryDirectory = Path.Combine(
                Path.GetTempPath(),
                "nexa-desktop-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_temporaryDirectory);
            XsrUiRuntimeContext uiRuntime = new();
            XsrStateStoreBuilder builder = FoundationState.CreateBuilder();
            LaunchPageState.DeclareState(builder);
            Store = builder.Build(uiRuntime.StateBridge);
            Intents = new DesktopUiIntentSink();
            Shell = PxmlShellComposer.Compose(Store, uiRuntime, intentSink: Intents);
            Minecraft = minecraft ?? MinecraftRuntimeComposer.Compose();
            _ownsMinecraftRuntime = minecraft is null || ownsMinecraftRuntime;
            Service = new AccountService(
                Store,
                new LaunchProfileFilePort(Path.Combine(_temporaryDirectory, "profiles.json")));
            if (addProfile)
            {
                AssertTrue(Service.AddProfile(new LaunchProfile
                {
                    Username = "Player",
                    Kind = LaunchProfileKind.Offline,
                }).IsSuccess);
            }

            Controller = new LaunchPageController(
                Shell,
                Intents,
                Minecraft,
                Service,
                Store,
                Path.Combine(_temporaryDirectory, "minecraft"),
                source);
            Controller.Attach();
        }

        public XsrUiShell Shell { get; }
        public DesktopUiIntentSink Intents { get; }
        public XsrStateStore Store { get; }
        public MinecraftRuntime Minecraft { get; }
        public AccountService Service { get; }
        public LaunchPageController Controller { get; }

        public void Dispose()
        {
            Controller.Dispose();
            if (_ownsMinecraftRuntime)
            {
                Minecraft.Dispose();
            }

            Directory.Delete(_temporaryDirectory, recursive: true);
        }
    }

    private sealed class ImmediateInstanceSource(IReadOnlyList<MinecraftInstanceDescriptor> instances)
        : ILaunchPageInstanceSource
    {
        public ValueTask<XsrResult<IReadOnlyList<MinecraftInstanceDescriptor>>> ReadAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(XsrResult.Success(instances));
    }

    private sealed class ControllableInstanceSource : ILaunchPageInstanceSource
    {
        private readonly object _gate = new();
        private readonly List<TaskCompletionSource<XsrResult<IReadOnlyList<MinecraftInstanceDescriptor>>>> _requests = [];
        private readonly List<CancellationToken> _tokens = [];
        private int _returnedCount;

        public int Count { get { lock (_gate) return _requests.Count; } }
        public int ReturnedCount => Volatile.Read(ref _returnedCount);

        public async ValueTask<XsrResult<IReadOnlyList<MinecraftInstanceDescriptor>>> ReadAsync(
            CancellationToken cancellationToken)
        {
            TaskCompletionSource<XsrResult<IReadOnlyList<MinecraftInstanceDescriptor>>> request =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                _requests.Add(request);
                _tokens.Add(cancellationToken);
            }

            XsrResult<IReadOnlyList<MinecraftInstanceDescriptor>> result = await request.Task.ConfigureAwait(false);
            _ = Interlocked.Increment(ref _returnedCount);
            return result;
        }

        public CancellationToken TokenAt(int index)
        {
            lock (_gate)
            {
                return _tokens[index];
            }
        }

        public void Complete(int index, IReadOnlyList<MinecraftInstanceDescriptor> instances)
        {
            TaskCompletionSource<XsrResult<IReadOnlyList<MinecraftInstanceDescriptor>>> request;
            lock (_gate)
            {
                request = _requests[index];
            }

            request.SetResult(XsrResult.Success(instances));
        }
    }

    private sealed class RecordingStartRoute
    {
        public MinecraftStartCommand? LastCommand { get; private set; }

        public ValueTask<XsrResult> Handle(
            MinecraftStartCommand command,
            CancellationToken cancellationToken)
        {
            LastCommand = command;
            return ValueTask.FromResult(XsrResult.Success());
        }
    }

    private sealed class NoopDispatchObserver : IXsrDispatchObserver
    {
        public void OnCompleted(XsrDispatchObservation observation)
        {
        }
    }
}
