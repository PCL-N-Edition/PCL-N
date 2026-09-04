using System.Net;
using System.Text;
using PCL.Core.Media;
using PCL.Services.Accounts;
using PCL.Xsr.State;

namespace PCL.Services.Tests;

internal static partial class Program
{
    private static byte[] SkinFixture()
    {
        using Stream stream = typeof(Program).Assembly.GetManifestResourceStream("Fixtures.Steve.png")!;
        using MemoryStream buffer = new(); stream.CopyTo(buffer); return buffer.ToArray();
    }

    private static async ValueTask ProfileSkinsResolveExplicitAndSessionTextures()
    {
        byte[] png = SkinFixture();
        XsrStateStoreBuilder builder = new(); AccountService.DeclareState(builder); AccountSkinService.DeclareState(builder);
        AccountService accounts = new(builder.Build(), new ThrowingProfilePort());
        accounts.AddProfile(SampleProfile("Explicit") with { SkinAddress = "https://skins.example/direct.png" });
        accounts.AddProfile(SampleProfile("Online") with
        {
            Kind = LaunchProfileKind.Microsoft,
            Uuid = "01234567-89ab-cdef-0123-456789abcdef",
            AccessToken = "never-send-this",
        });
        accounts.AddProfile(SampleProfile("Broken") with { SkinAddress = "https://skins.example/broken.png" });
        accounts.AddProfile(SampleProfile("Legacy session") with { SkinAddress = "https://skins.example/session/profile" });
        accounts.AddProfile(SampleProfile("Legacy UUID") with { SkinAddress = "uuid:0123456789abcdef0123456789abcdef", AuthServer = "" });
        accounts.AddProfile(SampleProfile("Oversized") with { SkinAddress = "https://skins.example/oversized.png" });
        accounts.AddProfile(SampleProfile("Unsafe") with { SkinAddress = "file:///C:/private/skin.png" });
        string texture = Convert.ToBase64String(Encoding.UTF8.GetBytes("""{"textures":{"SKIN":{"url":"http://textures.minecraft.net/texture/fixture"}}}"""));
        SkinHttp handler = new();
        handler.Responses["https://skins.example/direct.png"] = png;
        handler.Responses["https://skins.example/broken.png"] = [1, 2, 3];
        handler.Responses["https://sessionserver.mojang.com/session/minecraft/profile/0123456789abcdef0123456789abcdef"] =
            Encoding.UTF8.GetBytes("{\"properties\":[{\"name\":\"textures\",\"value\":\"" + texture + "\"}]}");
        handler.Responses["https://textures.minecraft.net/texture/fixture"] = png;
        handler.Responses["https://skins.example/session/profile"] = handler.Responses["https://sessionserver.mojang.com/session/minecraft/profile/0123456789abcdef0123456789abcdef"];
        handler.Responses["https://skins.example/oversized.png"] = new byte[1_048_577];
        using HttpClient client = new(handler);
        using AccountSkinService skins = new(accounts, client);
        AssertTrue(skins.Refresh().IsSuccess); await skins.WhenIdle;
        var snapshots = accounts.StateStore.ReadCollection<AccountSkinSnapshot>(accounts.StateStore.Resolve(AccountSkinService.SkinsKey)).Items;
        AssertEqual(7, snapshots.Count);
        AssertEqual(4, snapshots.Count(item => item.Image is not null));
        AssertTrue(snapshots.Where(item => item.Image is not null).All(item => item.Image!.Bytes.Span.SequenceEqual(png)));
        AssertFalse(handler.SentCredentials);
        int calls = handler.Calls;
        handler.Responses["https://skins.example/broken.png"] = png;
        skins.Refresh(); await skins.WhenIdle;
        AssertEqual(calls + 2, handler.Calls); // Successful images are cached; failed media can recover.
        AssertEqual(5, accounts.StateStore.ReadCollection<AccountSkinSnapshot>(accounts.StateStore.Resolve(AccountSkinService.SkinsKey)).Items.Count(item => item.Image is not null));
        AssertTrue(PngImage.TryCreate([1, 2, 3]) is null);
        byte[] oversized = png.ToArray(); oversized[16] = 127;
        AssertTrue(PngImage.TryCreate(oversized) is null);
    }

    private static async ValueTask RemovedProfilesRejectLateSkinResponses()
    {
        XsrStateStoreBuilder builder = new(); AccountService.DeclareState(builder); AccountSkinService.DeclareState(builder);
        AccountService accounts = new(builder.Build(), new ThrowingProfilePort());
        accounts.AddProfile(SampleProfile("Old") with { SkinAddress = "https://skins.example/old.png" });
        SkinHttp handler = new() { Paused = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using HttpClient client = new(handler);
        using AccountSkinService skins = new(accounts, client);
        skins.Refresh(); Task old = skins.WhenIdle;
        AssertTrue(SpinWait.SpinUntil(() => handler.Calls > 0, TimeSpan.FromSeconds(2)));
        accounts.RemoveProfile(0);
        skins.Refresh(); await skins.WhenIdle;
        handler.Paused.SetResult(SkinFixture()); await old;
        AssertEqual(0, accounts.StateStore.ReadCollection<AccountSkinSnapshot>(accounts.StateStore.Resolve(AccountSkinService.SkinsKey)).Count);
    }

    private sealed class SkinHttp : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Responses { get; } = [];
        public TaskCompletionSource<byte[]>? Paused { get; init; }
        public int Calls;
        public bool SentCredentials;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            if (request.Headers.Authorization is not null || request.Content is not null) SentCredentials = true;
            byte[] data = Paused is not null ? await Paused.Task : Responses[request.RequestUri!.AbsoluteUri];
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };
        }
    }
}
