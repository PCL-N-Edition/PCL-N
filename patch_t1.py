# -*- coding: utf-8 -*-
# P1-1: environment options survive; embedded Microsoft id wins when present.
p = r"PCL.Desktop\Program.cs"
with open(p, encoding="utf-8") as f:
    s = f.read()
old = """            options: new AccountOnboardingOptions(ResolveEmbeddedMicrosoftClientId() ?? string.Empty, null),
            observer: operationLog.Dispatch);"""
new = """            options: ComposeAccountOnboardingOptions(),
            observer: operationLog.Dispatch);"""
assert s.count(old) == 1, "options call"
s = s.replace(old, new)
old2 = """    private static string? ResolveEmbeddedMicrosoftClientId() =>"""
new2 = """    /// <summary>
    /// Merges the two account-configuration sources: environment/config-file values
    /// (Microsoft client id AND the LittleSkin OAuth configuration) are the baseline, and a
    /// publish-time embedded Microsoft client id wins over them. Composing the raw embedded
    /// value alone silently dropped LittleSkin configuration.
    /// </summary>
    private static AccountOnboardingOptions ComposeAccountOnboardingOptions()
    {
        AccountOnboardingOptions fromEnvironment = AccountOnboardingOptions.FromEnvironment();
        string? embedded = ResolveEmbeddedMicrosoftClientId();
        return fromEnvironment with
        {
            MicrosoftClientId = !string.IsNullOrWhiteSpace(embedded) ? embedded : fromEnvironment.MicrosoftClientId,
        };
    }

    private static string? ResolveEmbeddedMicrosoftClientId() =>"""
assert s.count(old2) == 1
s = s.replace(old2, new2)
with open(p, "w", encoding="utf-8", newline="") as f:
    f.write(s)
print("P1-1 options merged")
