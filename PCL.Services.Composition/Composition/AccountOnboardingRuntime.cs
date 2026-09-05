using PCL.Services.Accounts;
using PCL.Services.Foundation;
using PCL.Services.Logging;
using PCL.Xsr.Runtime;

namespace PCL.Services.Composition;

public sealed class AccountOnboardingRuntime(AccountOnboardingService service, XsrCommandRouter commands, HttpClient? ownedClient = null, AccountSkinService? skins = null, IAccountLaunchIdentityResolver? launchIdentityResolver = null) : IDisposable
{
    public AccountOnboardingService Service { get; } = service;
    public XsrCommandRouter Commands { get; } = commands;
    public AccountSkinService? Skins { get; } = skins;

    /// <summary>
    /// The launch identity resolver wired to this runtime's own Microsoft auth capability, so
    /// the Minecraft runtime can refresh Microsoft sessions without recomposing them.
    /// </summary>
    public IAccountLaunchIdentityResolver? LaunchIdentityResolver { get; } = launchIdentityResolver;
    public void Dispose() { Service.Dispose(); Skins?.Dispose(); ownedClient?.Dispose(); }
}

public static class AccountOnboardingRuntimeComposer
{
    public static AccountOnboardingRuntime Compose(FoundationHost host, HttpClient? client = null,
        AccountOnboardingOptions? options = null, LegacyProfileImport? imports = null,
        IMicrosoftMinecraftAuthService? microsoft = null, ILittleSkinOAuthService? littleSkin = null,
        IXsrDispatchObserver? observer = null)
    {
        HttpClient http = client ?? new HttpClient(new DiagnosticHttpHandler(host.Logging,
            new HttpClientHandler { AllowAutoRedirect = false }))
        { Timeout = TimeSpan.FromSeconds(30) };
        // One instance, two consumers: the onboarding service and the launch resolver MUST
        // share this capability, or production silently loses Microsoft refresh.
        IMicrosoftMinecraftAuthService microsoftService =
            microsoft ?? new MicrosoftMinecraftAuthService(http, log: host.Logging);
        AccountOnboardingOptions resolvedOptions = options ?? AccountOnboardingOptions.FromEnvironment();
        AccountOnboardingService service = new(host.Accounts, microsoftService,
            littleSkin ?? new LittleSkinOAuthService(http, host.Logging), new YggdrasilAuthService(http, host.Logging),
            resolvedOptions, imports, host.Logging);
        XsrCommandRouterBuilder commands = new();
        AccountSkinService skins = new(host.Accounts, http, host.Logging);
        commands.Register<AccountRefreshSkinsCommand>(AccountSkinService.RefreshRoute, (_, _) => ValueTask.FromResult(skins.Refresh()));
        commands.Register<AccountLoginStartCommand>(AccountOnboardingRoutes.Start, (command, _) => ValueTask.FromResult(service.Start(command)));
        commands.Register<AccountLoginCancelCommand>(AccountOnboardingRoutes.Cancel, (command, _) => ValueTask.FromResult(service.Cancel(command.Generation)));
        commands.Register<AccountChooseCharacterCommand>(AccountOnboardingRoutes.ChooseCharacter, (command, _) => ValueTask.FromResult(service.ChooseCharacter(command.Generation, command.Uuid)));
        commands.Register<AccountImportCommand>(AccountOnboardingRoutes.Import, (command, _) => ValueTask.FromResult(service.Import(command)));
        commands.Register<AccountDiscoverImportsCommand>(AccountOnboardingRoutes.DiscoverImports,
            async (_, cancellation) => await Task.Run(service.DiscoverImports, cancellation).ConfigureAwait(false));
        IAccountLaunchIdentityResolver resolver = new AccountLaunchIdentityResolver(
            host.Accounts,
            microsoftService,
            resolvedOptions.MicrosoftClientId,
            host.Logging);
        return new(service, commands.Build(observer ?? new Observer()), client is null ? http : null, skins, resolver);
    }
    private sealed class Observer : IXsrDispatchObserver { public void OnCompleted(XsrDispatchObservation observation) { } }
}
