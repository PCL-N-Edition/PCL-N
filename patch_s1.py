# -*- coding: utf-8 -*-
# P1-1: one Microsoft service instance shared by onboarding and the launch resolver.
p = "PCL.Services.Composition/Composition/AccountOnboardingRuntime.cs"
s = open(p, encoding="utf-8").read()
old = """        HttpClient http = client ?? new HttpClient(new DiagnosticHttpHandler(host.Logging,
            new HttpClientHandler { AllowAutoRedirect = false }))
        { Timeout = TimeSpan.FromSeconds(30) };
        AccountOnboardingService service = new(host.Accounts, microsoft ?? new MicrosoftMinecraftAuthService(http, log: host.Logging),
            littleSkin ?? new LittleSkinOAuthService(http, host.Logging), new YggdrasilAuthService(http, host.Logging),
            options ?? AccountOnboardingOptions.FromEnvironment(), imports, host.Logging);"""
new = """        HttpClient http = client ?? new HttpClient(new DiagnosticHttpHandler(host.Logging,
            new HttpClientHandler { AllowAutoRedirect = false }))
        { Timeout = TimeSpan.FromSeconds(30) };
        // One instance, two consumers: the onboarding service and the launch resolver MUST
        // share this capability, or production silently loses Microsoft refresh.
        IMicrosoftMinecraftAuthService microsoftService =
            microsoft ?? new MicrosoftMinecraftAuthService(http, log: host.Logging);
        AccountOnboardingOptions resolvedOptions = options ?? AccountOnboardingOptions.FromEnvironment();
        AccountOnboardingService service = new(host.Accounts, microsoftService,
            littleSkin ?? new LittleSkinOAuthService(http, host.Logging), new YggdrasilAuthService(http, host.Logging),
            resolvedOptions, imports, host.Logging);"""
assert s.count(old) == 1, "composer share"
s = s.replace(old, new)
old2 = """        IAccountLaunchIdentityResolver resolver = new AccountLaunchIdentityResolver(
            host.Accounts,
            microsoft,
            (options ?? AccountOnboardingOptions.FromEnvironment()).MicrosoftClientId,
            host.Logging);
        return new(service, commands.Build(observer ?? new Observer()), client is null ? http : null, skins, resolver);"""
new2 = """        IAccountLaunchIdentityResolver resolver = new AccountLaunchIdentityResolver(
            host.Accounts,
            microsoftService,
            resolvedOptions.MicrosoftClientId,
            host.Logging);
        return new(service, commands.Build(observer ?? new Observer()), client is null ? http : null, skins, resolver);"""
assert s.count(old2) == 1, "resolver share"
s = s.replace(old2, new2)
open(p, "w", encoding="utf-8", newline="\n").write(s)
print("P1-1 shared instance")

# Resolver: expose the capability for composition regressions.
p2 = "PCL.Services/Accounts/AccountLaunchIdentityResolver.cs"
s2 = open(p2, encoding="utf-8").read()
old3 = "    private readonly AccountService _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));"
new3 = """    private readonly AccountService _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));

    /// <summary>Whether Microsoft session refresh is composed (service + client id present).</summary>
    public bool ComposedRefreshCapability => microsoft is not null && !string.IsNullOrWhiteSpace(microsoftClientId);"""
assert s2.count(old3) == 1
s2 = s2.replace(old3, new3)
open(p2, "w", encoding="utf-8", newline="\n").write(s2)
print("capability exposed")
