using PCL.Xsr;
using PCL.Xsr.State;

namespace PCL.Services.Accounts;

public enum AccountLoginProvider { Offline, Microsoft, LittleSkin, ThirdParty }
public enum AccountLoginPhase { Idle, Starting, AwaitingAuthorization, ChoosingProfile, Saving, Completed, Cancelled, Failed }

/// <summary>Credential-free progress only. Device polling codes and tokens never enter this record.</summary>
public sealed record AccountLoginSnapshot(
    long Generation, AccountLoginPhase Phase, string Message,
    string UserCode = "", string VerificationUri = "", double Progress = 0)
{
    public bool IsBusy => Phase is AccountLoginPhase.Starting or AccountLoginPhase.AwaitingAuthorization
        or AccountLoginPhase.ChoosingProfile or AccountLoginPhase.Saving;
}

public sealed record AccountCharacterChoice(string Uuid, string Username);
public sealed record AccountImportCandidate(string Id, string DisplayPath);

/// <summary>Trusted command payload, not public state. Avoid record-generated credential ToString output.</summary>
public sealed class AccountLoginStartCommand(AccountLoginProvider provider, string username = "", string server = "", string password = "")
{
    public AccountLoginProvider Provider { get; } = provider;
    public string Username { get; } = username;
    public string Server { get; } = server;
    public string Password { get; } = password;
}

public sealed record AccountLoginCancelCommand(long Generation);
public sealed record AccountChooseCharacterCommand(long Generation, string Uuid);
public sealed record AccountImportCommand(string Path);
public sealed record AccountDiscoverImportsCommand;

public static class AccountOnboardingState
{
    public static readonly XsrSemanticId Login = XsrSemanticId.Parse("accounts.login");
    public static readonly XsrSemanticId Characters = XsrSemanticId.Parse("accounts.login.characters");
    public static readonly XsrSemanticId Imports = XsrSemanticId.Parse("accounts.import.candidates");
    public const string Owner = "PCL.Services.AccountOnboarding";
    public static void DeclareState(XsrStateStoreBuilder builder)
    {
        builder.Cell<AccountLoginSnapshot>(Login, Owner);
        builder.Collection<AccountCharacterChoice, string>(Characters, Owner, choice => choice.Uuid);
        builder.Collection<AccountImportCandidate, string>(Imports, Owner, candidate => candidate.Id);
    }
}

public static class AccountOnboardingRoutes
{
    public static readonly XsrSemanticId Start = XsrSemanticId.Parse("accounts.login.start");
    public static readonly XsrSemanticId Cancel = XsrSemanticId.Parse("accounts.login.cancel");
    public static readonly XsrSemanticId ChooseCharacter = XsrSemanticId.Parse("accounts.login.choose-character");
    public static readonly XsrSemanticId Import = XsrSemanticId.Parse("accounts.import");
    public static readonly XsrSemanticId DiscoverImports = XsrSemanticId.Parse("accounts.import.discover");
}

public sealed record AccountOnboardingOptions(string MicrosoftClientId, LittleSkinOAuthConfiguration? LittleSkin)
{
    public static AccountOnboardingOptions FromEnvironment()
    {
        LittleSkinOAuthConfiguration? littleSkin = null;
        try { littleSkin = LittleSkinOAuthService.ResolveConfiguration(); }
        catch (InvalidOperationException) { /* Configuration errors belong to the login action, not app startup. */ }
        return new(MicrosoftMinecraftAuthService.ResolveClientId(), littleSkin);
    }
}
