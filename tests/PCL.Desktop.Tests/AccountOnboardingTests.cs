using System.Net;
using System.Text;
using PCL.Desktop.Ui;
using PCL.Services.Accounts;
using PCL.UI.Next;
using PCL.Xsr;

namespace PCL.Desktop.Tests;

internal static partial class Program
{
    private static readonly XsrUiSize AccountTestSize = new(810, 470);

    private static void AccountFormsUseOneHeaderAndCreateOfflineProfiles()
    {
        foreach (XsrUiShellStyle style in Enum.GetValues<XsrUiShellStyle>())
        {
            using LaunchPageFixture fixture = new(new ImmediateInstanceSource([]));
            fixture.Shell.SetStyle(style);
            AccountClick(fixture, "AccountAdd");
            XsrUiScene scene = fixture.Shell.Render(AccountTestSize);
            AssertEqual("添加账户", FindByKey(fixture.Shell, scene, "AccountHeader").Text);
            AssertFalse(HasKey(fixture.Shell, scene, "FormTitle"));
            AssertFalse(HasKey(fixture.Shell, scene, "AccountPickerTitle"));
            AssertFalse(HasKey(fixture.Shell, scene, "AccountAdd"));
            AssertContains(FindByKey(fixture.Shell, scene, "CardAccount").Rect,
                FindByKey(fixture.Shell, scene, "ProviderImport").Rect);
            AccountClick(fixture, "ProviderOffline");
            scene = fixture.Shell.Render(AccountTestSize);
            AssertEqual("离线档案", FindByKey(fixture.Shell, scene, "AccountHeader").Text);
            AssertEqual(FindByKey(fixture.Shell, scene, "OfflineName").Entity, fixture.Shell.Renderer.Focused);
            AccountFill(fixture, "OfflineName", "New_Player");
            AccountClick(fixture, "FormSubmit");
            fixture.Onboarding.Service.WhenIdle.GetAwaiter().GetResult();
            scene = fixture.Shell.Render(AccountTestSize);
            AssertEqual("New_Player", FindByKey(fixture.Shell, scene, "AccountName").Text);
            AssertEqual("账户", FindByKey(fixture.Shell, scene, "AccountHeader").Text);
            AssertEqual(FindByKey(fixture.Shell, scene, "AccountSwitch").Entity, fixture.Shell.Renderer.Focused);
            AssertFalse(HasKey(fixture.Shell, scene, "AccountForm"));
            AssertFalse(fixture.Shell.Tree.GetComponent<XsrUiTextInput>(FindEntity(fixture.Shell, "OfflineName"))!.ReadDraft().Length > 0);
            AccountClick(fixture, "AccountAdd"); AccountClick(fixture, "ProviderOffline");
            AccountClick(fixture, "AccountBack");
            AssertTrue(HasKey(fixture.Shell, fixture.Shell.Render(AccountTestSize), "ProviderMicrosoft"));
            AccountClick(fixture, "AccountBack");
            AssertTrue(HasKey(fixture.Shell, fixture.Shell.Render(AccountTestSize), "AccountName"));
        }
    }

