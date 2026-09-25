using Nexa.Services.Accounts;
using Nexa.Services.Capabilities;
using Nexa.Services.Foundation;
using Nexa.Services.Minecraft.Management;
using Nexa.Services.Settings;
using Nexa.Services.Telemetry;
using Nexa.Xsr.Runtime;

namespace Nexa.Services.Composition;

/// <summary>
/// The composed foundation runtime: the foundation host (services over one shared state
/// store) plus the XSR command and query routers with every foundation route registered.
/// This is the only place foundation services meet the runtime dispatch layer; the product
/// never calls foundation service methods directly when an intent can be a command.
/// </summary>
public sealed class FoundationRuntime
{
    public FoundationRuntime(FoundationHost host, XsrCommandRouter commands, XsrQueryRouter queries)
    {
        Host = host ?? throw new ArgumentNullException(nameof(host));
        Commands = commands ?? throw new ArgumentNullException(nameof(commands));
        Queries = queries ?? throw new ArgumentNullException(nameof(queries));
    }

    public FoundationHost Host { get; }

    public XsrCommandRouter Commands { get; }

    public XsrQueryRouter Queries { get; }
}

/// <summary>
/// Builds the foundation runtime over an existing host: registers every foundation route
/// into fresh command/query routers and seals them.
/// </summary>
public static class FoundationRuntimeComposer
{
    private sealed class NullDispatchObserver : IXsrDispatchObserver
    {
        public static readonly NullDispatchObserver Instance = new();

        public void OnCompleted(XsrDispatchObservation observation)
        {
        }
    }

