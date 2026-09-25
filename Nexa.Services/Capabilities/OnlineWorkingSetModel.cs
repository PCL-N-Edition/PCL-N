using System.Text.Json;

namespace Nexa.Services.Capabilities;

/// <summary>Immutable, admitted aggregate coefficients. Predictions describe process working set,
/// never JVM heap, native allocations, commit, or a verified launch requirement.</summary>
public sealed class OnlineWorkingSetModel
{
    public const int MaximumDocumentBytes = 65536;
    private readonly Cohort[] _cohorts;
    private sealed record Cohort(string Os, string Loader, double[] Coefficients, double[] Minimum, double[] Maximum);
    public DateTimeOffset GeneratedAt { get; }
    public DateTimeOffset ExpiresAt { get; }

    private OnlineWorkingSetModel(DateTimeOffset generated, DateTimeOffset expires, Cohort[] cohorts)
    { GeneratedAt = generated; ExpiresAt = expires; _cohorts = cohorts; }

    public static OnlineWorkingSetModel Parse(ReadOnlyMemory<byte> bytes, DateTimeOffset now)
    {
        if (bytes.Length > MaximumDocumentBytes) throw new InvalidDataException("资源模型超过大小限制。");
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
        var root = document.RootElement;
        UniqueProperties(root);
        if (root.GetProperty("schema").GetInt32() != 1
            || root.GetProperty("metric").GetString() != "process-working-set-peak-mib")
            throw new InvalidDataException("不支持的资源模型。");
        var generated = root.GetProperty("generatedAt").GetDateTimeOffset();
        var expires = root.GetProperty("expiresAt").GetDateTimeOffset();
        if (generated > now.AddMinutes(5) || expires <= now || expires <= generated
            || expires - generated > TimeSpan.FromDays(7)) throw new InvalidDataException("资源模型已过期或时间无效。");
        var models = root.GetProperty("models");
        if (models.GetArrayLength() > 27) throw new InvalidDataException("资源模型分组过多。");
        List<Cohort> cohorts = [];
        HashSet<string> keys = new(StringComparer.Ordinal);
        int totalSamples = 0;
        foreach (var model in models.EnumerateArray())
        {
            UniqueProperties(model);
            string os = model.GetProperty("os").GetString() ?? "";
            string loader = model.GetProperty("loader").GetString() ?? "";
            if (os is not ("windows" or "linux" or "macos")
                || loader is not ("Vanilla" or "OptiFine" or "Forge" or "NeoForge" or "Fabric" or "Quilt" or "LiteLoader" or "Cleanroom" or "LabyMod")
                || !keys.Add(os + "/" + loader)) throw new InvalidDataException("资源模型分组无效。");
            int train = model.GetProperty("samples").GetInt32(), validation = model.GetProperty("validationSamples").GetInt32();
            if (train is < 40 or > 102 || validation is < 10 or > 26
                || validation != (train + validation + 4) / 5
                || (totalSamples += train + validation) > 128) throw new InvalidDataException("资源模型样本数无效。");
            double loss = Number(model.GetProperty("validationLossMiB"), 0, 65536);
            double baseline = Number(model.GetProperty("baselineLossMiB"), 0, 65536);
            _ = Number(model.GetProperty("coverage"), .8, 1);
            if (loss > baseline) throw new InvalidDataException("资源模型未通过验证。");
            var coefficients = Vector(model.GetProperty("coefficients"), [0, 0, 0, 0], [32768, 32768, 32768, 32768]);
            double[] lower = [1, 256d / 4096, 1d / 256, 4d / 256], upper = [1, 16, 16, 16];
            var minimum = Vector(model.GetProperty("featureMin"), lower, upper);
            var maximum = Vector(model.GetProperty("featureMax"), lower, upper);
            for (int i = 0; i < 4; i++) if (minimum[i] > maximum[i]) throw new InvalidDataException("资源模型范围无效。");
            cohorts.Add(new(os, loader, coefficients, minimum, maximum));
        }
        return new(generated, expires, cohorts.ToArray());
    }

