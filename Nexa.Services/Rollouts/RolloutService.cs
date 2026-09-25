using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nexa.Xsr;
using Nexa.Xsr.State;

namespace Nexa.Services.Rollouts;

public sealed record RolloutSnapshot(long Revision, bool CompactTelemetryBatches, bool Available);
public static class RolloutStateContract
{
    public static readonly XsrSemanticId StateKey = XsrSemanticId.Parse("launcher.rollout.snapshot");
    public static void DeclareState(XsrStateStoreBuilder builder) => builder.Cell<RolloutSnapshot>(StateKey, "Nexa.Services.Rollouts");
}

/// <summary>Local assignment is not an identity, entitlement or authorization boundary.</summary>
public sealed class RolloutService(HttpClient http, XsrStateStore store, string seedPath, string channel, string rid) : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private string? _seed;
    private int _started;
    private DateTimeOffset _featureExpires;
    private bool _compact;
    public Action<string, string>? Record { get; set; }
    public bool CompactTelemetryBatches { get { lock (_gate) return _compact && _featureExpires > DateTimeOffset.UtcNow; } }

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 0) _ = RunAsync(_stop.Token);
    }
    private async Task RunAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        try
        {
            do { await RefreshAsync(token).ConfigureAwait(false); }
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
    public async Task RefreshAsync(CancellationToken token = default)
    {
        try
        {
            using var response = await http.GetAsync("https://api.pcln.top/v2/launcher/rollouts", HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var document = await ReadBoundedAsync(response, token).ConfigureAwait(false);
            var root = document.RootElement;
            long revision = root.GetProperty("revision").GetInt64();
            if (revision < 0 || root.GetProperty("rules").GetArrayLength() > 32) throw new InvalidDataException();
            bool compact = false;
            DateTimeOffset expires = default;
            foreach (var rule in root.GetProperty("rules").EnumerateArray())
            {
                if (rule.GetProperty("kind").GetString() != "feature" || rule.GetProperty("target").GetString() != "telemetry.compact-batches") continue;
                compact = Includes(rule, channel, rid);
                expires = rule.GetProperty("expiresAt").GetDateTimeOffset();
            }
            bool exposed;
            lock (_gate) { exposed = compact && !_compact; _compact = compact; _featureExpires = expires; }
            store.Publish(store.Resolve(RolloutStateContract.StateKey), new RolloutSnapshot(revision, compact, true), token);
            Record?.Invoke("rollout.checked", "ok");
            if (exposed) Record?.Invoke("feature.exposed", "ok");
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OperationCanceledException)
        {
            lock (_gate) _compact = false;
            if (!token.IsCancellationRequested)
            {
                store.Publish(store.Resolve(RolloutStateContract.StateKey), new RolloutSnapshot(0, false, false), CancellationToken.None);
                Record?.Invoke("rollout.checked", "failed");
            }
        }
    }
    public bool Includes(JsonElement rule, string targetChannel, string targetRid)
    {
        int points = rule.GetProperty("basisPoints").GetInt32();
        string id = rule.GetProperty("id").GetString() ?? "";
        if (points is < 0 or > 10000 || id.Length is < 1 or > 64 || id.Any(c => !char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c != '-')) throw new InvalidDataException();
        if (!rule.GetProperty("enabled").GetBoolean() || rule.GetProperty("expiresAt").GetDateTimeOffset() <= DateTimeOffset.UtcNow
            || !rule.GetProperty("channels").EnumerateArray().Any(item => item.GetString() == targetChannel)
            || !rule.GetProperty("rids").EnumerateArray().Any(item => item.GetString() == targetRid)) return false;
        return Bucket(GetSeed(), id) < points;
    }
    internal static int Bucket(string seed, string ruleId)
        => (int)(BinaryPrimitives.ReadUInt32BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes(seed + ":" + ruleId))) % 10000);

    private string GetSeed()
    {
        lock (_gate)
        {
            if (_seed is not null) return _seed;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(seedPath))!);
            try
            {
                using var output = new FileStream(seedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                var bytes = Encoding.ASCII.GetBytes(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
                output.Write(bytes);
            }
            catch (IOException) when (File.Exists(seedPath)) { }
            using var input = File.OpenRead(seedPath);
            if (input.Length != 64) throw new InvalidDataException("Invalid rollout seed.");
            byte[] value = new byte[64]; input.ReadExactly(value);
            string seed = Encoding.ASCII.GetString(value);
            if (seed.Any(c => !char.IsAsciiHexDigit(c))) throw new InvalidDataException("Invalid rollout seed.");
            return _seed = seed;
        }
    }
    internal static async Task<JsonDocument> ReadBoundedAsync(HttpResponseMessage response, CancellationToken token)
    {
        await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var buffer = new MemoryStream(); byte[] chunk = new byte[4096]; int read;
        while ((read = await input.ReadAsync(chunk, token).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > 32768) throw new InvalidDataException("Rollout policy exceeds limit.");
            buffer.Write(chunk, 0, read);
        }
        return JsonDocument.Parse(buffer.ToArray());
    }
    public void Dispose() { _stop.Cancel(); _stop.Dispose(); }
}
