using Nexa.Services.Accounts;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async ValueTask MicrosoftRefreshCannotRestoreRemovedAccounts()
    {
        foreach (string mutation in new[] { "shift", "readd", "replace", "unrelated", "concurrent", "unchanged", "cancel" })
        {
            string root = CreateTempDirectory();
            try
            {
                var port = new LaunchProfileFilePort(Path.Combine(root, "profiles.json"));
                var accounts = CreateAccountService(port);
                var original = SampleProfile("Alice", "old-access") with
                { Kind = LaunchProfileKind.Microsoft, RefreshToken = "old-refresh" };
                var other = original with { Username = "Bob", Uuid = "uuid-Bob", AccessToken = "bob-access" };
                AssertTrue(accounts.AddProfile(original).IsSuccess);
                AssertTrue(accounts.AddProfile(other).IsSuccess);
                var auth = new PausedMicrosoftRefresh();
                using var cancellation = new CancellationTokenSource();
                var resolver = new AccountLaunchIdentityResolver(accounts, auth, "client-id");
                var pending = resolver.ResolveAsync(0, original, cancellation.Token).AsTask();
                await auth.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if (mutation is "shift" or "readd") AssertTrue(accounts.RemoveProfile(0).IsSuccess);
                if (mutation == "readd")
                {
                    AssertTrue(accounts.RemoveProfile(0).IsSuccess);
                    AssertTrue(accounts.AddProfile(original).IsSuccess);
                }
                if (mutation == "replace") AssertTrue(accounts.ReplaceProfile(0, original with { AccessToken = "newer-access" }).IsSuccess);
                if (mutation == "unrelated") AssertTrue(accounts.ReplaceProfile(1, other with { Username = "BobRenamed" }).IsSuccess);
                if (mutation == "concurrent")
                {
                    var newer = new PausedMicrosoftRefresh();
                    var second = new AccountLaunchIdentityResolver(accounts, newer, "client-id").ResolveAsync(0, original).AsTask();
                    await newer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    newer.Completion.SetResult(new("Alice", original.Uuid, "newer-access", "newer-refresh", null, true));
                    AssertTrue((await second).IsSuccess);
                }
                if (mutation == "cancel") cancellation.Cancel();
                var before = port.Load().Profiles.ToArray();
                auth.Completion.SetResult(new("AliceRenamed", original.Uuid, "rotated-access", "rotated-refresh", null, true));
                bool succeeded;
                try { succeeded = (await pending).IsSuccess; }
                catch (OperationCanceledException) when (mutation == "cancel") { succeeded = false; }
                AssertEqual(mutation == "unchanged", succeeded);
                if (mutation == "unchanged")
                {
                    AssertEqual("rotated-access", port.Load().Profiles[0].AccessToken);
                    AssertEqual(other, port.Load().Profiles[1]);
                }
                else AssertTrue(before.SequenceEqual(port.Load().Profiles));
            }
            finally { Directory.Delete(root, true); }
        }

        var failingPort = new ThrowingProfilePort();
        var sessionAccounts = CreateAccountService(failingPort);
        var sessionProfile = SampleProfile("Alice") with { Kind = LaunchProfileKind.Microsoft, RefreshToken = "refresh" };
        AssertTrue(sessionAccounts.AddProfile(sessionProfile).IsSuccess);
        failingPort.SaveShouldThrow = true;
        var sessionAuth = new PausedMicrosoftRefresh();
        var session = new AccountLaunchIdentityResolver(sessionAccounts, sessionAuth, "client-id").ResolveAsync(0, sessionProfile).AsTask();
        sessionAuth.Completion.SetResult(new("Alice", sessionProfile.Uuid, "session-access", "rotated", null, true));
        AssertTrue((await session).IsSuccess);
        AssertEqual(sessionProfile, sessionAccounts.GetProfile(0).Value);
    }

    private sealed class PausedMicrosoftRefresh : IMicrosoftMinecraftAuthService
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<MicrosoftMinecraftLoginResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<MicrosoftMinecraftLoginResult> RefreshAsync(string clientId, string refreshToken, CancellationToken cancellationToken = default)
        { Entered.SetResult(); return Completion.Task; }
        public Task<MicrosoftDeviceCodeInfo> RequestDeviceCodeAsync(string clientId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MicrosoftMinecraftLoginResult> CompleteDeviceLoginAsync(string clientId, MicrosoftDeviceCodeInfo deviceCode, IProgress<double>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
