using System.Text.Json;
using PCL.Services.Accounts;
using PCL.Xsr;
using PCL.Xsr.State;

namespace PCL.Services.Tests;

// XSR-506: account capability contract — legacy launch profile file compatibility,
// credential-free published views, and durable-first list edits.
internal static partial class Program
{
    private static LaunchProfile SampleProfile(string username = "Steve", string accessToken = "") => new()
    {
        Username = username,
        Kind = LaunchProfileKind.Offline,
        Uuid = "uuid-" + username,
        AccessToken = accessToken,
    };

    private static AccountService CreateAccountService(ILaunchProfilePort port)
    {
        XsrStateStoreBuilder builder = new();
        AccountService.DeclareState(builder);
        return new AccountService(builder.Build(), port);
    }

    internal static ValueTask ProfilePortRoundTripsLegacyJsonShape()
    {
        string directory = CreateTempDirectory();
        try
        {
            string path = Path.Combine(directory, "profiles.json");
            LaunchProfileFilePort port = new(path);
            AssertEqual(0, port.Load().Profiles.Count);

            LaunchProfile profile = new()
            {
                Username = "Alex",
                Info = "main",
                Kind = LaunchProfileKind.Microsoft,
                Uuid = "uuid-alex",
                SkinAddress = "https://example/skin.png",
                AuthServer = "https://example/auth",
                AccessToken = "access-1",
                RefreshToken = "refresh-1",
                ProviderAccessToken = "provider-1",
                ProviderTokenExpiresAtUnix = 1893456000,
                ClientToken = "client-1",
            };
            port.Save(new LaunchProfileSet { Profiles = [profile] });

            string json = File.ReadAllText(path);
            using (JsonDocument document = JsonDocument.Parse(json))
            {
                JsonElement root = document.RootElement;
                AssertEqual(1, root.GetProperty("schemaVersion").GetInt32());
                JsonElement serialized = root.GetProperty("profiles")[0];
                AssertEqual("Alex", serialized.GetProperty("username").GetString());
                AssertEqual("Microsoft", serialized.GetProperty("kind").GetString());
                AssertEqual("access-1", serialized.GetProperty("accessToken").GetString());
                AssertEqual("provider-1", serialized.GetProperty("providerAccessToken").GetString());
                AssertEqual(1893456000, serialized.GetProperty("providerTokenExpiresAtUnix").GetInt64());
                AssertEqual("lucide/user", serialized.GetProperty("svgIcon").GetString());
            }

            LaunchProfileSet loaded = port.Load();
            AssertEqual(1, loaded.Profiles.Count);
            LaunchProfile restored = loaded.Profiles[0];
            AssertTrue(restored == profile);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    internal static ValueTask ProfilePortQuarantinesUnreadableFiles()
    {
        string directory = CreateTempDirectory();
        try
        {
            string path = Path.Combine(directory, "profiles.json");
            const string corrupt = """{ "schemaVersion": 2, "profiles": [] }""";
            File.WriteAllText(path, corrupt);

            LaunchProfileFilePort port = new(path);
            bool threw = false;
            try
            {
                port.Load();
            }
            catch (IOException)
            {
                threw = true;
            }

            AssertTrue(threw);
            AssertTrue(File.Exists(port.QuarantinePath));
            AssertTrue(File.ReadAllText(port.QuarantinePath).Contains("\"schemaVersion\": 2", StringComparison.Ordinal));

            AccountService service = CreateAccountService(port);
            AssertTrue(service.LoadError is not null);
            AssertEqual(AccountErrors.PersistFailedCode, service.LoadError!.Code);
            AssertEqual(0, service.GetViews().Count);

            XsrStateId id = service.StateStore.Resolve(AccountService.ProfilesKey);
            XsrCollectionSnapshot<LaunchProfileView> state = service.StateStore.ReadCollection<LaunchProfileView>(id);
            AssertEqual(XsrStateAvailability.Unavailable, state.Availability);

            // The first successful write heals the store and restores availability.
            XsrResult<int> added = service.AddProfile(SampleProfile());
            AssertTrue(added.TryGetValue(out int index) && index == 0);
            AssertEqual(XsrStateAvailability.Available, service.StateStore.ReadCollection<LaunchProfileView>(id).Availability);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    internal static ValueTask ProfilesPersistAcrossRestarts()
    {
        string directory = CreateTempDirectory();
        try
        {
            string path = Path.Combine(directory, "profiles.json");
            AccountService first = CreateAccountService(new LaunchProfileFilePort(path));
            AssertTrue(first.AddProfile(SampleProfile("Steve", "tok-1")).TryGetValue(out int steve) && steve == 0);
            AssertTrue(first.AddProfile(new LaunchProfile
            {
                Username = "Herobrine",
                Kind = LaunchProfileKind.ThirdParty,
                Uuid = "uuid-hb",
                AuthServer = "https://example/auth",
            }).TryGetValue(out int herobrine) && herobrine == 1);

            AccountService restarted = CreateAccountService(new LaunchProfileFilePort(path));
            IReadOnlyList<LaunchProfileView> views = restarted.GetViews();
            AssertEqual(2, views.Count);
            AssertTrue(views[0].Username == "Steve" && views[0].Kind == LaunchProfileKind.Offline);
            AssertTrue(views[1].Username == "Herobrine" && views[1].Kind == LaunchProfileKind.ThirdParty);

            AssertTrue(restarted.ReplaceProfile(1, SampleProfile("Notch")).IsSuccess);
            AssertTrue(restarted.GetViews()[1].Username == "Notch");
            AssertTrue(restarted.RemoveProfile(0).IsSuccess);
            IReadOnlyList<LaunchProfileView> afterRemove = restarted.GetViews();
            AssertEqual(1, afterRemove.Count);
            AssertEqual(0, afterRemove[0].Index);
            AssertEqual("Notch", afterRemove[0].Username);

            AccountService final = CreateAccountService(new LaunchProfileFilePort(path));
            IReadOnlyList<LaunchProfileView> finalViews = final.GetViews();
            AssertEqual(1, finalViews.Count);
            AssertEqual("Notch", finalViews[0].Username);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    internal static ValueTask InvalidProfilesAndIndexesAreRejectedStably()
    {
        string directory = CreateTempDirectory();
        try
        {
            string path = Path.Combine(directory, "profiles.json");
            AccountService service = CreateAccountService(new LaunchProfileFilePort(path));

            XsrResult<int> noUsername = service.AddProfile(SampleProfile(username: "  "));
            AssertFalse(noUsername.IsSuccess);
            AssertEqual(AccountErrors.InvalidProfileCode, noUsername.Error!.Code);
            AssertEqual(XsrErrorKind.Rejected, noUsername.Error.Kind);

            XsrResult<int> badKind = service.AddProfile(SampleProfile() with { Kind = (LaunchProfileKind)42 });
            AssertFalse(badKind.IsSuccess);
            AssertEqual(AccountErrors.InvalidProfileCode, badKind.Error!.Code);

            XsrResult missing = service.ReplaceProfile(3, SampleProfile());
            AssertFalse(missing.IsSuccess);
            AssertEqual(AccountErrors.ProfileNotFoundCode, missing.Error!.Code);
            AssertEqual(XsrErrorKind.NotFound, missing.Error.Kind);

            XsrResult missingRemove = service.RemoveProfile(-1);
            AssertFalse(missingRemove.IsSuccess);
            AssertEqual(AccountErrors.ProfileNotFoundCode, missingRemove.Error!.Code);

            AssertEqual(0, service.GetViews().Count);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    internal static void ProviderIdEqualityIsCaseInsensitive()
    {
        AccountProviderId upper = AccountProviderId.Parse("Microsoft");
        AccountProviderId lower = AccountProviderId.Parse("microsoft");

        AssertTrue(upper == lower);
        AssertFalse(upper != lower);
        AssertTrue(upper.Equals(lower));
        AssertTrue(upper.Equals("MICROSOFT"));
        AssertFalse(upper.Equals("littleskin"));
        AssertTrue(upper.GetHashCode() == lower.GetHashCode());
        AssertTrue(upper.Equals((object)lower));
        AssertEqual("Microsoft", upper.ToString());
    }

    internal static ValueTask FailedSavesChangeNothingObservable()
    {
        string directory = CreateTempDirectory();
        try
        {
            ThrowingProfilePort port = new();
            AccountService service = CreateAccountService(port);
            AssertEqual(-1, service.StateStore.Read<int>(service.StateStore.Resolve(AccountService.SelectedKey)).Value);
            AssertTrue(service.AddProfile(SampleProfile("Steve")).TryGetValue(out int index) && index == 0);

            port.SaveShouldThrow = true;
            XsrResult<int> failed = service.AddProfile(SampleProfile("Villager"));
            AssertFalse(failed.IsSuccess);
            AssertEqual(AccountErrors.PersistFailedCode, failed.Error!.Code);
            AssertEqual(1, service.GetViews().Count);
            AssertTrue(service.GetViews()[0].Username == "Steve");
            AssertEqual(0, service.SelectedIndex);
            AssertEqual(0, service.StateStore.Read<int>(service.StateStore.Resolve(AccountService.SelectedKey)).Value);

            AssertFalse(service.RemoveProfile(0).IsSuccess);
            AssertEqual(0, service.SelectedIndex);

            port.SaveShouldThrow = false;
            AssertTrue(service.RemoveProfile(0).IsSuccess);
            AssertEqual(0, service.GetViews().Count);
            AssertEqual(-1, service.SelectedIndex);
            AssertEqual(-1, service.StateStore.Read<int>(service.StateStore.Resolve(AccountService.SelectedKey)).Value);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    private sealed class ThrowingProfilePort : ILaunchProfilePort
    {
        public bool SaveShouldThrow { get; set; }

        private List<LaunchProfile> Profiles { get; set; } = [];

        public LaunchProfileSet Load() => new() { Profiles = Profiles };

        public void Save(LaunchProfileSet profiles)
        {
            if (SaveShouldThrow)
            {
                throw new IOException("simulated save failure");
            }

            Profiles = [.. profiles.Profiles];
        }
    }
}