    private static void MicrosoftOnboardingUsesServiceAndDiscardsLateCancellation()
    {
        ControlledMicrosoft microsoft = new();
        AccountEffects effects = new();
        using LaunchPageFixture fixture = new(new ImmediateInstanceSource([]), microsoft: microsoft, accountEffects: effects);
        AccountClick(fixture, "AccountAdd"); AccountClick(fixture, "ProviderMicrosoft");
        AwaitAccountPhase(fixture, AccountLoginPhase.AwaitingAuthorization);
        XsrUiScene scene = fixture.Shell.Render(AccountTestSize);
        AssertEqual("PUBLIC-CODE", FindByKey(fixture.Shell, scene, "UserCode").Text);
        AssertSceneHides(fixture, "PRIVATE-DEVICE");
        AccountClick(fixture, "OpenAuthorization"); AccountClick(fixture, "CopyUserCode");
        AssertEqual("https://www.microsoft.com/link", effects.Opened?.AbsoluteUri);
        AssertEqual("PUBLIC-CODE", effects.Copied);
        var dirty = fixture.Shell.Tree.DirtyEntities().ToArray();
        microsoft.Completion.SetResult(new("OnlinePlayer", "ms-uuid", "PRIVATE-ACCESS", "PRIVATE-REFRESH", null, true));
        fixture.Onboarding.Service.WhenIdle.GetAwaiter().GetResult();
        AssertTrue(dirty.SequenceEqual(fixture.Shell.Tree.DirtyEntities()));
        scene = fixture.Shell.Render(AccountTestSize);
        AssertEqual("OnlinePlayer", FindByKey(fixture.Shell, scene, "AccountName").Text);
        AssertEqual("PRIVATE-ACCESS", fixture.Service.GetProfile(0).Value!.AccessToken);
        AssertSceneHides(fixture, "PRIVATE-ACCESS", "PRIVATE-REFRESH", "PRIVATE-DEVICE");

        microsoft.Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        AccountClick(fixture, "AccountAdd"); AccountClick(fixture, "ProviderMicrosoft");
        AwaitAccountPhase(fixture, AccountLoginPhase.AwaitingAuthorization);
        Task cancelled = fixture.Onboarding.Service.WhenIdle;
        AccountClick(fixture, "FormCancel");
        AssertEqual(AccountLoginPhase.Cancelled, AccountSnapshot(fixture).Phase);
        AccountClick(fixture, "AccountBack"); AccountClick(fixture, "ProviderOffline");
        AccountFill(fixture, "OfflineName", "NewerPlayer"); AccountClick(fixture, "FormSubmit");
        fixture.Onboarding.Service.WhenIdle.GetAwaiter().GetResult();
        microsoft.Completion.SetResult(new("LatePlayer", "late-uuid", "LATE-ACCESS", "LATE-REFRESH", null, true));
        cancelled.GetAwaiter().GetResult();
        AssertEqual(2, fixture.Service.GetViews().Count);
        AssertEqual("NewerPlayer", FindByKey(fixture.Shell, fixture.Shell.Render(AccountTestSize), "AccountName").Text);
        AssertFalse(fixture.Service.GetViews().Any(profile => profile.Username == "LatePlayer"));
    }

    private static void ThirdPartyOnboardingMasksPasswordsAndUsesConfiguredServer()
    {
        AccountHttp handler = new();
        handler.Serve("https://auth.example/api/yggdrasil/authserver/authenticate",
            """{"accessToken":"PRIVATE-YGG","clientToken":"CLIENT","selectedProfile":{"id":"uuid","name":"ThirdPlayer"}}""");
        using HttpClient client = new(handler);
        using LaunchPageFixture fixture = new(new ImmediateInstanceSource([]), accountHttp: client);
        AccountClick(fixture, "AccountAdd"); AccountClick(fixture, "ProviderThirdParty");
        AccountFill(fixture, "AuthServer", "https://auth.example");
        AccountFill(fixture, "AuthUsername", "fixture@example.org");
        AccountFill(fixture, "AuthPassword", "PRIVATE-PASSWORD");
        AssertSceneHides(fixture, "PRIVATE-PASSWORD");
        AssertTrue(fixture.Shell.Renderer.CopySelectedText() is null);
        AccountClick(fixture, "FormSubmit");
        AssertEqual(string.Empty, fixture.Shell.Tree.GetComponent<XsrUiTextInput>(FindEntity(fixture.Shell, "AuthPassword"))!.ReadDraft());
        fixture.Onboarding.Service.WhenIdle.GetAwaiter().GetResult();
        AssertEqual(AccountLoginPhase.Completed, AccountSnapshot(fixture).Phase);
        AssertEqual("ThirdPlayer", FindByKey(fixture.Shell, fixture.Shell.Render(AccountTestSize), "AccountName").Text);
        AssertTrue(handler.Bodies.Single().Contains("PRIVATE-PASSWORD", StringComparison.Ordinal));
        AssertSceneHides(fixture, "PRIVATE-PASSWORD", "PRIVATE-YGG");
    }

