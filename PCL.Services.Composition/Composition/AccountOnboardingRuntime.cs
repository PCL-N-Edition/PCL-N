using PCL.Services.Accounts;
using PCL.Services.Foundation;
using PCL.Xsr.Runtime;

namespace PCL.Services.Composition;

public sealed class AccountOnboardingRuntime(AccountOnboardingService service, XsrCommandRouter commands, HttpClient? ownedClient = null) : IDisposable
{
    public AccountOnboardingService Service { get; } = service;
    public XsrCommandRouter Commands { get; } = commands;
    public void Dispose() { Service.Dispose(); ownedClient?.Dispose(); }
}

public static class AccountOnboardingRuntimeComposer
{
    public static AccountOnboardingRuntime Compose(FoundationHost host, HttpClient? client = null,
        AccountOnboardingOptions? options = null, LegacyProfileImport? imports = null,
        IMicrosoftMinecraftAuthService? microsoft = null, ILittleSkinOAuthService? littleSkin = null)
    {
        HttpClient http = client ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
        AccountOnboardingService service = new(host.Accounts, microsoft ?? new MicrosoftMinecraftAuthService(http),
            littleSkin ?? new LittleSkinOAuthService(http), new YggdrasilAuthService(http),
            options ?? AccountOnboardingOptions.FromEnvironment(), imports);
        XsrCommandRouterBuilder commands = new();
        commands.Register<AccountLoginStartCommand>(AccountOnboardingRoutes.Start, (command, _) => ValueTask.FromResult(service.Start(command)));
        commands.Register<AccountLoginCancelCommand>(AccountOnboardingRoutes.Cancel, (command, _) => ValueTask.FromResult(service.Cancel(command.Generation)));
        commands.Register<AccountChooseCharacterCommand>(AccountOnboardingRoutes.ChooseCharacter, (command, _) => ValueTask.FromResult(service.ChooseCharacter(command.Generation, command.Uuid)));
        commands.Register<AccountImportCommand>(AccountOnboardingRoutes.Import, (command, _) => ValueTask.FromResult(service.Import(command)));
        commands.Register<AccountDiscoverImportsCommand>(AccountOnboardingRoutes.DiscoverImports,
            async (_, cancellation) => await Task.Run(service.DiscoverImports, cancellation).ConfigureAwait(false));
        return new(service, commands.Build(new Observer()), client is null ? http : null);
    }
    private sealed class Observer : IXsrDispatchObserver { public void OnCompleted(XsrDispatchObservation observation) { } }
}
