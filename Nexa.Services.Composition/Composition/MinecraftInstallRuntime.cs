using Nexa.Services.Foundation;
using Nexa.Services.Minecraft.Install;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;

namespace Nexa.Services.Composition;

public sealed class MinecraftInstallRuntime : IDisposable
{
    public MinecraftInstallRuntime(MinecraftInstallService service, XsrCommandRouter commands, HttpClient ownedHttp)
    {
        Service = service;
        Commands = commands;
        _ownedHttp = ownedHttp;
    }

    public MinecraftInstallService Service { get; }

    public XsrCommandRouter Commands { get; }

    private readonly HttpClient _ownedHttp;

    public void Dispose()
    {
        Service.Dispose();
        _ownedHttp.Dispose();
    }
}

public static class MinecraftInstallRuntimeComposer
{
    public static MinecraftInstallRuntime Compose(
        FoundationHost host,
        IInstallCatalogSource? catalogSource = null,
        IXsrDispatchObserver? observer = null)
    {
        HttpClient http = new();
        MinecraftInstallService service = new(
            host.Tasks,
            host.Downloads,
            catalogSource ?? new HttpInstallCatalogSource(http),
            http,
            inheritVanilla: () => host.SettingsPolicy.Read(new()).Value?.Values
                .FirstOrDefault(value => value.Key == "install.inherit-vanilla")?.Value.Value == "true",
            settingsPolicy: host.SettingsPolicy, hostStore: host.StateStore);
        XsrCommandRouterBuilder commands = new();
        commands.Register<MinecraftInstallRecoveryCommand>(MinecraftInstallRoutes.Recover,
            async (command, token) => await service.RecoverPendingAsync(command, token).ConfigureAwait(false));
        commands.Register<MinecraftInstallCommand>(MinecraftInstallRoutes.Run,
            async (command, token) =>
            {
                XsrResult<MinecraftInstallResult> result = await service.InstallAsync(command, token).ConfigureAwait(false);
                return result.IsSuccess
                    ? Nexa.Xsr.XsrResult.Success()
                    : Nexa.Xsr.XsrResult.Failure(result.Error!);
            });
        return new(service, commands.Build(observer ?? new Observer()), http);
    }

    private sealed class Observer : IXsrDispatchObserver
    {
        public void OnCompleted(XsrDispatchObservation observation) { }
    }
}
