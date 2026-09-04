using PCL.Services.Accounts;
using PCL.Services.Foundation;
using PCL.Services.Settings;
using PCL.Services.Telemetry;
using PCL.Xsr.Runtime;

namespace PCL.Services.Composition;

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
        XsrCommandRouter commandRouter = commands.Build(dispatchObserver, timeProvider);

        XsrQueryRouterBuilder queries = new();
        queries.Register(
            FoundationRouteIds.SettingsGet,
            FoundationQueries.CreateSettingsGetHandler(host.Settings));
        XsrQueryRouter queryRouter = queries.Build(dispatchObserver, timeProvider);

        return new FoundationRuntime(host, commandRouter, queryRouter);
    }
}
