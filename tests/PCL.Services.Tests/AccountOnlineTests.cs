using System.Net;
using System.Text;
using PCL.Services.Accounts;
using PCL.Xsr;

namespace PCL.Services.Tests;

// XSR-513: online account flows — the Microsoft device-code chain and the Yggdrasil
// authenticate/validate/refresh service, fixture-driven through a stub handler and wired
// into the persisted roster.
internal static partial class Program
{
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, Queue<HttpResponseMessage>> _responses = new(StringComparer.Ordinal);

        public List<string> Requests { get; } = [];

        public void Serve(string url, string body, HttpStatusCode code = HttpStatusCode.OK)
        {
            if (!_responses.TryGetValue(url, out Queue<HttpResponseMessage>? queue))
            {
                queue = new Queue<HttpResponseMessage>();
                _responses[url] = queue;
            }

            queue.Enqueue(new HttpResponseMessage(code)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.Method + " " + request.RequestUri);
            if (_responses.TryGetValue(request.RequestUri!.ToString(), out Queue<HttpResponseMessage>? queue)
                && queue.Count > 0)
            {
                return Task.FromResult(queue.Dequeue());
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class ProgressCollector : IProgress<double>
    {
        public List<double> Values { get; } = [];

        public void Report(double value) => Values.Add(value);
    }

    internal static async ValueTask MicrosoftDeviceLoginRunsTheFullChain()
    {
        PCL.Services.Logging.LogService log = CreateLogService();
        ScriptedHandler handler = new();
        List<TimeSpan> waits = [];
        MicrosoftMinecraftAuthService service = new(
            new HttpClient(handler),
            (delay, _) =>
            {
                waits.Add(delay);
                return Task.CompletedTask;
            }, log);
        string deviceUrl = "https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode";
        string tokenUrl = "https://login.microsoftonline.com/consumers/oauth2/v2.0/token";
        handler.Serve(deviceUrl, """
            {"device_code":"DEV1","user_code":"ABCD-1234","verification_uri":"https://microsoft.com/link","expires_in":900,"interval":5}
            """);
        handler.Serve(tokenUrl, """{"error":"authorization_pending"}""");
        handler.Serve(tokenUrl, """{"error":"slow_down"}""");
        handler.Serve(tokenUrl, """{"access_token":"MS-AT","refresh_token":"MS-RT"}""");
        handler.Serve("https://user.auth.xboxlive.com/user/authenticate",
            """{"Token":"XBL-T","DisplayClaims":{"xui":[{"uhs":"UHS-1"}]}}""");
        handler.Serve("https://xsts.auth.xboxlive.com/xsts/authorize",
            """{"Token":"XSTS-T","DisplayClaims":{"xui":[{"uhs":"UHS-2"}]}}""");
        handler.Serve("https://api.minecraftservices.com/authentication/login_with_xbox",
            """{"access_token":"MC-AT"}""");
        handler.Serve("https://api.minecraftservices.com/minecraft/profile",
            """{"name":"Steve","id":"uuid-steve","skins":[{"state":"ACTIVE","url":"https://skin/steve"}]}""");
        handler.Serve("https://api.minecraftservices.com/entitlements/mcstore",
            """{"items":[{"name":"product_minecraft"}]}""");

        MicrosoftDeviceCodeInfo device = await service.RequestDeviceCodeAsync("client-id");
        AssertEqual("DEV1", device.DeviceCode);
        AssertEqual("ABCD-1234", device.UserCode);
        AssertEqual(TimeSpan.FromSeconds(5), device.PollInterval);

        ProgressCollector progress = new();
        MicrosoftMinecraftLoginResult result = await service.CompleteDeviceLoginAsync(
            "client-id", device, progress);

        AssertEqual("Steve", result.Username);
        AssertEqual("uuid-steve", result.Uuid);
        AssertEqual("MC-AT", result.AccessToken);
        AssertEqual("MS-RT", result.RefreshToken);
        AssertEqual("https://skin/steve", result.SkinAddress);
        AssertTrue(result.OwnsMinecraft);
        AssertTrue(waits.Count >= 3);
        AssertEqual(TimeSpan.FromSeconds(10), waits[2]); // slow_down takes effect on the next poll
        AssertTrue(progress.Values.Count > 0);
        AssertTrue(handler.Requests.Any(request => request.Contains("devicecode", StringComparison.Ordinal)));
        string diagnostic = DiagnosticText(log);
        foreach (string stage in new[] { "xbox_live", "xsts", "minecraft_authentication", "minecraft_profile", "minecraft_ownership" })
            AssertTrue(diagnostic.Contains($"stage={stage}", StringComparison.Ordinal));
        foreach (string secret in new[] { "DEV1", "ABCD-1234", "MS-AT", "MS-RT", "XBL-T", "XSTS-T", "MC-AT" })
            AssertFalse(diagnostic.Contains(secret, StringComparison.Ordinal));
    }

    internal static async ValueTask MicrosoftDeclinedAndExpiredAreDistinctErrors()
    {
        ScriptedHandler handler = new();
        MicrosoftMinecraftAuthService service = new(new HttpClient(handler), (_, _) => Task.CompletedTask);
        string tokenUrl = "https://login.microsoftonline.com/consumers/oauth2/v2.0/token";
        handler.Serve(tokenUrl, """{"error":"authorization_declined"}""");
        MicrosoftDeviceCodeInfo device = new("D", "U", "https://microsoft.com/link", null, "msg", TimeSpan.FromMinutes(5), TimeSpan.Zero);

        bool declined = false;
        try
        {
            await service.CompleteDeviceLoginAsync("client-id", device);
        }
        catch (InvalidOperationException failure)
        {
            declined = failure.Message.Contains("拒绝", StringComparison.Ordinal);
        }

        AssertTrue(declined);

        StubHandler expiredHandler = new();
        expiredHandler.Serve(tokenUrl, """{"error":"expired_token"}""");
        MicrosoftMinecraftAuthService expiredService = new(new HttpClient(expiredHandler), (_, _) => Task.CompletedTask);
        bool expired = false;
        try
        {
            await expiredService.CompleteDeviceLoginAsync("client-id", device);
        }
        catch (TimeoutException)
        {
            expired = true;
        }

        AssertTrue(expired);
    }

    internal static async ValueTask MicrosoftRefreshRunsTheChainWithoutDeviceCode()
    {
        ScriptedHandler handler = new();
        MicrosoftMinecraftAuthService service = new(new HttpClient(handler), (_, _) => Task.CompletedTask);
        handler.Serve("https://login.microsoftonline.com/consumers/oauth2/v2.0/token",
            """{"access_token":"MS-AT2","refresh_token":"MS-RT2"}""");
        handler.Serve("https://user.auth.xboxlive.com/user/authenticate",
            """{"Token":"XBL-T","DisplayClaims":{"xui":[{"uhs":"UHS"}]}}""");
        handler.Serve("https://xsts.auth.xboxlive.com/xsts/authorize",
            """{"Token":"XSTS-T","DisplayClaims":{"xui":[{"uhs":"UHS"}]}}""");
        handler.Serve("https://api.minecraftservices.com/authentication/login_with_xbox",
            """{"access_token":"MC-AT2"}""");
        handler.Serve("https://api.minecraftservices.com/minecraft/profile",
            """{"name":"Alex","id":"uuid-alex","skins":[]}""");
        handler.Serve("https://api.minecraftservices.com/entitlements/mcstore", """{"items":[]}""");

        MicrosoftMinecraftLoginResult result = await service.RefreshAsync("client-id", "OLD-RT");
        AssertEqual("Alex", result.Username);
        AssertEqual("MS-RT2", result.RefreshToken);
        AssertFalse(result.OwnsMinecraft);
        AssertNull(result.SkinAddress);
    }

    internal static async ValueTask YggdrasilAuthenticateValidateAndRefreshRun()
    {
        ScriptedHandler handler = new();
        YggdrasilAuthService service = new(new HttpClient(handler));
        handler.Serve(
            "https://littleskin.cn/api/yggdrasil/authserver/authenticate",
            """{"accessToken":"AT-1","clientToken":"CT-1","selectedProfile":{"id":"uuid-ls","name":"Lemon"},"refreshToken":"RT-1"}""");
        handler.Serve("https://littleskin.cn/api/yggdrasil/authserver/validate", "", HttpStatusCode.NoContent);
        handler.Serve(
            "https://littleskin.cn/api/yggdrasil/authserver/refresh",
            """{"accessToken":"AT-2","clientToken":"CT-1","selectedProfile":{"id":"uuid-ls","name":"Lemon"}}""");

        YggdrasilAuthLoginResult login = await service.AuthenticateAsync(
            new YggdrasilAuthLoginRequest("https://littleskin.cn/api/yggdrasil", "user@mail", "pass", "CT-1"));
        AssertEqual("Lemon", login.Username);
        AssertEqual("uuid-ls", login.Uuid);
        AssertEqual("AT-1", login.AccessToken);
        AssertEqual("CT-1", login.ClientToken);
        AssertEqual("RT-1", login.RefreshToken);

        AssertTrue(await service.ValidateAsync("https://littleskin.cn", "AT-1"));
        AssertFalse(await service.ValidateAsync("https://littleskin.cn", "", "CT-1"));

        YggdrasilAuthLoginResult refreshed = await service.RefreshAsync("https://littleskin.cn", "AT-1", "CT-1");
        AssertEqual("AT-2", refreshed.AccessToken);
        AssertEqual("CT-1", refreshed.ClientToken);
        AssertEqual("littleskin.cn", refreshed.AuthServerDisplayName);
        AssertTrue(AccountLoginProfiles.IsLittleSkinServer(refreshed.AuthServer));
    }

    internal static async ValueTask YggdrasilFailureSurfacesServerMessage()
    {
        ScriptedHandler handler = new();
        YggdrasilAuthService service = new(new HttpClient(handler));
        handler.Serve(
            "https://littleskin.cn/api/yggdrasil/authserver/authenticate",
            """{"error":"ForbiddenOperationException","errorMessage":"Invalid credentials."}""",
            HttpStatusCode.Forbidden);

        bool rejected = false;
        try
        {
            await service.AuthenticateAsync(new YggdrasilAuthLoginRequest("littleskin.cn", "user@mail", "bad"));
        }
        catch (InvalidOperationException failure)
        {
            rejected = failure.Message.Contains("Invalid credentials", StringComparison.Ordinal);
        }

        AssertTrue(rejected);
        AssertTrue(handler.Requests.Single(request => request.Contains("authenticate", StringComparison.Ordinal))
            .Contains("https://littleskin.cn/api/yggdrasil/authserver/authenticate", StringComparison.Ordinal));
    }

    internal static void YggdrasilServerNormalizationAndJwtExpiry()
    {
        AssertEqual(
            "https://littleskin.cn/api/yggdrasil",
            YggdrasilAuthService.NormalizeYggdrasilServer("https://littleskin.cn"));
        AssertEqual(
            "https://littleskin.cn/api/yggdrasil",
            YggdrasilAuthService.NormalizeYggdrasilServer("littleskin.cn/authserver/"));
        AssertEqual(
            "http://localhost:8080/api/yggdrasil",
            YggdrasilAuthService.NormalizeYggdrasilServer("http://localhost:8080"));

        // An expired JWT with skew is expired; an opaque token is always "valid".
        string expired = "hdr." + Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes("{\"exp\":1000000000}")) + ".sig";
        string fresh = "hdr." + Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes($"{{\"exp\":{DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()}}}")) + ".sig";
        AssertFalse(YggdrasilAuthService.IsJwtAccessTokenUnexpired(expired));
        AssertTrue(YggdrasilAuthService.IsJwtAccessTokenUnexpired(fresh));
        AssertTrue(YggdrasilAuthService.IsJwtAccessTokenUnexpired("opaque-token"));
        AssertFalse(YggdrasilAuthService.IsJwtAccessTokenUnexpired(null));
    }