    public static FoundationRuntime Compose(
        FoundationHost host,
        IXsrDispatchObserver? observer = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(host);

        IXsrDispatchObserver dispatchObserver = observer ?? NullDispatchObserver.Instance;

        XsrCommandRouterBuilder commands = new();
        var recovery = new InstanceRecoveryService(host.SettingsPolicy, host.StateStore, host.Logging);
        commands.Register<InstanceRecoveryRestoreCommand>(InstanceRecoveryContract.Restore,
            async (command, token) => await recovery.RestoreAsync(command, token).ConfigureAwait(false));
        commands.Register<InstanceRecoveryResumeCommand>(InstanceRecoveryContract.Recover,
            async (command, token) => await recovery.RecoverAsync(command, token).ConfigureAwait(false));
        commands.Register<InstanceModEnabledCommand>(InstanceManagementContract.SetModEnabled,
            async (command, token) => await InstanceContentService.SetModEnabledAsync(command, host.StateStore, token).ConfigureAwait(false));
        commands.Register<InstanceContentRemoveCommand>(InstanceManagementContract.RemoveContent,
            async (command, token) => await InstanceContentTrash.RemoveAsync(command, host.StateStore, token).ConfigureAwait(false));
        commands.Register<InstanceContentRestoreCommand>(InstanceManagementContract.RestoreContent,
            async (command, token) => await InstanceContentTrash.RestoreAsync(command, host.StateStore, token).ConfigureAwait(false));
        commands.Register(
            FoundationRouteIds.SettingsSet,
            FoundationCommands.CreateSettingsSetHandler(host.Settings));
        commands.Register(
            FoundationRouteIds.TelemetryConsent,
            FoundationCommands.CreateTelemetryConsentHandler(host.Telemetry));
        commands.Register(
            FoundationRouteIds.AccountUpsertProfile,
            FoundationCommands.CreateAccountUpsertHandler(host.Accounts));
        commands.Register(
            FoundationRouteIds.AccountSelectProfile,
            FoundationCommands.CreateAccountSelectHandler(host.Accounts));
        commands.Register(FoundationRouteIds.AccountRemoveProfile, FoundationCommands.CreateAccountRemoveHandler(host.Accounts));
        commands.Register<SettingsMutation>(SettingsPolicyContract.SetCommand,
            (command, token) => new(Task.Run(() => host.SettingsPolicy.Set(command), token)));
        commands.Register<SettingsBatchCommand>(SettingsPolicyContract.BatchCommand,
            (command, token) => new(Task.Run(() => host.SettingsPolicy.SetBatch(command), token)));
        commands.Register<SettingsImportCommand>(SettingsPolicyContract.ImportCommand,
            (command, token) => new(Task.Run(() => host.SettingsPolicy.ApplyImport(command), token)));
        commands.Register<MachineCapabilityRefresh>(MachineCapabilityStateContract.RefreshCommand, async (command, token) =>
        {
            await host.MachineCapabilities.ReadAsync(refresh: true, cancellationToken: token).ConfigureAwait(false);
            return Nexa.Xsr.XsrResult.Success();
        });
        commands.Register<RemediationRequest>(MachineCapabilityStateContract.RemediationCommand, async (request, token) =>
        {
            RemediationResult result = await host.Remediations.ExecuteAsync(request, token).ConfigureAwait(false);
            if (result.Succeeded)
            {
                await host.MachineCapabilities.ReadAsync(refresh: true, cancellationToken: token)
                    .ConfigureAwait(false);
            }
            return result.Succeeded
                ? Nexa.Xsr.XsrResult.Success()
                : Nexa.Xsr.XsrResult.Failure(new Nexa.Xsr.XsrError(
                    Nexa.Xsr.XsrErrorKind.Rejected,
                    Nexa.Xsr.XsrSemanticId.Parse("machine.capabilities.remediation.rejected"),
                    result.Message));
        });
        XsrCommandRouter commandRouter = commands.Build(dispatchObserver, timeProvider);

        XsrQueryRouterBuilder queries = new();
        queries.Register<InstanceManagementQuery, InstanceManagementSnapshot>(InstanceManagementContract.Query,
            async (query, token) =>
            {
                var snapshot = await InstanceManagementService.ReadAsync(query, token).ConfigureAwait(false);
                if (query.IncludeRecoveryStorage)
                {
                    var comparison = await recovery.ReadAsync(new(query.InstanceDirectory), token).ConfigureAwait(false);
                    snapshot = snapshot with
                    {
                        RecoveryComparison = comparison.IsSuccess ? comparison.Value :
                        new(query.InstanceDirectory, null, null, [], "", "暂时无法比较更改，请刷新重试。")
                    };
                }
                return Nexa.Xsr.XsrResult.Success(snapshot);
            });
        queries.Register<InstanceRecoveryQuery, InstanceRecoveryReport>(InstanceRecoveryContract.Query,
            async (query, token) => await recovery.ReadAsync(query, token).ConfigureAwait(false));
        queries.Register(
            FoundationRouteIds.SettingsGet,
            FoundationQueries.CreateSettingsGetHandler(host.Settings));
        queries.Register<SettingsCatalogQuery, SettingsCatalogSnapshot>(SettingsPolicyContract.CatalogQuery,
            (query, token) => ValueTask.FromResult(Nexa.Xsr.XsrResult.Success(SettingsCatalog.Read(query))));
        queries.Register<SettingsEffectiveQuery, SettingsEffectiveSnapshot>(SettingsPolicyContract.EffectiveQuery,
            (query, token) => ValueTask.FromResult(host.SettingsPolicy.Read(query)));
        queries.Register<SettingsPreviewQuery, SettingsEffectiveSnapshot>(SettingsPolicyContract.PreviewQuery,
            (query, token) => ValueTask.FromResult(host.SettingsPolicy.Preview(query)));
        queries.Register<SettingsExportQuery, string>(SettingsPolicyContract.ExportQuery,
            (query, token) => ValueTask.FromResult(host.SettingsPolicy.Export(query)));
        queries.Register<SettingsImportQuery, SettingsImportPreview>(SettingsPolicyContract.ImportPreviewQuery,
            (query, token) => ValueTask.FromResult(Nexa.Xsr.XsrResult.Success(host.SettingsPolicy.PreviewImport(query))));
        queries.Register<MachineCapabilityQuery, MachineCapabilitySnapshot>(MachineCapabilityStateContract.SnapshotQuery,
            async (query, token) => Nexa.Xsr.XsrResult.Success(await host.MachineCapabilities.ReadAsync(query, cancellationToken: token).ConfigureAwait(false)));
        queries.Register<LaunchPreflightQuery, CapabilityPreflightReport>(MachineCapabilityStateContract.PreflightQuery,
            (query, token) =>
            {
                token.ThrowIfCancellationRequested();
                ArgumentNullException.ThrowIfNull(query.Snapshot);
                return ValueTask.FromResult(Nexa.Xsr.XsrResult.Success(
                    CapabilityPreflightEngine.Evaluate(query.Snapshot)));
            });
        XsrQueryRouter queryRouter = queries.Build(dispatchObserver, timeProvider);

        return new FoundationRuntime(host, commandRouter, queryRouter);
    }
}
