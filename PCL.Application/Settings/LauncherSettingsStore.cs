// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Collections.Concurrent;
using System.Text.Json;
using PCL.Core.Logging;

namespace PCL.Application.Settings;

public sealed class LauncherSettingsStore : IDisposable
{
    private const int ReplaceAttemptCount = 5;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> AccessLocks =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly SemaphoreSlim _accessLock;

    public LauncherSettingsStore(string settingsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        SettingsPath = Path.GetFullPath(settingsPath);
        _accessLock = AccessLocks.GetOrAdd(SettingsPath, static _ => new SemaphoreSlim(1, 1));
    }

    public string SettingsPath { get; }

    public async ValueTask<LauncherSettingsLoadResult> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        await _accessLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _accessLock.Release();
        }
    }

    public async ValueTask SaveAsync(
        LauncherSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ValidateCurrentSchema(settings);

        await _accessLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SaveCoreAsync(settings, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _accessLock.Release();
        }
    }

    /// <summary>
    /// Atomically loads, changes, and saves settings while holding the path-wide lock.
    /// Use this for partial updates so concurrent launcher services cannot overwrite
    /// unrelated values with a stale snapshot.
    /// </summary>
    public async ValueTask<LauncherSettings> UpdateAsync(
        Func<LauncherSettings, LauncherSettings> update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        await _accessLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LauncherSettingsLoadResult loaded = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            LauncherSettings settings = update(loaded.Settings)
                ?? throw new InvalidOperationException("The launcher settings update returned null.");
            ValidateCurrentSchema(settings);
            await SaveCoreAsync(settings, cancellationToken).ConfigureAwait(false);
            return settings;
        }
        finally
        {
            _accessLock.Release();
        }
    }

    private async ValueTask<LauncherSettingsLoadResult> LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(SettingsPath))
            return new(new LauncherSettings(), false, null);

        try
        {
            JsonDocument document;
            await using (FileStream stream = new(
                             SettingsPath,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.ReadWrite | FileShare.Delete,
                             bufferSize: 16 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                document = await JsonDocument.ParseAsync(
                        stream,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }

            using (document)
            {
                LauncherSettings settings = ReadSettings(document.RootElement, out bool recoveredInvalidItems);
                if (settings.SchemaVersion is <= 0 or > LauncherSettings.CurrentSchemaVersion)
                {
                    throw new InvalidDataException(
                        $"Unsupported launcher settings schema: {settings.SchemaVersion}.");
                }

                settings = settings.NormalizeOptionDictionaries();
                if (!recoveredInvalidItems)
                    return new(settings, false, null);

                string backupPath = SettingsPath + ".invalid";
                File.Copy(SettingsPath, backupPath, overwrite: true);
                try
                {
                    await SaveCoreAsync(settings, cancellationToken).ConfigureAwait(false);
                }
                catch (IOException exception)
                {
                    // Loading valid settings is more important than persisting the cleanup. The
                    // next regular save can still replace a file held briefly by another process.
                    PortableLog.Warn(
                        exception,
                        "Settings",
                        "已隔离损坏的启动器配置项，但暂时无法写回修复后的设置文件。");
                }

                PortableLog.Warn(
                    "Settings",
                    $"启动器设置中存在损坏项，已保留其他有效设置并备份到 {backupPath}。");
                return new(settings, true, backupPath);
            }
        }
        catch (Exception exception)
            when (exception is JsonException or InvalidDataException)
        {
            string backupPath = SettingsPath + ".invalid";
            File.Copy(SettingsPath, backupPath, overwrite: true);
            return new(new LauncherSettings(), true, backupPath);
        }
    }

    private async Task SaveCoreAsync(LauncherSettings settings, CancellationToken cancellationToken)
    {
        string directory = Path.GetDirectoryName(SettingsPath)
            ?? throw new InvalidOperationException(
                "The launcher settings path has no parent directory.");
        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(SettingsPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (FileStream stream = new(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 16 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                        stream,
                        settings,
                        LauncherSettingsJsonContext.Default.LauncherSettings,
                        cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            await ReplaceWithRetryAsync(temporaryPath, cancellationToken).ConfigureAwait(false);
            temporaryPath = string.Empty;
        }
        finally
        {
            if (!string.IsNullOrEmpty(temporaryPath))
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (IOException)
                {
                    // Preserve the original save exception. A later save or OS cleanup can
                    // remove an externally locked temporary file.
                }
            }
        }
    }

    private static void ValidateCurrentSchema(LauncherSettings settings)
    {
        if (settings.SchemaVersion != LauncherSettings.CurrentSchemaVersion)
        {
            throw new ArgumentOutOfRangeException(
                nameof(settings),
                settings.SchemaVersion,
                "Only the current launcher settings schema can be saved.");
        }
    }

    private static LauncherSettings ReadSettings(JsonElement root, out bool recoveredInvalidItems)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("The launcher settings file root must be an object.");

        LauncherSettings defaults = new();
        recoveredInvalidItems = false;
        return new LauncherSettings
        {
            SchemaVersion = ReadInt32(root, "schemaVersion", defaults.SchemaVersion, ref recoveredInvalidItems),
            AutomaticallyRepairGameIssues = ReadBoolean(
                root,
                "automaticallyRepairGameIssues",
                defaults.AutomaticallyRepairGameIssues,
                ref recoveredInvalidItems),
            ColorMode = ReadEnum(root, "colorMode", defaults.ColorMode, ref recoveredInvalidItems),
            LightColor = ReadEnum(root, "lightColor", defaults.LightColor, ref recoveredInvalidItems),
            DarkColor = ReadEnum(root, "darkColor", defaults.DarkColor, ref recoveredInvalidItems),
            DownloadSource = ReadEnum(root, "downloadSource", defaults.DownloadSource, ref recoveredInvalidItems),
            BooleanOptions = ReadDictionary(root, "booleanOptions", static value =>
                value.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? (true, value.GetBoolean())
                    : (false, default), ref recoveredInvalidItems),
            IntegerOptions = ReadDictionary(root, "integerOptions", static value =>
                value.TryGetInt32(out int parsed)
                    ? (true, parsed)
                    : (false, default), ref recoveredInvalidItems),
            TextOptions = ReadDictionary(root, "textOptions", static value =>
                value.ValueKind == JsonValueKind.String
                    ? (true, value.GetString() ?? string.Empty)
                    : (false, string.Empty), ref recoveredInvalidItems)
        };
    }

    private static bool ReadBoolean(
        JsonElement root,
        string propertyName,
        bool fallback,
        ref bool recovered)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement value))
            return fallback;
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return value.GetBoolean();

        recovered = true;
        return fallback;
    }

    private static int ReadInt32(
        JsonElement root,
        string propertyName,
        int fallback,
        ref bool recovered)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement value))
            return fallback;
        if (value.TryGetInt32(out int parsed))
            return parsed;

        recovered = true;
        return fallback;
    }

    private static TEnum ReadEnum<TEnum>(
        JsonElement root,
        string propertyName,
        TEnum fallback,
        ref bool recovered)
        where TEnum : struct, Enum
    {
        if (!root.TryGetProperty(propertyName, out JsonElement value))
            return fallback;

        TEnum parsed = fallback;
        bool valid = value.ValueKind switch
        {
            JsonValueKind.String => Enum.TryParse(value.GetString(), ignoreCase: true, out parsed) &&
                                    Enum.IsDefined(parsed),
            JsonValueKind.Number when value.TryGetInt32(out int number) =>
                Enum.IsDefined(parsed = (TEnum)Enum.ToObject(typeof(TEnum), number)),
            _ => false
        };
        if (valid)
            return parsed;

        recovered = true;
        return fallback;
    }

    private static Dictionary<string, TValue> ReadDictionary<TValue>(
        JsonElement root,
        string propertyName,
        Func<JsonElement, (bool Success, TValue Value)> readValue,
        ref bool recovered)
    {
        Dictionary<string, TValue> result = new(StringComparer.OrdinalIgnoreCase);
        if (!root.TryGetProperty(propertyName, out JsonElement dictionary))
            return result;
        if (dictionary.ValueKind != JsonValueKind.Object)
        {
            recovered = true;
            return result;
        }

        foreach (JsonProperty property in dictionary.EnumerateObject())
        {
            (bool success, TValue value) = readValue(property.Value);
            if (success)
                result[property.Name] = value;
            else
                recovered = true;
        }

        return result;
    }

    private async Task ReplaceWithRetryAsync(string temporaryPath, CancellationToken cancellationToken)
    {
        IOException? lastException = null;
        for (int attempt = 1; attempt <= ReplaceAttemptCount; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Move(temporaryPath, SettingsPath, overwrite: true);
                return;
            }
            catch (IOException exception) when (attempt < ReplaceAttemptCount)
            {
                lastException = exception;
                await Task.Delay(TimeSpan.FromMilliseconds(25 * attempt * attempt), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (IOException exception)
            {
                lastException = exception;
            }
        }

        throw new IOException(
            $"Unable to replace launcher settings file '{SettingsPath}' after {ReplaceAttemptCount} attempts.",
            lastException);
    }

    // Locks are shared by every store instance for the same normalized path and live for
    // the process lifetime. Disposing one short-lived store must not dispose the shared lock.
    public void Dispose()
    {
    }
}