    public bool TryPredict(string os, string loader, long heapMiB, int classpathCount, int renderDistance,
        DateTimeOffset now, out long workingSetMiB)
    {
        workingSetMiB = 0;
        if (now >= ExpiresAt || now < GeneratedAt.AddMinutes(-5) || heapMiB is < 256 or > 65536
            || classpathCount is < 1 or > 4096 || renderDistance is < 2 or > 64) return false;
        var cohort = Array.Find(_cohorts, item => item.Os == os && item.Loader == loader);
        if (cohort is null) return false;
        ReadOnlySpan<double> features = [1, heapMiB / 4096d, classpathCount / 256d, renderDistance * renderDistance / 256d];
        double prediction = 0;
        for (int i = 0; i < 4; i++)
        {
            if (features[i] < cohort.Minimum[i] || features[i] > cohort.Maximum[i]) return false;
            prediction += features[i] * cohort.Coefficients[i];
        }
        if (!double.IsFinite(prediction) || prediction is < 64 or > 65536) return false;
        workingSetMiB = (long)Math.Ceiling(prediction);
        return true;
    }

    private static double Number(JsonElement value, double minimum, double maximum)
    {
        double number = value.GetDouble();
        if (!double.IsFinite(number) || number < minimum || number > maximum) throw new InvalidDataException("资源模型数值无效。");
        return number;
    }
    private static double[] Vector(JsonElement value, double[] minimum, double[] maximum)
    {
        if (value.GetArrayLength() != 4) throw new InvalidDataException("资源模型参数数量无效。");
        return Enumerable.Range(0, 4).Select(i => Number(value[i], minimum[i], maximum[i])).ToArray();
    }
    private static void UniqueProperties(JsonElement value)
    {
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!names.Add(property.Name)) throw new InvalidDataException("资源模型包含重复字段。");
    }
}

/// <summary>A session cache, refreshed explicitly off the render/launch path. A failed refresh
/// retains the last admitted document only until its original expiry. No response can extend it.</summary>
public sealed class OnlineWorkingSetModelStore
{
    private readonly object _gate = new();
    private OnlineWorkingSetModel? _current;
    public OnlineWorkingSetModel? Read(DateTimeOffset now)
    {
        var model = Volatile.Read(ref _current);
        return model is not null && model.ExpiresAt > now ? model : null;
    }
    internal bool Publish(OnlineWorkingSetModel model)
    {
        lock (_gate)
        {
            if (_current is { } previous && model.GeneratedAt < previous.GeneratedAt) return false;
            Volatile.Write(ref _current, model);
            return true;
        }
    }
}

public sealed class OnlineWorkingSetModelClient(HttpClient http, TimeProvider? clock = null,
    OnlineWorkingSetModelStore? store = null) : IDisposable
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly SemaphoreSlim _refresh = new(1);
    private readonly OnlineWorkingSetModelStore _store = store ?? new();
    /// <summary>The owner must cancel and await outstanding refreshes before disposal.</summary>
    public void Dispose() => _refresh.Dispose();
    public OnlineWorkingSetModel? Current => _store.Read(_clock.GetUtcNow());

    public async Task<bool> RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _refresh.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            using var response = await http.GetAsync("https://api.pcln.top/v2/launcher/resource-model",
                HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > OnlineWorkingSetModel.MaximumDocumentBytes) return false;
            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            byte[] buffer = new byte[OnlineWorkingSetModel.MaximumDocumentBytes + 1];
            int count = 0;
            while (count < buffer.Length)
            {
                int read = await input.ReadAsync(buffer.AsMemory(count), timeout.Token).ConfigureAwait(false);
                if (read == 0) break;
                count += read;
            }
            var model = OnlineWorkingSetModel.Parse(buffer.AsMemory(0, count), _clock.GetUtcNow());
            timeout.Token.ThrowIfCancellationRequested();
            return _store.Publish(model);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or JsonException
            or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException or OperationCanceledException)
        { return false; }
        finally { _refresh.Release(); }
    }
}
