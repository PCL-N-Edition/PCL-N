using PCL.Services.Accounts;

namespace PCL.Services.Tests;

internal static partial class Program
{
    private static void ProfileImportIsValidatedDeduplicatedAndDurable()
    {
        ThrowingProfilePort port = new();
        AccountService accounts = CreateAccountService(port);
        AssertTrue(accounts.AddProfile(SampleProfile("Existing", "existing-token")).IsSuccess);
        long before = accounts.StateStore.ReadCollection<LaunchProfileView>(accounts.StateStore.Resolve(AccountService.ProfilesKey)).Revision;
        AssertFalse(accounts.ImportProfiles([SampleProfile("New"), SampleProfile("")]).IsSuccess);
        AssertEqual(1, accounts.GetViews().Count);
        AssertEqual(before, accounts.StateStore.ReadCollection<LaunchProfileView>(accounts.StateStore.Resolve(AccountService.ProfilesKey)).Revision);
        port.SaveShouldThrow = true;
        AssertFalse(accounts.ImportProfiles([SampleProfile("New")]).IsSuccess);
        AssertEqual(1, accounts.GetViews().Count);
        AssertEqual(0, accounts.SelectedIndex);
        port.SaveShouldThrow = false;
        AssertEqual(1, accounts.ImportProfiles([SampleProfile("Existing", "must-not-overwrite"), SampleProfile("New"), SampleProfile("New")]).Value);
        AssertEqual("existing-token", accounts.GetProfile(0).Value!.AccessToken);
        AssertEqual(2, accounts.GetViews().Count);
        AssertEqual(0, accounts.ImportProfiles([SampleProfile("New")]).Value);
    }

    private static async ValueTask LegacyImportNeverRepairsOrWritesSource()
    {
        string directory = CreateTempDirectory();
        try
        {
            string source = Path.Combine(directory, "legacy.json");
            new LaunchProfileFilePort(source).Save(new LaunchProfileSet { Profiles = [SampleProfile("Imported", "private-token")] });
            byte[] original = File.ReadAllBytes(source);
            LegacyProfileImport discovery = new(() => [source, source, Path.Combine(directory, "absent.json")]);
            AssertEqual(1, discovery.Discover().Count);
            AssertEqual(1, (await LegacyProfileImport.ReadAsync(source, default)).Count);
            AssertTrue(original.SequenceEqual(File.ReadAllBytes(source)));
            foreach (string malformed in new[] { "{broken", "{\"schemaVersion\":999,\"profiles\":[]}" })
            {
                File.WriteAllText(source, malformed);
                bool failed = false;
                try { await LegacyProfileImport.ReadAsync(source, default); }
                catch (Exception failure) when (failure is System.Text.Json.JsonException or InvalidDataException) { failed = true; }
                AssertTrue(failed);
                AssertEqual(malformed, File.ReadAllText(source));
                AssertEqual(1, Directory.GetFiles(directory).Length); // no .invalid backup or repairs in old data
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void AccountAuthorizationUrlsStayOnProviderOrigins()
    {
        AssertTrue(AccountOnboardingService.IsVerificationUri(AccountLoginProvider.Microsoft, "https://www.microsoft.com/link"));
        AssertTrue(AccountOnboardingService.IsVerificationUri(AccountLoginProvider.LittleSkin, "https://open.littleskin.cn/device"));
        foreach (string url in new[] { "http://www.microsoft.com/link", "https://www.microsoft.com.evil.example/link",
            "https://user:password@www.microsoft.com/link", "https://www.microsoft.com:444/link", "file:///tmp/profile" })
            AssertFalse(AccountOnboardingService.IsVerificationUri(AccountLoginProvider.Microsoft, url));
        AssertFalse(new AccountLoginStartCommand(AccountLoginProvider.ThirdParty, "name", "server", "private-password")
            .ToString()!.Contains("private-password", StringComparison.Ordinal));
    }
}