    internal static async ValueTask LoginResultsFeedThePersistedRoster()
    {
        string directory = CreateTempDirectory();
        try
        {
            string path = System.IO.Path.Combine(directory, "profiles.json");
            AccountService accounts = CreateAccountService(new LaunchProfileFilePort(path));

            MicrosoftMinecraftLoginResult microsoft = new(
                "Steve", "uuid-ms", "ms-access", "ms-refresh", "https://skin/steve", true);
            XsrResult<int> microsoftIndex = AccountLoginProfiles.Upsert(accounts, AccountLoginProfiles.FromMicrosoft(microsoft));
            AssertTrue(microsoftIndex.TryGetValue(out int steve) && steve == 0);

            YggdrasilAuthLoginResult littleSkin = new(
                "Lemon", "uuid-ls", "ls-access", "https://littleskin.cn/api/yggdrasil", "littleskin.cn", "CT", "RT");
            XsrResult<int> littleSkinIndex = AccountLoginProfiles.Upsert(accounts, AccountLoginProfiles.FromYggdrasil(littleSkin));
            AssertTrue(littleSkinIndex.TryGetValue(out int lemon) && lemon == 1);

            YggdrasilAuthLoginResult other = new(
                "Forum", "uuid-f", "f-access", "https://example.org/api/yggdrasil", "example.org");
            XsrResult<int> otherIndex = AccountLoginProfiles.Upsert(accounts, AccountLoginProfiles.FromYggdrasil(other));
            AssertTrue(otherIndex.TryGetValue(out int forum) && forum == 2);

            IReadOnlyList<LaunchProfileView> views = accounts.GetViews();
            AssertEqual(3, views.Count);
            AssertEqual(LaunchProfileKind.Microsoft, views[0].Kind);
            AssertEqual(LaunchProfileKind.LittleSkin, views[1].Kind);
            AssertEqual(LaunchProfileKind.ThirdParty, views[2].Kind);

            // A repeated Microsoft login replaces the existing roster entry instead of duplicating.
            MicrosoftMinecraftLoginResult again = microsoft with { Username = "Steve2" };
            XsrResult<int> replaced = AccountLoginProfiles.Upsert(accounts, AccountLoginProfiles.FromMicrosoft(again));
            AssertTrue(replaced.TryGetValue(out int replacedIndex) && replacedIndex == 0);
            AssertEqual("Steve2", accounts.GetViews()[0].Username);

            // Credentials persist, but the published views stay credential-free.
            string json = File.ReadAllText(path);
            AssertTrue(json.Contains("ms-refresh", StringComparison.Ordinal));
            AssertFalse(accounts.GetViews().Any(view =>
                view.Username.Contains("refresh", StringComparison.Ordinal)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        await Task.CompletedTask;
    }
}
