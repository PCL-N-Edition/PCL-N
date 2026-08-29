using PCL.Services.Accounts;
using PCL.Services.Settings;
using PCL.Services.Telemetry;
using PCL.Xsr;

namespace PCL.Services.Foundation;

/// <summary>One foundation command: set one setting to a raw schema-encoded value.</summary>
public sealed record SettingsSetCommand(string Key, string Value);

/// <summary>One foundation command: grant or revoke telemetry consent.</summary>
public sealed record TelemetryConsentCommand(bool Consent);

/// <summary>One foundation command: insert or replace one launch profile in the roster.</summary>
public sealed record AccountUpsertProfileCommand(LaunchProfile Profile);

/// <summary>
/// Foundation command handlers, expressed against the XSR handler delegates. PCL.Services
/// cannot reference the runtime's router (dependency direction), so these handlers are what
/// the composition root registers into the real <c>XsrCommandRouterBuilder</c> — one semantic
/// id, one handler, one service call, one state publication.
/// </summary>
public static class FoundationCommands
{
    /// <summary>Sets one setting by semantic key with a raw schema-encoded value.</summary>
    public static XsrCommandHandler<SettingsSetCommand> CreateSettingsSetHandler(SettingsService settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return (command, _) =>
        {
            XsrResult result = settings.SetValue(command.Key, command.Value);
            return ValueTask.FromResult(result);
        };
    }

    /// <summary>Grants or revokes telemetry consent.</summary>
    public static XsrCommandHandler<TelemetryConsentCommand> CreateTelemetryConsentHandler(TelemetryService telemetry) =>
        (command, _) =>
        {
            ArgumentNullException.ThrowIfNull(telemetry);
            telemetry.Consent = command.Consent;
            return ValueTask.FromResult(XsrResult.Success());
        };

    /// <summary>Inserts or replaces one launch profile in the persisted roster.</summary>
    public static XsrCommandHandler<AccountUpsertProfileCommand> CreateAccountUpsertHandler(AccountService accounts)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        return (command, _) =>
        {
            XsrResult<int> result = AccountLoginProfiles.Upsert(accounts, command.Profile);
            return ValueTask.FromResult(result.IsSuccess
                ? XsrResult.Success()
                : XsrResult.Failure(result.Error!));
        };
    }
}

/// <summary>
/// Foundation query handlers. Queries are for one-time reads; continuously read state goes
/// through the state store instead.
/// </summary>
public static class FoundationQueries
{
    /// <summary>Reads one setting's raw schema-encoded value by semantic key.</summary>
    public static XsrQueryHandler<SettingsGetQuery, string> CreateSettingsGetHandler(SettingsService settings) =>
        (query, _) =>
        {
            ArgumentNullException.ThrowIfNull(settings);
            XsrResult<string> result = settings.GetValue<string>(query.Key);
            return ValueTask.FromResult(result.IsSuccess
                ? XsrResult.Success(result.Value)
                : XsrResult.Failure<string>(result.Error!));
        };
}

/// <summary>One foundation query: read one setting's raw value.</summary>
public sealed record SettingsGetQuery(string Key);
