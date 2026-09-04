using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PCL.Core.Media;
using PCL.Services.Logging;
using PCL.Xsr;
using PCL.Xsr.State;

namespace PCL.Services.Accounts;

public sealed record AccountSkinSnapshot(string ProfileKey, PngImage? Image);
public sealed record AccountRefreshSkinsCommand;

/// <summary>Credential-free skin resolution, bounded to 64 current profiles and one MiB per image.</summary>
public sealed class AccountSkinService(AccountService accounts, HttpClient client, LogService? log = null) : IDisposable
{
    public static readonly XsrSemanticId SkinsKey = XsrSemanticId.Parse("accounts.skins");
    public static readonly XsrSemanticId RefreshRoute = XsrSemanticId.Parse("accounts.skins.refresh");
    private readonly object _gate = new();
    private CancellationTokenSource? _active;
    private Task _running = Task.CompletedTask;
    private bool _disposed;
    public Task WhenIdle { get { lock (_gate) return _running; } }
    public static void DeclareState(XsrStateStoreBuilder builder) =>
        builder.Collection<AccountSkinSnapshot, string>(SkinsKey, "PCL.Services.AccountSkins", item => item.ProfileKey);

    public static string ProfileKey(LaunchProfileView profile) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        $"{profile.Kind}\n{profile.Uuid}\n{profile.AuthServer}\n{profile.SkinAddress}\n{profile.Username}")));

    public XsrResult Refresh()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _active?.Cancel();
            CancellationTokenSource operation = new();
            _active = operation;
            LaunchProfileView[] profiles = accounts.GetViews().OrderByDescending(profile => profile.Index == accounts.SelectedIndex).Take(64).ToArray();
            _running = Task.Run(() => ResolveAll(profiles, operation));
            return XsrResult.Success();
        }
    }

    public void Dispose()
    {
        lock (_gate) { _disposed = true; _active?.Cancel(); _active = null; }
    }

    private async Task ResolveAll(LaunchProfileView[] profiles, CancellationTokenSource operation)
    {
        using LogOperation? trace = log?.BeginOperation("AccountSkin", "ResolveAvatars", $"profiles={profiles.Length}", LogLevel.Debug);
        try
        {
            XsrStateStore store = accounts.StateStore;
            XsrStateId id = store.Resolve(SkinsKey);
            Dictionary<string, AccountSkinSnapshot> cached = store.ReadCollection<AccountSkinSnapshot>(id).Items.ToDictionary(item => item.ProfileKey);
            HashSet<string> keys = profiles.Select(ProfileKey).ToHashSet(StringComparer.Ordinal);
            lock (_gate)
            {
                if (_disposed || _active != operation) { trace?.Cancel(); return; }
                var snapshot = store.ReadCollection<AccountSkinSnapshot>(id);
                store.PublishDelta(id, new XsrCollectionDelta<AccountSkinSnapshot, string>(snapshot.Revision, [], snapshot.Items.Select(item => item.ProfileKey).Where(key => !keys.Contains(key)).ToArray()));
            }
            await Parallel.ForEachAsync(profiles, new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = operation.Token }, async (profile, token) =>
            {
                string key = ProfileKey(profile);
                if (cached.TryGetValue(key, out AccountSkinSnapshot? previous) && previous.Image is not null) return;
                PngImage? image = null;
                try { image = await Resolve(profile, token).ConfigureAwait(false); }
                catch (Exception failure) when (failure is HttpRequestException or IOException or InvalidDataException or JsonException or FormatException or InvalidOperationException or TaskCanceledException)
                {
                    log?.Write(token.IsCancellationRequested ? LogLevel.Debug : LogLevel.Warn,
                        "AccountSkin", $"Avatar lookup failed; retaining embedded fallback profile_index={profile.Index} kind={profile.Kind}", ExceptionDiagnostics.Describe(failure));
                }
                log?.Debug("AccountSkin", $"Avatar resolved profile_index={profile.Index} source={(image is null ? "embedded" : "remote")}");
                lock (_gate)
                {
                    if (_disposed || _active != operation || token.IsCancellationRequested) return;
                    var snapshot = store.ReadCollection<AccountSkinSnapshot>(id, cancellationToken: token);
                    store.PublishDelta(id, new XsrCollectionDelta<AccountSkinSnapshot, string>(snapshot.Revision, [new(key, image)], []), cancellationToken: token);
                }
            }).ConfigureAwait(false);
            trace?.Complete();
        }
        catch (OperationCanceledException) { trace?.Cancel(); }
        catch (Exception failure) when (failure is not OutOfMemoryException and not AccessViolationException)
        {
            trace?.Fail(failure);
            throw;
        }
        finally
        {
            lock (_gate) { if (_active == operation) _active = null; operation.Dispose(); }
        }
    }

    private async Task<PngImage?> Resolve(LaunchProfileView profile, CancellationToken token)
    {
        string? source = profile.SkinAddress;
        if (string.IsNullOrWhiteSpace(source) || source.StartsWith("uuid:", StringComparison.OrdinalIgnoreCase))
        {
            string identity = source?.StartsWith("uuid:", StringComparison.OrdinalIgnoreCase) == true ? source[5..] : profile.Uuid;
            if ((profile.Kind == LaunchProfileKind.Offline && string.IsNullOrWhiteSpace(source)) || !Guid.TryParse(identity, out Guid uuid)) return null;
            string endpoint = string.IsNullOrWhiteSpace(profile.AuthServer)
                ? "https://sessionserver.mojang.com/session/minecraft/profile/" + uuid.ToString("N")
                : profile.AuthServer.TrimEnd('/') + "/sessionserver/session/minecraft/profile/" + uuid.ToString("N");
            if (SafeUri(endpoint) is not { } metadata) return null;
            source = TextureAddress(await Read(metadata, 128 * 1024, token).ConfigureAwait(false));
        }
        if (SafeUri(source) is not { } address) return null;
        byte[] response = await Read(address, 1_048_576, token).ConfigureAwait(false);
        PngImage? image = PngImage.TryCreate(response);
        // Legacy SkinAddress may refer to a public session profile, not directly to PNG.
        // Follow at most one texture reference, never a recursive metadata/redirect chain.
        if (image is null && response.Length <= 128 * 1024 && SafeUri(TextureAddress(response)) is { } texture)
            image = PngImage.TryCreate(await Read(texture, 1_048_576, token).ConfigureAwait(false));
        return image is { Width: >= 64 and <= 512 } && image.Width % 64 == 0
            && (image.Height == image.Width || image.Height * 2 == image.Width) ? image : null;
    }

    private static string? TextureAddress(byte[] response)
    {
        using JsonDocument document = JsonDocument.Parse(response);
        if (!document.RootElement.TryGetProperty("properties", out JsonElement properties) || properties.ValueKind != JsonValueKind.Array) return null;
        foreach (JsonElement property in properties.EnumerateArray())
        {
            if (!property.TryGetProperty("name", out JsonElement name) || name.GetString() != "textures"
                || !property.TryGetProperty("value", out JsonElement value) || value.GetString() is not { } encoded) continue;
            using JsonDocument textures = JsonDocument.Parse(Convert.FromBase64String(encoded));
            if (textures.RootElement.TryGetProperty("textures", out JsonElement entries) && entries.TryGetProperty("SKIN", out JsonElement skin)
                && skin.TryGetProperty("url", out JsonElement url)) return url.GetString();
        }
        return null;
    }

    private static Uri? SafeUri(string? address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out Uri? uri) || uri.UserInfo.Length != 0) return null;
        if (uri.Scheme == "http" && uri.Host is "textures.minecraft.net" or "littleskin.cn")
            uri = new UriBuilder(uri) { Scheme = "https", Port = -1 }.Uri;
        return uri.Scheme == "https" && !uri.IsLoopback ? uri : null;
    }

    private async Task<byte[]> Read(Uri address, int limit, CancellationToken token)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, address);
        using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("Skin response exceeds limit.");
        using Stream stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using MemoryStream result = new();
        byte[] buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            if (result.Length + count > limit) throw new InvalidDataException("Skin response exceeds limit.");
            result.Write(buffer, 0, count);
        }
        return result.ToArray();
    }
}