    private static void LittleSkinOnboardingChoosesCharacterAndKeepsTokenKindsSeparate()
    {
        AccountHttp handler = new();
        handler.Serve("https://open.littleskin.cn/oauth/device_code",
            """{"user_code":"LS-PUBLIC","device_code":"PRIVATE-DEVICE","verification_uri":"https://open.littleskin.cn/device","expires_in":300,"interval":1}""");
        handler.Serve("https://open.littleskin.cn/oauth/token",
            """{"access_token":"PRIVATE-OAUTH","refresh_token":"PRIVATE-REFRESH","expires_in":259200}""");
        handler.Serve("https://littleskin.cn/api/yggdrasil/sessionserver/session/minecraft/profile",
            """[{"id":"first","name":"First"},{"id":"second","name":"Second"}]""");
        handler.Serve("https://littleskin.cn/api/yggdrasil/authserver/oauth",
            """{"accessToken":"PRIVATE-MC","clientToken":"CT","selectedProfile":{"id":"second","name":"Second"}}""");
        using HttpClient client = new(handler);
        using LaunchPageFixture fixture = new(new ImmediateInstanceSource([]), accountHttp: client,
            accountOptions: new("fixture", new("fixture", "", new Uri("http://127.0.0.1:17342/callback"))));
        AccountClick(fixture, "AccountAdd"); AccountClick(fixture, "ProviderLittleSkin");
        AwaitAccountPhase(fixture, AccountLoginPhase.ChoosingProfile);
        XsrUiScene scene = fixture.Shell.Render(AccountTestSize);
        AssertEqual("LittleSkin 账户", FindByKey(fixture.Shell, scene, "AccountHeader").Text);
        AssertTrue(fixture.Shell.Renderer.Activate(scene.Nodes.Single(node => node.Label == "选择角色 Second").Entity));
        fixture.Onboarding.Service.WhenIdle.GetAwaiter().GetResult();
        AssertEqual(AccountLoginPhase.Completed, AccountSnapshot(fixture).Phase);
        LaunchProfile profile = fixture.Service.GetProfile(0).Value!;
        AssertEqual("PRIVATE-MC", profile.AccessToken);
        AssertEqual("PRIVATE-OAUTH", profile.ProviderAccessToken);
        AssertEqual("PRIVATE-REFRESH", profile.RefreshToken);
        AssertEqual("Second", FindByKey(fixture.Shell, fixture.Shell.Render(AccountTestSize), "AccountName").Text);
        AssertSceneHides(fixture, "PRIVATE-MC", "PRIVATE-OAUTH", "PRIVATE-REFRESH", "PRIVATE-DEVICE");
    }

    private static void AccountFailuresAndLateFilePickerStayInCurrentView()
    {
        ControlledMicrosoft microsoft = new();
        AccountEffects effects = new();
        using LaunchPageFixture fixture = new(new ImmediateInstanceSource([]), accountEffects: effects,
            microsoft: microsoft, accountOptions: new("", null));
        AccountClick(fixture, "AccountAdd"); AccountClick(fixture, "ProviderMicrosoft");
        fixture.Onboarding.Service.WhenIdle.GetAwaiter().GetResult();
        AssertEqual(AccountLoginPhase.Failed, AccountSnapshot(fixture).Phase);
        AssertTrue(AccountSnapshot(fixture).Message.Contains("PCL_MS_CLIENT_ID", StringComparison.Ordinal));
        AssertEqual(0, microsoft.Requests);
        AccountClick(fixture, "AccountBack"); AccountClick(fixture, "ProviderLittleSkin");
        fixture.Onboarding.Service.WhenIdle.GetAwaiter().GetResult();
        AssertEqual(AccountLoginPhase.Failed, AccountSnapshot(fixture).Phase);
        AccountClick(fixture, "AccountBack"); AccountClick(fixture, "ProviderImport");
        AccountClick(fixture, "BrowseProfiles");
        AccountClick(fixture, "AccountBack"); AccountClick(fixture, "AccountAdd");
        effects.File.SetResult("stale-path");
        AssertTrue(SpinWait.SpinUntil(() => effects.Returned, TimeSpan.FromSeconds(2)));
        AssertEqual("", ReadCell(fixture.Store, AccountFormState.ImportPath));
        AssertTrue(HasKey(fixture.Shell, fixture.Shell.Render(AccountTestSize), "ProviderMicrosoft"));
        AssertEqual(0, fixture.Service.GetViews().Count);
    }

    private static void AccountClick(LaunchPageFixture fixture, string key)
    {
        XsrUiScene scene = fixture.Shell.Render(AccountTestSize);
        AssertTrue(fixture.Shell.Renderer.Activate(FindByKey(fixture.Shell, scene, key).Entity));
    }

    private static void AccountFill(LaunchPageFixture fixture, string key, string text)
    {
        XsrUiScene scene = fixture.Shell.Render(AccountTestSize);
        AssertTrue(fixture.Shell.Renderer.Focus(FindByKey(fixture.Shell, scene, key).Entity));
        fixture.Shell.Renderer.EditText(XsrUiTextEdit.SelectAll);
        AssertTrue(fixture.Shell.Renderer.InsertText(text));
    }

    private static AccountLoginSnapshot AccountSnapshot(LaunchPageFixture fixture) => fixture.Store.Read<AccountLoginSnapshot>(
        fixture.Store.Resolve(AccountOnboardingState.Login)).Value!;

    private static void AwaitAccountPhase(LaunchPageFixture fixture, AccountLoginPhase phase) =>
        AssertTrue(SpinWait.SpinUntil(() => AccountSnapshot(fixture).Phase == phase, TimeSpan.FromSeconds(5)));

