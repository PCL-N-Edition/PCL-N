// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PCL.Desktop.Telemetry;

/// <summary>
/// Small AOT-safe PostHog transport. Events live only in a bounded memory queue so opting out or
/// clearing pending data has immediate, deterministic semantics.
/// </summary>
internal sealed class PostHogExperienceClient : IDisposable
{
    private const int MaximumQueueLength = 256;
    private const int MaximumBatchLength = 20;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private readonly string _projectToken;
    private readonly string _distinctId;
    private readonly HttpClient _httpClient;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private readonly List<PostHogCaptureEvent> _pending = [];
    private Task? _flushLoop;
    private bool _enabled = true;

    public PostHogExperienceClient(string projectToken, Uri host, string distinctId)
    {
        _projectToken = projectToken;
        _distinctId = distinctId;
        _httpClient = new HttpClient
        {
            BaseAddress = host,
            Timeout = TimeSpan.FromSeconds(10)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("PCL-N", TelemetryDataPolicy.Release));
    }

    public void Capture(string eventName, IReadOnlyDictionary<string, string>? properties)
    {
        Dictionary<string, JsonElement> payloadProperties = new(StringComparer.Ordinal)
        {
            ["distinct_id"] = JsonSerializer.SerializeToElement(
                _distinctId,
                TelemetryJsonContext.Default.String),
            ["$process_person_profile"] = JsonSerializer.SerializeToElement(
                false,
                TelemetryJsonContext.Default.Boolean),
            ["$geoip_disable"] = JsonSerializer.SerializeToElement(
                true,
                TelemetryJsonContext.Default.Boolean),
            ["app_version"] = StringElement(TelemetryDataPolicy.Release),
            ["release_channel"] = StringElement(TelemetryDataPolicy.ReleaseChannel),
            ["platform"] = StringElement(TelemetryDataPolicy.Platform),
            ["architecture"] = StringElement(TelemetryDataPolicy.Architecture)
        };
        foreach ((string key, string value) in TelemetryDataPolicy.SanitizeProperties(properties))
        {
            payloadProperties[key] = JsonSerializer.SerializeToElement(
                value,
                TelemetryJsonContext.Default.String);
        }

        lock (_gate)
        {
            if (!_enabled)
                return;
            if (_pending.Count >= MaximumQueueLength)
                _pending.RemoveAt(0);
            _pending.Add(new PostHogCaptureEvent(
                TelemetryDataPolicy.NormalizeName(eventName),
                DateTimeOffset.UtcNow,
                payloadProperties));
            _flushLoop ??= RunFlushLoopAsync(_lifetime.Token);
        }
    }

    public async Task<bool?> IsFeatureEnabledAsync(
        string flagKey,
        CancellationToken cancellationToken)
    {
        if (!_enabled || string.IsNullOrWhiteSpace(flagKey))
            return null;

        PostHogFlagsRequest request = new(
            _projectToken,
            _distinctId,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["app_version"] = TelemetryDataPolicy.Release,
                ["release_channel"] = TelemetryDataPolicy.ReleaseChannel,
                ["platform"] = TelemetryDataPolicy.Platform,
                ["architecture"] = TelemetryDataPolicy.Architecture
            });
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(
            request,
            TelemetryJsonContext.Default.PostHogFlagsRequest);
        using ByteArrayContent content = new(json);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using HttpResponseMessage response = await _httpClient.PostAsync(
                "flags/?v=2",
                content,
                cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return null;

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using JsonDocument document = await JsonDocument.ParseAsync(
                stream,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return TryReadFlag(document.RootElement, flagKey);
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        await _flushGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (TryTakeBatch(out List<PostHogCaptureEvent>? batch))
            {
                PostHogCaptureBatch request = new(_projectToken, batch!);
                byte[] json = JsonSerializer.SerializeToUtf8Bytes(
                    request,
                    TelemetryJsonContext.Default.PostHogCaptureBatch);
                using ByteArrayContent content = new(json);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                using HttpResponseMessage response = await _httpClient.PostAsync(
                        "batch/",
                        content,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                    continue;

                RestoreBatch(batch!);
                return;
            }
        }
        finally
        {
            _flushGate.Release();
        }
    }

    public void DisableAndClear()
    {
        lock (_gate)
        {
            if (!_enabled)
                return;
            _enabled = false;
            _pending.Clear();
        }
        _lifetime.Cancel();
    }

    public void ClearPending()
    {
        lock (_gate)
            _pending.Clear();
    }

    public void Dispose()
    {
        DisableAndClear();
        _httpClient.Dispose();
        _flushGate.Dispose();
        _lifetime.Dispose();
    }

    private async Task RunFlushLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(FlushInterval, cancellationToken).ConfigureAwait(false);
                await FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            // Telemetry must never affect launcher behavior. A later capture starts a new loop.
        }
        finally
        {
            lock (_gate)
                _flushLoop = null;
        }
    }

    private bool TryTakeBatch(out List<PostHogCaptureEvent>? batch)
    {
        lock (_gate)
        {
            if (!_enabled || _pending.Count == 0)
            {
                batch = null;
                return false;
            }

            int count = Math.Min(MaximumBatchLength, _pending.Count);
            batch = _pending.GetRange(0, count);
            _pending.RemoveRange(0, count);
            return true;
        }
    }

    private void RestoreBatch(List<PostHogCaptureEvent> batch)
    {
        lock (_gate)
        {
            if (!_enabled)
                return;
            _pending.InsertRange(0, batch);
            if (_pending.Count > MaximumQueueLength)
                _pending.RemoveRange(MaximumQueueLength, _pending.Count - MaximumQueueLength);
        }
    }

    private static bool? TryReadFlag(JsonElement root, string flagKey)
    {
        if (root.TryGetProperty("flags", out JsonElement flags) &&
            flags.ValueKind == JsonValueKind.Object &&
            flags.TryGetProperty(flagKey, out JsonElement flag))
        {
            if (flag.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return flag.GetBoolean();
            if (flag.ValueKind == JsonValueKind.Object &&
                flag.TryGetProperty("enabled", out JsonElement enabled) &&
                enabled.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return enabled.GetBoolean();
            }
        }

        if (root.TryGetProperty("featureFlags", out JsonElement legacyFlags) &&
            legacyFlags.ValueKind == JsonValueKind.Object &&
            legacyFlags.TryGetProperty(flagKey, out JsonElement legacyFlag))
        {
            return legacyFlag.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False or JsonValueKind.Null => false,
                JsonValueKind.String => true,
                _ => null
            };
        }

        return null;
    }

    private static JsonElement StringElement(string value) =>
        JsonSerializer.SerializeToElement(value, TelemetryJsonContext.Default.String);
}

internal sealed record PostHogCaptureBatch(
    [property: JsonPropertyName("api_key")] string ApiKey,
    [property: JsonPropertyName("batch")] List<PostHogCaptureEvent> Batch);

internal sealed record PostHogCaptureEvent(
    [property: JsonPropertyName("event")] string Event,
    [property: JsonPropertyName("timestamp")] DateTimeOffset Timestamp,
    [property: JsonPropertyName("properties")] Dictionary<string, JsonElement> Properties);

internal sealed record PostHogFlagsRequest(
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("distinct_id")] string DistinctId,
    [property: JsonPropertyName("person_properties")] Dictionary<string, string> PersonProperties);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(PostHogCaptureBatch))]
[JsonSerializable(typeof(PostHogFlagsRequest))]
internal sealed partial class TelemetryJsonContext : JsonSerializerContext;
