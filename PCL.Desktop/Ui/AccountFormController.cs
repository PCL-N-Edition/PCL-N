using PCL.Pxml;
using PCL.Services.Accounts;
using PCL.Services.Logging;
using PCL.UI.Next;
using PCL.UI.Next.Backend.Avalonia;
using PCL.Xsr;
using PCL.Xsr.Runtime;
using PCL.Xsr.State;

namespace PCL.Desktop.Ui;

internal interface IAccountUiEffects
{
    Task OpenAuthorization(Uri uri);
    Task CopyCode(string code);
    Task<string?> PickProfiles();
}

internal sealed class NativeAccountUiEffects(AvaloniaUiPlatformActions actions) : IAccountUiEffects
{
    // Shell execution can briefly block while Windows resolves the registered browser. Keep that
    // work off the render thread so an authorization transition cannot stall pointer presentation.
    public Task OpenAuthorization(Uri uri) => Task.Run(() => actions.OpenHttpsUri(uri));
    public Task CopyCode(string code) => actions.CopyTextAsync(code);
    public Task<string?> PickProfiles() => actions.PickJsonFileAsync();
}

/// <summary>Render-thread PXML form projection and typed intent adapter. Auth workers own no tree.</summary>
internal sealed class AccountFormController : IDisposable
{
    private readonly XsrUiShell _shell;
    private readonly DesktopUiIntentSink _intents;
    private readonly XsrCommandRouter _commands;
    private readonly XsrStateStore _store;
    private readonly DesktopFeedbackService _feedback;
    private readonly IAccountUiEffects? _effects;
    private readonly LogService? _log;
    private readonly Dictionary<string, XsrUiEntityId> _entities = [];
    private readonly Dictionary<XsrUiEntityId, string> _importRows = [];
    private readonly Dictionary<XsrUiEntityId, string> _characterRows = [];
    private readonly PxmlHostIr _rowTemplate = Load("AccountChoiceRow.pxml");
    private readonly CancellationTokenSource _lifetime = new();
    private long _importsRevision = -1, _charactersRevision = -1, _seenCompletion, _viewEpoch;
    private long _presentedChallengeGeneration = -1;
    private string _appliedPath = string.Empty;
    private string? _pendingFocus;
    private XsrUiEntityId _returnFocus;
    private readonly XsrUiEntityId _fallbackFocus;
    private bool _keyboardFocus, _disposed;
    private AccountLoginProvider _provider;

    public AccountFormController(XsrUiShell shell, DesktopUiIntentSink intents, XsrCommandRouter commands,
        XsrStateStore store, XsrUiEntityId accountBody, DesktopFeedbackService feedback,
        IAccountUiEffects? effects = null, LogService? log = null)
    {
        _shell = shell; _intents = intents; _commands = commands; _store = store;
        _feedback = feedback ?? throw new ArgumentNullException(nameof(feedback));
        _effects = effects;
        _log = log;
        XsrUiEntityId fallback = default;
        shell.Tree.Walk(shell.Tree.Parent(accountBody), entity =>
        {
            if (shell.Tree.Name(entity) == "AccountAdd") fallback = entity;
            if (shell.Tree.Name(entity) is "AccountBack" or "AccountSwitch") _entities[shell.Tree.Name(entity)] = entity;
            return true;
        });
        _fallbackFocus = fallback;
        XsrUiEntityId form = PxmlUiLoader.Load(Load("AccountForm.pxml"), shell.Tree, store, accountBody);
        shell.Tree.Walk(form, entity => { _entities[shell.Tree.Name(entity)] = entity; Style(entity); return true; });
        _intents.IntentEmitted += OnIntent;
        _shell.Renderer.FramePreparing += OnFrame;
        _ = Dispatch(AccountOnboardingRoutes.DiscoverImports, new AccountDiscoverImportsCommand());
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _intents.IntentEmitted -= OnIntent;
        _shell.Renderer.FramePreparing -= OnFrame;
        _lifetime.Cancel();
        _lifetime.Dispose();
        ClearDrafts();
    }