    private static void AssertSceneHides(LaunchPageFixture fixture, params string[] secrets)
    {
        XsrUiScene scene = fixture.Shell.Render(AccountTestSize);
        string visible = string.Join("\n", scene.Nodes.Select(node => node.Text + node.Label + node.TextInput))
            + AccountSnapshot(fixture) + string.Join("\n", fixture.Service.GetViews());
        foreach (string secret in secrets) AssertFalse(visible.Contains(secret, StringComparison.Ordinal));
    }

    private sealed class ControlledMicrosoft : IMicrosoftMinecraftAuthService
    {
        public int Requests;
        public TaskCompletionSource<MicrosoftMinecraftLoginResult> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<MicrosoftDeviceCodeInfo> RequestDeviceCodeAsync(string clientId, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Requests);
            return Task.FromResult(new MicrosoftDeviceCodeInfo("PRIVATE-DEVICE", "PUBLIC-CODE", "https://www.microsoft.com/link", null,
                "", TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(1)));
        }
        public Task<MicrosoftMinecraftLoginResult> CompleteDeviceLoginAsync(string clientId, MicrosoftDeviceCodeInfo deviceCode,
            IProgress<double>? progress = null, CancellationToken cancellationToken = default) => Completion.Task;
        public Task<MicrosoftMinecraftLoginResult> RefreshAsync(string clientId, string refreshToken, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class AccountEffects : IAccountUiEffects
    {
        public Uri? Opened;
        public string? Copied;
        public TaskCompletionSource<string?> File = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public volatile bool Returned;
        public void OpenAuthorization(Uri uri) => Opened = uri;
        public Task CopyCode(string code) { Copied = code; return Task.CompletedTask; }
        public async Task<string?> PickProfiles() { string? path = await File.Task; Returned = true; return path; }
    }

    private sealed class AccountHttp : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _responses = [];
        public List<string> Bodies { get; } = [];
        public void Serve(string url, string body) => _responses.Add(url, body);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null) Bodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            return _responses.TryGetValue(request.RequestUri!.AbsoluteUri, out string? body)
                ? new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") }
                : new(HttpStatusCode.NotFound);
        }
    }

    private static void ImportedProfilesRemainVisibleWhenPickerReopens()
    {
        using LaunchPageFixture fixture = new(new ImmediateInstanceSource([]));
        string path = Path.Combine(fixture.TemporaryDirectory, "legacy-profiles.json");
        new LaunchProfileFilePort(path).Save(new LaunchProfileSet
        {
            Profiles = Enumerable.Range(0, 8).Select(index => new LaunchProfile
            {
                Username = "Imported" + index,
                Kind = LaunchProfileKind.Offline,
            }).ToList(),
        });
        byte[] original = File.ReadAllBytes(path);
        XsrUiSize size = new(810, 470);
        XsrUiScene scene = fixture.Shell.Render(size);
        AssertTrue(fixture.Shell.Renderer.Activate(FindByKey(fixture.Shell, scene, "AccountImport").Entity));
        scene = fixture.Shell.Render(size);
        fixture.Shell.Renderer.SetTextInputValue(FindByKey(fixture.Shell, scene, "ImportPath").Entity, path);
        AssertTrue(fixture.Shell.Renderer.Activate(FindByKey(fixture.Shell, scene, "FormSubmit").Entity));
        fixture.Onboarding.Service.WhenIdle.GetAwaiter().GetResult();
        AssertEqual(AccountLoginPhase.Completed, fixture.Store.Read<AccountLoginSnapshot>(
            fixture.Store.Resolve(AccountOnboardingState.Login)).Value!.Phase);
        scene = fixture.Shell.Render(size); // roster gets built while hidden behind the selected profile
        AssertEqual("Imported0", FindByKey(fixture.Shell, scene, "AccountName").Text);
        AssertTrue(original.SequenceEqual(File.ReadAllBytes(path)));
        foreach (XsrUiShellStyle style in Enum.GetValues<XsrUiShellStyle>())
        {
            fixture.Shell.SetStyle(style);
            scene = fixture.Shell.Render(size);
            AssertTrue(fixture.Shell.Renderer.Activate(FindByKey(fixture.Shell, scene, "AccountSwitch").Entity));
            scene = fixture.Shell.Render(size);
            XsrUiSceneNode row = FindByKey(fixture.Shell, scene, "account-row:1");
            AssertClose(56, row.Rect.Height);
            AssertTrue(row.Rect.Width > 100);
            AssertEqual("Imported1", FindByKey(fixture.Shell, scene, "ProfileName:1").Text);
            AssertTrue(fixture.Shell.Renderer.Activate(row.Entity));
            scene = fixture.Shell.Render(size);
            AssertEqual("Imported1", FindByKey(fixture.Shell, scene, "AccountName").Text);
        }
    }
}
