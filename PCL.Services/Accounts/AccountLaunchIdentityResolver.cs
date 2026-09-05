using PCL.Services.Logging;
using PCL.Services.Minecraft.Launch;
using PCL.Xsr;

namespace PCL.Services.Accounts;

/// <summary>
/// Resolves the launch identity for one persisted profile. Account-provider specifics live
/// here — offline derivation, Microsoft token refresh, and the honest refusal for provider
/// kinds whose launch preparation (Authlib Injector) has not migrated — so the launch
/// coordinator never grows provider-specific branches.
/// </summary>
public interface IAccountLaunchIdentityResolver
{
    ValueTask<XsrResult<MinecraftLaunchIdentity>> ResolveAsync(
        int accountIndex,
        LaunchProfile profile,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Default resolver. Microsoft profiles refresh through the injected auth service when one is
/// composed (the refreshed credentials persist back into the roster); without that capability
/// the persisted access token is used as-is and the gap is logged. LittleSkin, third-party
/// Authlib Injector, and NCloud profiles refuse with a stable error until their launch
/// preparation migrates.
/// </summary>
public sealed class AccountLaunchIdentityResolver(
    AccountService accounts,
    IMicrosoftMinecraftAuthService? microsoft = null,
    string? microsoftClientId = null,
    LogService? log = null) : IAccountLaunchIdentityResolver
{
    private readonly AccountService _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));

    public async ValueTask<XsrResult<MinecraftLaunchIdentity>> ResolveAsync(
        int accountIndex,
        LaunchProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        switch (profile.Kind)
        {
            case LaunchProfileKind.Offline:
                (string offlineName, string offlineUuid) = MinecraftOfflineIdentity.Resolve(profile.Username, profile.Uuid);
                return XsrResult.Success(new MinecraftLaunchIdentity(
                    offlineName, offlineUuid, "0", MinecraftLaunchIdentityMode.Offline));

            case LaunchProfileKind.Microsoft:
                return await ResolveMicrosoftAsync(accountIndex, profile, cancellationToken).ConfigureAwait(false);

            default:
                log?.Info("Account", $"Profile kind cannot launch yet kind={profile.Kind}.");
                return XsrResult.Failure<MinecraftLaunchIdentity>(AccountErrors.LaunchNotSupported(
                    profile.Kind,
                    "Authlib Injector launch preparation has not migrated for this account kind yet."));
        }
    }

    private async ValueTask<XsrResult<MinecraftLaunchIdentity>> ResolveMicrosoftAsync(
        int accountIndex,
        LaunchProfile profile,
        CancellationToken cancellationToken)
    {
        // Refresh comes first: a valid refresh token can restore an expired or missing access
        // token, so demanding complete persisted credentials beforehand would force a manual
        // re-login that the refresh chain can avoid.
        bool canRefresh = microsoft is not null && !string.IsNullOrWhiteSpace(microsoftClientId);
        if (canRefresh && !string.IsNullOrWhiteSpace(profile.RefreshToken))
        {
            log?.Info("Account", "Refreshing the Microsoft session before launch.");
            MicrosoftMinecraftLoginResult refreshed;
            try
            {
                refreshed = await microsoft!
                    .RefreshAsync(microsoftClientId!, profile.RefreshToken, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is InvalidOperationException or HttpRequestException)
            {
                log?.Warn("Account", $"Microsoft refresh failed: {exception.Message}");
                return XsrResult.Failure<MinecraftLaunchIdentity>(AccountErrors.LaunchNotSupported(
                    profile.Kind,
                    "the Microsoft session could not be refreshed; sign in again."));
            }

            if (!refreshed.OwnsMinecraft || string.IsNullOrWhiteSpace(refreshed.AccessToken))
            {
                log?.Warn("Account", "The refreshed Microsoft session carries no Minecraft entitlement.");
                return XsrResult.Failure<MinecraftLaunchIdentity>(AccountErrors.LaunchNotSupported(
                    profile.Kind,
                    "the refreshed Microsoft session carries no Minecraft entitlement; sign in again."));
            }

            // Prefer the refreshed identity (the player may have renamed); persist the rotated
            // credentials so the next launch refreshes from the newest refresh token.
            XsrResult persisted = _accounts.UpdateMicrosoftProfile(
                accountIndex, refreshed.Username, refreshed.Uuid, refreshed.AccessToken, refreshed.RefreshToken);
            if (!persisted.IsSuccess)
            {
                log?.Warn("Account", $"The refreshed Microsoft session could not be persisted: {persisted.Error?.Message}");
            }

            log?.Info("Account", "Microsoft session refreshed before launch.");
            return XsrResult.Success(new MinecraftLaunchIdentity(
                string.IsNullOrWhiteSpace(refreshed.Username) ? profile.Username : refreshed.Username,
                string.IsNullOrWhiteSpace(refreshed.Uuid) ? profile.Uuid : refreshed.Uuid,
                refreshed.AccessToken,
                MinecraftLaunchIdentityMode.Microsoft));
        }

        if (microsoft is null || string.IsNullOrWhiteSpace(microsoftClientId))
        {
            log?.Info("Account", "Microsoft refresh capability is not composed; using the persisted access token.");
        }
        else
        {
            log?.Warn("Account", "The Microsoft profile has no refresh token; using the persisted access token.");
        }

        if (string.IsNullOrWhiteSpace(profile.Uuid) || string.IsNullOrWhiteSpace(profile.AccessToken))
        {
            return XsrResult.Failure<MinecraftLaunchIdentity>(AccountErrors.LaunchNotSupported(
                profile.Kind,
                "the Microsoft profile has no launch UUID or access token; sign in again."));
        }

        return XsrResult.Success(new MinecraftLaunchIdentity(
            profile.Username, profile.Uuid, profile.AccessToken, MinecraftLaunchIdentityMode.Microsoft));
    }
}
