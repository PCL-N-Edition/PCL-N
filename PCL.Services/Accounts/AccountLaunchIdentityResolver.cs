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
        if (string.IsNullOrWhiteSpace(profile.Uuid) || string.IsNullOrWhiteSpace(profile.AccessToken))
        {
            return XsrResult.Failure<MinecraftLaunchIdentity>(AccountErrors.LaunchNotSupported(
                profile.Kind,
                "the Microsoft profile has no launch UUID or access token; sign in again."));
        }

        string playerName = profile.Username;
        string playerUuid = profile.Uuid;
        string accessToken = profile.AccessToken;

        if (microsoft is null || string.IsNullOrWhiteSpace(microsoftClientId))
        {
            log?.Info("Account", "Microsoft refresh capability is not composed; using the persisted access token.");
            return XsrResult.Success(new MinecraftLaunchIdentity(
                playerName, playerUuid, accessToken, MinecraftLaunchIdentityMode.Microsoft));
        }

        if (string.IsNullOrWhiteSpace(profile.RefreshToken))
        {
            log?.Warn("Account", "The Microsoft profile has no refresh token; using the persisted access token.");
            return XsrResult.Success(new MinecraftLaunchIdentity(
                playerName, playerUuid, accessToken, MinecraftLaunchIdentityMode.Microsoft));
        }

        // The launch pipeline owns the login stage: refresh before the game starts so a server-
        // side expired access token does not fail the launch and force a manual re-login.
        log?.Info("Account", "Refreshing the Microsoft session before launch.");
        MicrosoftMinecraftLoginResult refreshed;
        try
        {
            refreshed = await microsoft
                .RefreshAsync(microsoftClientId, profile.RefreshToken, cancellationToken)
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

        XsrResult persisted = _accounts.UpdateMicrosoftTokens(accountIndex, refreshed.AccessToken, refreshed.RefreshToken);
        if (!persisted.IsSuccess)
        {
            log?.Warn("Account", $"The refreshed Microsoft session could not be persisted: {persisted.Error?.Message}");
        }

        log?.Info("Account", "Microsoft session refreshed before launch.");
        return XsrResult.Success(new MinecraftLaunchIdentity(
            playerName, playerUuid, refreshed.AccessToken, MinecraftLaunchIdentityMode.Microsoft));
    }
}
