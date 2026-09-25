using Nexa.Services.Logging;
using Nexa.Services.Minecraft.Launch;
using Nexa.Xsr;

namespace Nexa.Services.Accounts;

/// <summary>
/// Resolves the launch identity for one persisted profile. Account-provider specifics live
/// here — offline derivation, Microsoft token refresh, and LittleSkin game sessions — so the launch
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
/// the persisted access token is used as-is and the gap is logged. LittleSkin resolves a
/// Yggdrasil game session separately from its provider OAuth credentials.
/// </summary>
public sealed class AccountLaunchIdentityResolver(
    AccountService accounts,
    IMicrosoftMinecraftAuthService? microsoft = null,
    string? microsoftClientId = null,
    LogService? log = null,
    ILittleSkinOAuthService? littleSkin = null,
    LittleSkinOAuthConfiguration? littleSkinConfiguration = null,
    YggdrasilAuthService? yggdrasil = null) : IAccountLaunchIdentityResolver
{
    private readonly AccountService _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));

    /// <summary>Whether Microsoft session refresh is composed (service + client id present).</summary>
    public bool ComposedRefreshCapability => microsoft is not null && !string.IsNullOrWhiteSpace(microsoftClientId);

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

            case LaunchProfileKind.LittleSkin:
                return await ResolveLittleSkinAsync(accountIndex, profile, cancellationToken).ConfigureAwait(false);

            default:
                log?.Info("Account", $"Profile kind cannot launch yet kind={profile.Kind}.");
                return XsrResult.Failure<MinecraftLaunchIdentity>(AccountErrors.LaunchNotSupported(
                    profile.Kind,
                    "Authlib Injector launch preparation has not migrated for this account kind yet."));
        }
    }

    private async ValueTask<XsrResult<MinecraftLaunchIdentity>> ResolveLittleSkinAsync(int index, LaunchProfile profile, CancellationToken token)
    {
        if (yggdrasil is null || string.IsNullOrWhiteSpace(profile.Uuid))
            return XsrResult.Failure<MinecraftLaunchIdentity>(AccountErrors.LaunchNotSupported(profile.Kind, "LittleSkin launch authentication is unavailable; sign in again."));
        const string server = LittleSkinOAuthService.YggdrasilServer;
        try
        {
            bool valid = await yggdrasil.ValidateAsync(server, profile.AccessToken, profile.ClientToken, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (!valid)
            {
                LaunchProfile updated;
                if (littleSkin is not null && !string.IsNullOrWhiteSpace(profile.ProviderAccessToken))
                {
                    string providerToken = profile.ProviderAccessToken;
                    if (profile.ProviderTokenExpiresAtUnix <= DateTimeOffset.UtcNow.AddMinutes(2).ToUnixTimeSeconds())
                    {
                        if (littleSkinConfiguration is null || string.IsNullOrWhiteSpace(profile.RefreshToken))
                            throw new InvalidOperationException("LittleSkin OAuth needs a new login.");
                        LittleSkinOAuthTokens refreshed = await littleSkin.RefreshOAuthTokenAsync(littleSkinConfiguration, profile.RefreshToken, token).ConfigureAwait(false);
                        LaunchProfile refreshedProfile = profile with
                        {
                            ProviderAccessToken = refreshed.AccessToken,
                            RefreshToken = refreshed.RefreshToken,
                            ProviderTokenExpiresAtUnix = refreshed.ExpiresAt.ToUnixTimeSeconds()
                        };
                        XsrResult saved = _accounts.ReplaceProfile(index, refreshedProfile, profile);
                        if (!saved.IsSuccess) return XsrResult.Failure<MinecraftLaunchIdentity>(saved.Error!);
                        profile = refreshedProfile;
                        providerToken = refreshed.AccessToken;
                    }
                    LittleSkinMinecraftSession session = await littleSkin.CreateMinecraftSessionAsync(providerToken, profile.Uuid, token).ConfigureAwait(false);
                    updated = profile with { Username = session.Username, Uuid = session.Uuid, AccessToken = session.AccessToken, ClientToken = session.ClientToken };
                }
                else
                {
                    YggdrasilAuthLoginResult session = await yggdrasil.RefreshAsync(server, profile.AccessToken, profile.ClientToken, token).ConfigureAwait(false);
                    updated = profile with { Username = session.Username, Uuid = session.Uuid, AccessToken = session.AccessToken, ClientToken = session.ClientToken };
                }
                if (!string.Equals(updated.Uuid, profile.Uuid, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(updated.AccessToken))
                    throw new InvalidOperationException("LittleSkin returned a different character or an empty game session.");
                XsrResult persisted = _accounts.ReplaceProfile(index, updated, profile);
                if (!persisted.IsSuccess) return XsrResult.Failure<MinecraftLaunchIdentity>(persisted.Error!);
                profile = updated;
            }
            if (_accounts.GetProfile(index).Value != profile)
                return XsrResult.Failure<MinecraftLaunchIdentity>(AccountErrors.InvalidProfile("The profile changed during launch authentication."));
            return XsrResult.Success(new MinecraftLaunchIdentity(profile.Username, profile.Uuid, profile.AccessToken, MinecraftLaunchIdentityMode.ThirdParty)
            { AuthServer = server });
        }
        catch (Exception failure) when (failure is HttpRequestException or InvalidOperationException or ArgumentException)
        {
            log?.Warn("Account", "LittleSkin launch session could not be refreshed.");
            return XsrResult.Failure<MinecraftLaunchIdentity>(AccountErrors.LaunchNotSupported(profile.Kind, "LittleSkin session expired or unavailable; sign in again."));
        }
    }

    private async ValueTask<XsrResult<MinecraftLaunchIdentity>> ResolveMicrosoftAsync(
        int accountIndex,
        LaunchProfile profile,
        CancellationToken cancellationToken)
    {
        if (!_accounts.TryCaptureRefresh(accountIndex, profile, out long generation))
            return XsrResult.Failure<MinecraftLaunchIdentity>(AccountErrors.InvalidProfile("The account changed before launch authentication."));
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

            cancellationToken.ThrowIfCancellationRequested();
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
                accountIndex, profile, generation, refreshed.Username, refreshed.Uuid, refreshed.AccessToken, refreshed.RefreshToken);
            if (!persisted.IsSuccess)
            {
                if (persisted.Error?.Code != AccountErrors.PersistFailedCode)
                    return XsrResult.Failure<MinecraftLaunchIdentity>(persisted.Error!);
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
