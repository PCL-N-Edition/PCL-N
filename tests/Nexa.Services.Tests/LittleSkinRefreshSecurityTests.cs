using System.Net;
using Nexa.Services.Accounts;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async ValueTask LittleSkinRefreshRejectsStaleGenerations()
    {
        foreach (string stage in new[] { "valid", "oauth", "session", "legacy" })
            foreach (string mutation in new[] { "readd", "shift", "replace", "touch", "unrelated", "cancel", "unchanged" })
            {
                var port = new ThrowingProfilePort();
                var accounts = CreateAccountService(port);
                var original = SampleProfile("Alice", "old-access") with
                {
                    Kind = LaunchProfileKind.LittleSkin,
                    Uuid = "0123456789abcdef0123456789abcdef",
                    ProviderAccessToken = stage == "legacy" ? "" : "provider-old",
                    RefreshToken = "refresh-old",
                    ProviderTokenExpiresAtUnix = 0
                };
                var other = original with { Username = "Bob", Uuid = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" };
                AssertTrue(accounts.AddProfile(original).IsSuccess);
                AssertTrue(accounts.AddProfile(other).IsSuccess);
                var barrier = new AuthStageBarrier(stage);
                using var http = new HttpClient(new LittleSkinRaceHandler(barrier, original.Uuid));
                using var cancellation = new CancellationTokenSource();
                var resolver = new AccountLaunchIdentityResolver(accounts,
                    littleSkin: new PausedLittleSkin(barrier, original.Uuid), littleSkinConfiguration: LittleSkinConfig(),
                    yggdrasil: new YggdrasilAuthService(http));
                var pending = resolver.ResolveAsync(0, original, cancellation.Token).AsTask();
                await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                LaunchProfile current = accounts.GetProfile(0).Value!;
                if (mutation == "readd")
                {
                    AssertTrue(accounts.RemoveProfile(0).IsSuccess);
                    AssertTrue(accounts.RemoveProfile(0).IsSuccess);
                    AssertTrue(accounts.AddProfile(current).IsSuccess);
                }
                if (mutation == "shift") AssertTrue(accounts.RemoveProfile(0).IsSuccess);
                if (mutation == "replace") AssertTrue(accounts.ReplaceProfile(0, current with { AccessToken = "newer" }).IsSuccess);
                if (mutation == "touch") AssertTrue(accounts.ReplaceProfile(0, current).IsSuccess);
                if (mutation == "unrelated") AssertTrue(accounts.ReplaceProfile(1, other with { Username = "Renamed" }).IsSuccess);
                if (mutation == "cancel") cancellation.Cancel();
                var before = port.Load().Profiles.ToArray();
                barrier.Continue.SetResult();
                bool success;
                try { success = (await pending).IsSuccess; }
                catch (OperationCanceledException) when (mutation == "cancel") { success = false; }
                AssertEqual(mutation == "unchanged", success);
                if (mutation != "unchanged") AssertTrue(before.SequenceEqual(port.Load().Profiles));
                else
                {
                    AssertEqual(stage == "valid" ? "old-access" : "game-new", accounts.GetProfile(0).Value!.AccessToken);
                    if (stage is "oauth" or "session") AssertEqual("provider-new", accounts.GetProfile(0).Value!.ProviderAccessToken);
                }
            }
    }

    private sealed class AuthStageBarrier(string stage)
    {
        public string Stage { get; } = stage;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task Pause(string current)
        {
            if (Stage != current) return;
            Entered.SetResult();
            await Continue.Task; // Deliberately ignores cancellation to exercise the resolver's guard.
        }
    }

    private sealed class LittleSkinRaceHandler(AuthStageBarrier barrier, string uuid) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("validate", StringComparison.Ordinal))
            {
                await barrier.Pause("valid");
                return new(barrier.Stage == "valid" ? HttpStatusCode.NoContent : HttpStatusCode.Unauthorized);
            }
            await barrier.Pause("legacy");
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"accessToken\":\"game-new\",\"clientToken\":\"client\",\"selectedProfile\":{\"name\":\"Alice\",\"id\":\"" + uuid + "\"}}") };
        }
    }

    private sealed class PausedLittleSkin(AuthStageBarrier barrier, string uuid) : ILittleSkinOAuthService
    {
        public async Task<LittleSkinOAuthTokens> RefreshOAuthTokenAsync(LittleSkinOAuthConfiguration configuration, string refreshToken, CancellationToken cancellationToken = default)
        {
            await barrier.Pause("oauth");
            return new("provider-new", "refresh-new", DateTimeOffset.UtcNow.AddDays(1), "");
        }
        public async Task<LittleSkinMinecraftSession> CreateMinecraftSessionAsync(string accessToken, string profileUuid, CancellationToken cancellationToken = default)
        {
            await barrier.Pause("session");
            return new("Alice", uuid, "game-new", "client-new");
        }
        public LittleSkinAuthorizationRequest CreateAuthorizationRequest(LittleSkinOAuthConfiguration configuration, string state) => throw new NotSupportedException();
        public Task<LittleSkinDeviceCodeInfo> RequestDeviceCodeAsync(LittleSkinOAuthConfiguration configuration, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LittleSkinOAuthTokens> WaitForDeviceAuthorizationAsync(LittleSkinOAuthConfiguration configuration, LittleSkinDeviceCodeInfo deviceCode, IProgress<double>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LittleSkinOAuthTokens> ExchangeAuthorizationCodeAsync(LittleSkinOAuthConfiguration configuration, string code, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<LittleSkinProfile>> GetProfilesAsync(string accessToken, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<LittleSkinPlayer>> GetPlayersAsync(string accessToken, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<LittleSkinClosetItem>> GetClosetItemsAsync(string accessToken, LittleSkinTextureKind kind, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ApplyTextureAsync(string accessToken, long playerId, long textureId, LittleSkinTextureKind kind, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task EnsureClosetTextureAsync(string accessToken, long textureId, string name, LittleSkinTextureKind kind, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LittleSkinTextureUploadResult> UploadMinecraftTextureAsync(string minecraftAccessToken, string profileUuid, byte[] pngBytes, string fileName, bool isSlim, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
