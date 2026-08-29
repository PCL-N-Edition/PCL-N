using PCL.Xsr;

namespace PCL.Services.Accounts;

/// <summary>
/// Maps online login outcomes onto the persisted launch profile roster: a login result
/// becomes (or replaces) a <see cref="LaunchProfile"/> with the credentials it carried, while
/// the published roster views stay credential-free. LittleSkin servers are recognized by
/// host so the profile kind matches what the legacy launcher stored.
/// </summary>
public static class AccountLoginProfiles
{
    /// <summary>Builds the Microsoft profile for a login result.</summary>
    public static LaunchProfile FromMicrosoft(MicrosoftMinecraftLoginResult login) => new()
    {
        Username = login.Username,
        Kind = LaunchProfileKind.Microsoft,
        Uuid = login.Uuid,
        SkinAddress = login.SkinAddress,
        AccessToken = login.AccessToken,
        RefreshToken = login.RefreshToken,
    };

    /// <summary>Builds the Yggdrasil profile for a login result; LittleSkin hosts become LittleSkin profiles.</summary>
    public static LaunchProfile FromYggdrasil(YggdrasilAuthLoginResult login) => new()
    {
        Username = login.Username,
        Kind = IsLittleSkinServer(login.AuthServer) ? LaunchProfileKind.LittleSkin : LaunchProfileKind.ThirdParty,
        Uuid = login.Uuid,
        AuthServer = login.AuthServer,
        AccessToken = login.AccessToken,
        RefreshToken = login.RefreshToken,
        ClientToken = login.ClientToken,
    };

    /// <summary>
    /// Replaces the roster entry identified by profile kind + uuid with the login result,
    /// appending a new profile when no entry matches.
    /// </summary>
    public static XsrResult<int> Upsert(AccountService accounts, LaunchProfile profile)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        foreach (LaunchProfileView view in accounts.GetViews())
        {
            if (view.Kind == profile.Kind
                && string.Equals(view.Uuid, profile.Uuid, StringComparison.OrdinalIgnoreCase)
                && view.Uuid.Length > 0)
            {
                XsrResult replaced = accounts.ReplaceProfile(view.Index, profile);
                return replaced.IsSuccess
                    ? XsrResult.Success(view.Index)
                    : XsrResult.Failure<int>(replaced.Error!);
            }
        }

        return accounts.AddProfile(profile);
    }

    /// <summary>Whether a Yggdrasil server is the LittleSkin host.</summary>
    public static bool IsLittleSkinServer(string? authServer) =>
        (authServer ?? string.Empty).Contains("littleskin", StringComparison.OrdinalIgnoreCase);
}