    private void OnIntent(object? sender, DesktopUiIntentEventArgs e)
    {
        string command = e.Intent.Command.ToString();
        if (command == "ui.account.add" || command == "ui.account.import")
        {
            _returnFocus = e.Intent.Source;
            _keyboardFocus = IsKeyboard(e.Intent.Source);
            Open(command == "ui.account.add" ? "providers" : "import");
        }
        else if (command.StartsWith("ui.navigation.", StringComparison.Ordinal) && command != "ui.navigation.expand")
        {
            if (ReadBool("open")) Close(restoreFocus: false);
        }
        else if (command == "ui.account.back")
        {
            if (!ReadBool("open")) _intents.Emit(XsrSemanticId.Parse("ui.account.dismiss"), e.Intent.Source, XsrCorrelationId.Create());
            else if (ReadText("mode") is "providers" or "import") Close();
            else Open("providers");
        }
        else if (!ReadBool("open")) return;
        else if (command == "ui.account.close") Close();
        else if (command == "ui.account.cancel")
            _ = Dispatch(AccountOnboardingRoutes.Cancel, new AccountLoginCancelCommand(Snapshot.Generation));
        else if (command is "ui.account.microsoft" or "ui.account.littleskin" or "ui.account.third-party" or "ui.account.offline")
        {
            _provider = command switch
            {
                "ui.account.microsoft" => AccountLoginProvider.Microsoft,
                "ui.account.littleskin" => AccountLoginProvider.LittleSkin,
                "ui.account.third-party" => AccountLoginProvider.ThirdParty,
                _ => AccountLoginProvider.Offline,
            };
            Open(_provider is AccountLoginProvider.Microsoft or AccountLoginProvider.LittleSkin ? "device"
                : _provider == AccountLoginProvider.Offline ? "offline" : "third-party");
            if (_provider is AccountLoginProvider.Microsoft or AccountLoginProvider.LittleSkin) Submit();
        }
        else if (command == "ui.account.submit") Submit();
        else if (command == "ui.account.browse") _ = PickProfiles();
        else if (command == "ui.account.authorize")
        {
            _ = OpenAuthorization(Snapshot);
        }
        else if (command == "ui.account.copy-code") _ = CopyCode(Snapshot.UserCode, showSuccess: true);
        else if (command == "ui.account.choice")
        {
            if (_importRows.TryGetValue(e.Intent.Source, out string? path)) Publish("import-path", path);
            else if (_characterRows.TryGetValue(e.Intent.Source, out string? uuid))
                _ = Dispatch(AccountOnboardingRoutes.ChooseCharacter, new AccountChooseCharacterCommand(Snapshot.Generation, uuid));
        }
    }

    private void Open(string mode)
    {
        if (Snapshot.IsBusy) _ = Dispatch(AccountOnboardingRoutes.Cancel, new AccountLoginCancelCommand(Snapshot.Generation));
        Interlocked.Increment(ref _viewEpoch);
        ClearDrafts();
        Publish("open", true); Publish("mode", mode);
        _seenCompletion = Snapshot.Generation;
        _pendingFocus = mode switch { "offline" => "OfflineName", "third-party" => "AuthServer", "import" => "ImportPath", "device" => "AccountBack", _ => "ProviderMicrosoft" };
        if (mode == "import") _ = Dispatch(AccountOnboardingRoutes.DiscoverImports, new AccountDiscoverImportsCommand());
    }

    private void Close(bool restoreFocus = true)
    {
        if (Snapshot.IsBusy) _ = Dispatch(AccountOnboardingRoutes.Cancel, new AccountLoginCancelCommand(Snapshot.Generation));
        Interlocked.Increment(ref _viewEpoch);
        Publish("open", false);
        _store.Publish(_store.Resolve(LaunchPageState.AccountAddVisibleKey), true);
        ClearDrafts();
        if (restoreFocus) RestoreFocus();
    }

