using PCL.Services.Minecraft.Launch;
using PCL.Xsr;
using PCL.Xsr.State;

namespace PCL.Services.Accounts;

/// <summary>Product login lifecycle over the existing authentication and durable profile services.</summary>
public sealed class AccountOnboardingService : IDisposable
{
    private readonly AccountService _accounts;
    private readonly IMicrosoftMinecraftAuthService _microsoft;
    private readonly ILittleSkinOAuthService _littleSkin;
    private readonly YggdrasilAuthService _yggdrasil;
    private readonly AccountOnboardingOptions _options;
    private readonly LegacyProfileImport _imports;
    private readonly XsrStateStore _store;
    private readonly object _gate = new();
    private Operation? _active;
    private long _generation;
    private Task _running = Task.CompletedTask;
    private AccountLoginSnapshot _snapshot = new(0, AccountLoginPhase.Idle, "");
    private bool _disposed;

    public AccountOnboardingService(AccountService accounts, IMicrosoftMinecraftAuthService microsoft,
        ILittleSkinOAuthService littleSkin, YggdrasilAuthService yggdrasil, AccountOnboardingOptions options,
        LegacyProfileImport? imports = null)
    {
        _accounts = accounts;
        _store = accounts.StateStore;
        _microsoft = microsoft;
        _littleSkin = littleSkin;
        _yggdrasil = yggdrasil;
        _options = options;
        _imports = imports ?? new LegacyProfileImport();
        _store.Publish(_store.Resolve(AccountOnboardingState.Login), _snapshot);
    }

    public Task WhenIdle { get { lock (_gate) return _running; } }