    private void Submit()
    {
        if (Snapshot.IsBusy) return;
        if (ReadText("mode") == "import")
        {
            _ = Dispatch(AccountOnboardingRoutes.Import, new AccountImportCommand(Draft("ImportPath")));
            return;
        }
        AccountLoginStartCommand command = new(_provider,
            _provider == AccountLoginProvider.Offline ? Draft("OfflineName") : Draft("AuthUsername"),
            Draft("AuthServer"), Draft("AuthPassword"));
        _shell.Renderer.SetTextInputValue(_entities["AuthPassword"], string.Empty);
        _ = Dispatch(AccountOnboardingRoutes.Start, command);
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        AccountLoginSnapshot snapshot = Snapshot;
        bool open = ReadBool("open");
        if (open && snapshot.Generation > _seenCompletion && snapshot.Phase == AccountLoginPhase.Completed)
        {
            _seenCompletion = snapshot.Generation;
            Publish("open", false);
            _store.Publish(_store.Resolve(LaunchPageState.AccountAddVisibleKey), true);
            _store.Publish(_store.Resolve(LaunchPageState.AccountPickerKey), false);
            ClearDrafts();
            RestoreFocus();
            open = false;
        }
        string mode = ReadText("mode");
        foreach (string name in new[] { "providers", "offline", "third-party", "import", "device" })
            Publish(name, open && mode == name);
        Publish("title", mode switch
        {
            "offline" => "离线档案",
            "third-party" => "第三方账户",
            "import" => "导入旧档案",
            "device" when _provider == AccountLoginProvider.LittleSkin => "LittleSkin 账户",
            "device" => "Microsoft 账户",
            _ => "添加账户",
        });
        Publish("status", mode == "providers" || snapshot.Generation <= _seenCompletion ? string.Empty : snapshot.Message);
        Publish("status-visible", !string.IsNullOrWhiteSpace(ReadText("status")));
        Publish("user-code", snapshot.UserCode);
        Publish("busy", open && snapshot.IsBusy);
        Publish("code", open && snapshot.Phase == AccountLoginPhase.AwaitingAuthorization);
        Publish("characters", open && snapshot.Phase == AccountLoginPhase.ChoosingProfile);
        Publish("submit", open && mode != "providers" && !snapshot.IsBusy);
        Publish("submit-label", mode == "import" ? "确认导入" : mode == "offline" ? "创建档案" : mode == "device" ? "重新登录" : "登录并添加");
        if (open && mode == "device" && snapshot.Phase == AccountLoginPhase.AwaitingAuthorization
            && snapshot.Generation != _presentedChallengeGeneration)
        {
            // State can invalidate many frames while polling. Generation is the service-owned
            // identity of this public challenge, so browser/clipboard effects occur exactly once.
            _presentedChallengeGeneration = snapshot.Generation;
            _ = OpenAuthorization(snapshot);
            _ = CopyCode(snapshot.UserCode, showSuccess: false);
        }
        XsrCollectionSnapshot<AccountImportCandidate> imports = _store.ReadCollection<AccountImportCandidate>(_store.Resolve(AccountOnboardingState.Imports));
        if (imports.Revision != _importsRevision)
        {
            BuildRows("ImportCandidates", _importRows, imports.Items.Select(item => (item.DisplayPath, "使用已发现的旧档案", item.DisplayPath)));
            _importsRevision = imports.Revision;
        }
        XsrCollectionSnapshot<AccountCharacterChoice> characters = _store.ReadCollection<AccountCharacterChoice>(_store.Resolve(AccountOnboardingState.Characters));
        if (characters.Revision != _charactersRevision)
        {
            BuildRows("CharacterChoices", _characterRows, characters.Items.Select(item => (item.Uuid, item.Username, "选择角色 " + item.Username)));
            _charactersRevision = characters.Revision;
        }
        string path = ReadText("import-path");
        if (path != _appliedPath)
        {
            _appliedPath = path;
            _shell.Renderer.SetTextInputValue(_entities["ImportPath"], path);
        }
        if (open && _pendingFocus is { } focus)
        {
            if (_shell.Renderer.Focus(_entities[focus], _keyboardFocus)) _pendingFocus = null;
        }
    }

    private void BuildRows(string host, Dictionary<XsrUiEntityId, string> destinations,
        IEnumerable<(string Value, string Text, string Label)> entries)
    {
        foreach (XsrUiEntityId entity in destinations.Keys) _shell.Tree.Destroy(entity);
        destinations.Clear();
        foreach ((string value, string text, string label) in entries)
        {
            PxmlIrNode node = _rowTemplate.Root with { Key = host + ":" + destinations.Count, Content = text, Label = label };
            XsrUiEntityId entity = PxmlUiLoader.Load(new PxmlHostIr(node), _shell.Tree, _store, _entities[host]);
            Style(entity);
            destinations.Add(entity, value);
        }
    }

    private void Style(XsrUiEntityId entity)
    {
        string name = _shell.Tree.Name(entity);
        bool button = _shell.Tree.GetComponent<XsrUiInput>(entity)?.Clickable == true;
        bool input = _shell.Tree.GetComponent<XsrUiTextInput>(entity) is not null;
        bool primary = name is "FormSubmit" or "OpenAuthorization";
        _shell.Tree.SetComponent(entity, new XsrUiVisualStyle
        {
            Surface = XsrUiSurfaceKind.Solid,
            Background = primary ? new(11, 91, 203) : button || input ? new(244, 246, 250) : XsrUiColor.Transparent,
            Foreground = primary ? new(255, 255, 255) : new(52, 61, 74),
            Border = input ? new(218, 225, 235) : XsrUiColor.Transparent,
            BorderWidth = input ? 1 : 0,
            Hover = button ? new(115, 158, 220, 35) : XsrUiColor.Transparent,
            CornerRadius = name == "FormBack" ? XsrUiCornerRadii.Pill(32) : XsrUiCornerRadii.Inset,
            FontSize = name == "FormTitle" ? 18 : name == "UserCode" ? 22 : name is "FormStatus" or "ImportHelp" or "OfflineHelp" or "ServerPrivacy" ? 12 : 14,
            FontWeight = button || name is "FormTitle" or "UserCode" ? 600 : 400,
            TextAlignment = button || name == "UserCode" ? XsrUiTextAlignment.Center : XsrUiTextAlignment.Start,
            WrapText = name is "FormStatus" or "ImportHelp" or "OfflineHelp" or "ServerPrivacy",
        });
    }