    public XsrResult Start(AccountLoginStartCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Operation operation = Begin();
            _running = Task.Run(() => RunLogin(operation, command));
            return XsrResult.Success();
        }
    }

    public XsrResult Import(AccountImportCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Operation operation = Begin();
            _running = Task.Run(() => RunImport(operation, command.Path));
            return XsrResult.Success();
        }
    }

    public XsrResult DiscoverImports()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ReplaceCollection(AccountOnboardingState.Imports, _imports.Discover(), item => item.Id);
            return XsrResult.Success();
        }
    }

    public XsrResult Cancel(long generation)
    {
        lock (_gate)
        {
            if (_active is not { } operation || operation.Generation != generation || !_snapshot.IsBusy)
                return XsrResult.Failure(AccountErrors.InvalidProfile("The login session is no longer active."));
            operation.Cancellation.Cancel();
            Publish(new(generation, AccountLoginPhase.Cancelled, "已取消"));
            ReplaceCollection<AccountCharacterChoice>(AccountOnboardingState.Characters, [], choice => choice.Uuid);
            return XsrResult.Success();
        }
    }

    public XsrResult ChooseCharacter(long generation, string uuid)
    {
        lock (_gate)
        {
            if (_active is not { } operation || operation.Generation != generation || !IsCurrent(operation)
                || _snapshot.Phase != AccountLoginPhase.ChoosingProfile
                || !operation.Characters.Any(character => character.Uuid == uuid))
                return XsrResult.Failure(AccountErrors.InvalidProfile("The character choice is stale or unknown."));
            operation.Choice.TrySetResult(uuid);
            return XsrResult.Success();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _active?.Cancellation.Cancel();
            _active = null;
        }
    }

    private Operation Begin()
    {
        _active?.Cancellation.Cancel();
        Operation operation = new(++_generation);
        _active = operation;
        ReplaceCollection<AccountCharacterChoice>(AccountOnboardingState.Characters, [], choice => choice.Uuid);
        Publish(new(operation.Generation, AccountLoginPhase.Starting, "正在准备…"));
        return operation;
    }

    private async Task RunLogin(Operation operation, AccountLoginStartCommand command)
    {
        try
        {
            LaunchProfile profile = command.Provider switch
            {
                AccountLoginProvider.Offline => CreateOffline(command.Username),
                AccountLoginProvider.Microsoft => await LoginMicrosoft(operation).ConfigureAwait(false),
                AccountLoginProvider.LittleSkin => await LoginLittleSkin(operation).ConfigureAwait(false),
                AccountLoginProvider.ThirdParty => await LoginThirdParty(command, operation.Cancellation.Token).ConfigureAwait(false),
                _ => throw new OnboardingFailure("不支持的账户类型。"),
            };
            lock (_gate)
            {
                if (!IsCurrent(operation)) return;
                Publish(new(operation.Generation, AccountLoginPhase.Saving, "正在保存档案…", Progress: .95));
                XsrResult<int> saved = AccountLoginProfiles.Upsert(_accounts, profile);
                if (!saved.IsSuccess) throw new OnboardingFailure("档案保存失败，请检查数据目录权限后重试。");
                _accounts.SelectProfile(saved.Value);
                Publish(new(operation.Generation, AccountLoginPhase.Completed, "档案已添加", Progress: 1));
            }
        }
        catch (Exception failure) { FinishFailure(operation, failure); }
        finally { Release(operation); }
    }

    private async Task RunImport(Operation operation, string path)
    {
        try
        {
            IReadOnlyList<LaunchProfile> profiles = await LegacyProfileImport.ReadAsync(path, operation.Cancellation.Token).ConfigureAwait(false);
            lock (_gate)
            {
                if (!IsCurrent(operation)) return;
                XsrResult<int> imported = _accounts.ImportProfiles(profiles);
                if (!imported.IsSuccess) throw new OnboardingFailure("无法导入档案，请检查文件内容与数据目录权限。");
                Publish(new(operation.Generation, AccountLoginPhase.Completed,
                    imported.Value > 0 ? $"已导入 {imported.Value} 个档案" : "这些档案已存在，无需重复导入", Progress: 1));
            }
        }
        catch (Exception failure) { FinishFailure(operation, failure); }
        finally { Release(operation); }
    }

    private static LaunchProfile CreateOffline(string username)
    {
        string name = username.Trim();
        if (name.Length is < 1 or > 16 || name.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_'))
            throw new OnboardingFailure("离线名称需为 1–16 位英文字母、数字或下划线。");
        return new LaunchProfile { Username = name, Kind = LaunchProfileKind.Offline, Uuid = MinecraftOfflineIdentity.UuidFromName(name) };
    }

    private async Task<LaunchProfile> LoginMicrosoft(Operation operation)
    {
        if (string.IsNullOrWhiteSpace(_options.MicrosoftClientId))
            throw new OnboardingFailure("未配置 Microsoft 应用 ID：请设置 PCL_MS_CLIENT_ID 后重新启动。");
        MicrosoftDeviceCodeInfo code = await _microsoft.RequestDeviceCodeAsync(_options.MicrosoftClientId, operation.Cancellation.Token).ConfigureAwait(false);
        ShowDeviceCode(operation, AccountLoginProvider.Microsoft, code.UserCode, code.VerificationUriComplete ?? code.VerificationUri);
        MicrosoftMinecraftLoginResult result = await _microsoft.CompleteDeviceLoginAsync(_options.MicrosoftClientId, code,
            new InlineProgress(value => ReportProgress(operation, value)), operation.Cancellation.Token).ConfigureAwait(false);
        if (!result.OwnsMinecraft) throw new OnboardingFailure("该 Microsoft 账户未拥有 Minecraft Java 版。");
        return AccountLoginProfiles.FromMicrosoft(result);
    }

    private async Task<LaunchProfile> LoginLittleSkin(Operation operation)
    {
        LittleSkinOAuthConfiguration configuration = _options.LittleSkin
            ?? throw new OnboardingFailure("LittleSkin 应用配置缺失或无效：请检查 PCL_LITTLESKIN_CLIENT_ID 后重新启动。");
        LittleSkinDeviceCodeInfo code = await _littleSkin.RequestDeviceCodeAsync(configuration, operation.Cancellation.Token).ConfigureAwait(false);
        ShowDeviceCode(operation, AccountLoginProvider.LittleSkin, code.UserCode,
            string.IsNullOrWhiteSpace(code.VerificationUriComplete) ? code.VerificationUri : code.VerificationUriComplete);
        LittleSkinOAuthTokens tokens = await _littleSkin.WaitForDeviceAuthorizationAsync(configuration, code,
            new InlineProgress(value => ReportProgress(operation, value)), operation.Cancellation.Token).ConfigureAwait(false);
        IReadOnlyList<LittleSkinProfile> characters = await _littleSkin.GetProfilesAsync(tokens.AccessToken, operation.Cancellation.Token).ConfigureAwait(false);
        if (characters.Count is 0 or > 256) throw new OnboardingFailure("LittleSkin 没有可用角色，请先在网站创建角色。");
        string uuid = characters[0].Uuid;
        if (characters.Count > 1)
        {
            lock (_gate)
            {
                if (!IsCurrent(operation)) throw new OperationCanceledException();
                operation.Characters = characters;
                ReplaceCollection(AccountOnboardingState.Characters, characters.Select(character => new AccountCharacterChoice(character.Uuid, character.Username)).ToArray(), item => item.Uuid);
                Publish(new(operation.Generation, AccountLoginPhase.ChoosingProfile, "请选择 LittleSkin 角色", Progress: .8));
            }
            uuid = await operation.Choice.Task.WaitAsync(operation.Cancellation.Token).ConfigureAwait(false);
        }
        LittleSkinMinecraftSession session = await _littleSkin.CreateMinecraftSessionAsync(tokens.AccessToken, uuid, operation.Cancellation.Token).ConfigureAwait(false);
        return new LaunchProfile
        {
            Username = session.Username,
            Uuid = session.Uuid,
            Kind = LaunchProfileKind.LittleSkin,
            AuthServer = LittleSkinOAuthService.YggdrasilServer,
            AccessToken = session.AccessToken,
            ClientToken = session.ClientToken,
            ProviderAccessToken = tokens.AccessToken,
            ProviderTokenExpiresAtUnix = tokens.ExpiresAt.ToUnixTimeSeconds(),
            RefreshToken = tokens.RefreshToken,
        };
    }

    private async Task<LaunchProfile> LoginThirdParty(AccountLoginStartCommand command, CancellationToken cancellationToken)
    {
        string server = command.Server.Trim();
        if (!server.Contains("://", StringComparison.Ordinal)) server = "https://" + server;
        if (!Uri.TryCreate(server, UriKind.Absolute, out Uri? uri) || uri.UserInfo.Length > 0
            || (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
            throw new OnboardingFailure("认证服务器必须使用 HTTPS；仅本机开发服务器允许 HTTP。");
        if (string.IsNullOrWhiteSpace(command.Username) || string.IsNullOrEmpty(command.Password))
            throw new OnboardingFailure("请填写登录名和密码。");
        YggdrasilAuthLoginResult result = await _yggdrasil.AuthenticateAsync(
            new YggdrasilAuthLoginRequest(uri.AbsoluteUri, command.Username.Trim(), command.Password), cancellationToken).ConfigureAwait(false);
        return AccountLoginProfiles.FromYggdrasil(result);
    }

    public static bool IsVerificationUri(AccountLoginProvider provider, string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps
            || uri.UserInfo.Length > 0 || !uri.IsDefaultPort) return false;
        return provider switch
        {
            AccountLoginProvider.Microsoft => uri.Host is "microsoft.com" or "www.microsoft.com" or "login.microsoftonline.com",
            AccountLoginProvider.LittleSkin => uri.Host is "littleskin.cn" or "open.littleskin.cn" or "www.littleskin.cn",
            _ => false,
        };
    }

    private void ShowDeviceCode(Operation operation, AccountLoginProvider provider, string userCode, string uri)
    {
        if (!IsVerificationUri(provider, uri) || userCode.Length is 0 or > 64)
            throw new OnboardingFailure("授权服务返回了无效的验证地址或代码。");
        lock (_gate)
            if (IsCurrent(operation)) Publish(new(operation.Generation, AccountLoginPhase.AwaitingAuthorization,
                "在浏览器完成授权，返回后将自动继续。", userCode, uri));
    }

    private void ReportProgress(Operation operation, double value)
    {
        lock (_gate)
            if (IsCurrent(operation) && double.IsFinite(value)) Publish(_snapshot with { Progress = Math.Clamp(value, 0, .94) });
    }

    private void FinishFailure(Operation operation, Exception failure)
    {
        lock (_gate)
        {
            if (_disposed || _active != operation) return;
            bool cancelled = operation.Cancellation.IsCancellationRequested || failure is OperationCanceledException;
            string message = cancelled ? "已取消" : failure switch
            {
                OnboardingFailure safe => safe.Message,
                TimeoutException => "授权已过期，请重新开始。",
                HttpRequestException => "无法连接认证服务，请检查网络后重试。",
                IOException or System.Text.Json.JsonException => "无法读取档案文件，请确认文件格式和访问权限。",
                _ => "登录未完成，请检查登录信息、授权状态和账户权限后重试。",
            };
            Publish(new(operation.Generation, cancelled ? AccountLoginPhase.Cancelled : AccountLoginPhase.Failed, message));
        }
    }

    private void Release(Operation operation)
    {
        lock (_gate)
        {
            if (_active == operation)
            {
                _active = null;
                ReplaceCollection<AccountCharacterChoice>(AccountOnboardingState.Characters, [], choice => choice.Uuid);
            }
            operation.Cancellation.Dispose();
        }
    }

    private bool IsCurrent(Operation operation) => !_disposed && _active == operation && !operation.Cancellation.IsCancellationRequested;
    private void Publish(AccountLoginSnapshot snapshot)
    {
        _snapshot = snapshot;
        _store.Publish(_store.Resolve(AccountOnboardingState.Login), snapshot);
    }

    private void ReplaceCollection<T>(XsrSemanticId key, IReadOnlyList<T> items, Func<T, string> identity)
    {
        XsrStateId id = _store.Resolve(key);
        XsrCollectionSnapshot<T> snapshot = _store.ReadCollection<T>(id);
        HashSet<string> kept = items.Select(identity).ToHashSet(StringComparer.Ordinal);
        _store.PublishDelta(id, new XsrCollectionDelta<T, string>(snapshot.Revision, items,
            snapshot.Items.Select(identity).Where(value => !kept.Contains(value)).ToArray()));
    }

    private sealed class Operation(long generation)
    {
        public long Generation { get; } = generation;
        public CancellationTokenSource Cancellation { get; } = new();
        public TaskCompletionSource<string> Choice { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<LittleSkinProfile> Characters { get; set; } = [];
    }
    private sealed class InlineProgress(Action<double> report) : IProgress<double> { public void Report(double value) => report(value); }
    private sealed class OnboardingFailure(string message) : Exception(message);
}