    private async Task Dispatch<T>(XsrSemanticId route, T command) where T : notnull
    {
        if (!_commands.TryResolve(route, out XsrCommandId id)) { _feedback.Error("账户操作未注册。"); return; }
        XsrResult result = await _commands.Dispatch(id, command, cancellationToken: _lifetime.Token).Completion.ConfigureAwait(false);
        if (!_disposed && !result.IsSuccess) _feedback.Error("操作未完成，当前登录会话可能已结束，请重试。");
    }

    private async Task PickProfiles()
    {
        long epoch = Interlocked.Read(ref _viewEpoch);
        try
        {
            string? path = await RequireEffects().PickProfiles().ConfigureAwait(false);
            if (!_disposed && epoch == Interlocked.Read(ref _viewEpoch) && path is not null) Publish("import-path", path);
        }
        catch (Exception) { if (!_disposed) _feedback.Warn("无法打开文件选择器，也可以在上方填写文件路径。"); }
    }

    private async Task OpenAuthorization(AccountLoginSnapshot snapshot)
    {
        string address = snapshot.VerificationUri;
        if (!AccountOnboardingService.IsVerificationUri(_provider, address))
        {
            if (!_disposed) _feedback.Warn("授权地址无效，请重新发起登录。");
            return;
        }

        try { await RequireEffects().OpenAuthorization(new Uri(address)).ConfigureAwait(false); }
        catch (Exception) { if (!_disposed) _feedback.Warn("无法打开浏览器，请检查系统默认浏览器设置。"); }
    }

    private async Task CopyCode(string code, bool showSuccess)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            if (!_disposed) _feedback.Warn("授权码尚未生成，请稍后重试。");
            return;
        }

        try
        {
            _log?.Debug("AccountOnboarding",
                $"Device authorization code copy started provider={_provider} mode={(showSuccess ? "manual" : "automatic")}");
            await RequireEffects().CopyCode(code).ConfigureAwait(false);
            _log?.Info("AccountOnboarding",
                $"Device authorization code copied provider={_provider} mode={(showSuccess ? "manual" : "automatic")}");
            if (showSuccess && !_disposed) _feedback.Info("授权码已复制。");
        }
        catch (Exception error)
        {
            _log?.Error(
                "AccountOnboarding",
                $"Device authorization code copy failed provider={_provider} mode={(showSuccess ? "manual" : "automatic")}",
                ExceptionDiagnostics.Describe(error));
            if (!_disposed) _feedback.Warn("无法复制授权码，请手动输入。");
        }
    }

    private IAccountUiEffects RequireEffects() => _effects ?? throw new InvalidOperationException("Native actions unavailable.");
    private AccountLoginSnapshot Snapshot => _store.Read<AccountLoginSnapshot>(_store.Resolve(AccountOnboardingState.Login)).Value
        ?? new(0, AccountLoginPhase.Idle, "");
    private string Draft(string key) => _shell.Tree.GetComponent<XsrUiTextInput>(_entities[key])!.ReadDraft();
    private bool IsKeyboard(XsrUiEntityId entity) => entity.IsAssigned && _shell.Tree.GetComponent<XsrUiInput>(entity)?.IsFocusVisible == true;
    private bool ReadBool(string name) => _store.ReadAppliedValue(_store.Resolve(AccountFormState.Key(name))) is true;
    private string ReadText(string name) => _store.ReadAppliedValue(_store.Resolve(AccountFormState.Key(name))) as string ?? string.Empty;
    private void Publish<T>(string name, T value)
    {
        XsrStateId id = _store.Resolve(AccountFormState.Key(name));
        if (!Equals(_store.ReadAppliedValue(id), value)) _store.Publish(id, value);
    }
    private void ClearDrafts()
    {
        foreach (string key in new[] { "OfflineName", "AuthServer", "AuthUsername", "AuthPassword", "ImportPath" })
            _shell.Renderer.SetTextInputValue(_entities[key], string.Empty);
        _appliedPath = string.Empty;
        Publish("import-path", string.Empty);
        _pendingFocus = null;
    }
    private void RestoreFocus()
    {
        if (!_returnFocus.IsAssigned || !_shell.Renderer.Focus(_returnFocus, _keyboardFocus))
            if (_fallbackFocus.IsAssigned) _shell.Renderer.Focus(_fallbackFocus, _keyboardFocus);
    }
    private static PxmlHostIr Load(string file)
    {
        using Stream stream = typeof(AccountFormController).Assembly.GetManifestResourceStream("PCL.Desktop.Ui." + file)
            ?? throw new InvalidOperationException("Missing account PXML template.");
        using StreamReader reader = new(stream);
        return PxmlCompiler.Compile(PxmlParser.Parse(reader.ReadToEnd()));
    }
}
