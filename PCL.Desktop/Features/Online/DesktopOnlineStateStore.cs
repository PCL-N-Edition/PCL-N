// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PCL.Core.Logging;
using PCL.Platform.Security;
using PCL.Platform.Abstractions.Security;

namespace PCL.Desktop.Features.Online;

internal sealed class DesktopOnlineStateStore
{
    private static readonly HashSet<string> SensitiveStringKeys = new(StringComparer.Ordinal)
    {
        "Online.MsAccessToken",
        "Online.MsOAuthRefreshToken",
        "Online.MsGraphAccessToken",
        "Online.MsGraphRefreshToken"
    };

    private readonly object _gate = new();
    private readonly string _statePath;
    private readonly DefaultSecureStorage _secureStorage;
    private readonly Dictionary<string, string> _secretValues = new(StringComparer.Ordinal);
    private readonly HashSet<string> _loadedSecrets = new(StringComparer.Ordinal);
    private readonly HashSet<string> _dirtySecrets = new(StringComparer.Ordinal);
    private DesktopOnlineState _state;

    public DesktopOnlineStateStore(string sharedDataDirectory, string applicationDataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sharedDataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDataDirectory);
        _statePath = Path.Combine(sharedDataDirectory, "online-state.v1.json");
        _secureStorage = new DefaultSecureStorage(applicationDataDirectory, "PCL-N.Online");
        _state = LoadState(_statePath);
    }

    public string GetString(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        lock (_gate)
        {
            if (!SensitiveStringKeys.Contains(key))
                return _state.Strings.GetValueOrDefault(key, string.Empty);

            EnsureSecretLoaded(key);
            return _secretValues.GetValueOrDefault(key, string.Empty);
        }
    }

    public void SetString(string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        value ??= string.Empty;
        lock (_gate)
        {
            if (!SensitiveStringKeys.Contains(key))
            {
                _state.Strings[key] = value;
                return;
            }

            _secretValues[key] = value;
            _loadedSecrets.Add(key);
            _dirtySecrets.Add(key);
        }
    }

    public bool GetBoolean(string key, bool fallback = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        lock (_gate)
            return _state.Booleans.GetValueOrDefault(key, fallback);
    }

    public void SetBoolean(string key, bool value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        lock (_gate)
            _state.Booleans[key] = value;
    }

    public void Flush()
    {
        lock (_gate)
        {
            SaveState(_statePath, _state);
            foreach (string key in _dirtySecrets.ToArray())
            {
                string value = _secretValues.GetValueOrDefault(key, string.Empty);
                SecureStorageOperationResult result = string.IsNullOrEmpty(value)
                    ? _secureStorage.DeleteAsync(key).AsTask().GetAwaiter().GetResult()
                    : _secureStorage.WriteAsync(key, Encoding.UTF8.GetBytes(value)).AsTask().GetAwaiter().GetResult();
                if (!result.IsSuccess)
                {
                    PortableLog.Warn(
                        "Online",
                        $"在线账户凭据未能写入安全存储；Key={key}；Status={result.Status}；Message={result.Message}");
                    continue;
                }

                _dirtySecrets.Remove(key);
            }
        }
    }

    private void EnsureSecretLoaded(string key)
    {
        if (!_loadedSecrets.Add(key))
            return;

        SecureStorageReadResult result = _secureStorage.ReadAsync(key).AsTask().GetAwaiter().GetResult();
        if (result.Status == SecureStorageStatus.Success && result.Value is not null)
        {
            _secretValues[key] = Encoding.UTF8.GetString(result.Value);
            return;
        }

        _secretValues[key] = string.Empty;
        if (result.Status is SecureStorageStatus.Failed or SecureStorageStatus.Unavailable)
        {
            PortableLog.Warn(
                "Online",
                $"在线账户凭据未能从安全存储读取；Key={key}；Status={result.Status}；Message={result.Message}");
        }
    }

    private static DesktopOnlineState LoadState(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new DesktopOnlineState();

            using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            DesktopOnlineState? state = JsonSerializer.Deserialize(
                stream,
                DesktopOnlineJsonContext.Default.DesktopOnlineState);
            if (state is null || state.SchemaVersion != DesktopOnlineState.CurrentSchemaVersion)
                throw new InvalidDataException("不支持的在线状态文件版本。");
            return state.Normalize();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            try
            {
                if (File.Exists(path))
                    File.Copy(path, path + ".invalid", overwrite: true);
            }
            catch (Exception backupException) when (backupException is IOException or UnauthorizedAccessException)
            {
                PortableLog.Warn(backupException, "Online", "备份损坏的在线状态文件失败。");
            }

            PortableLog.Warn(exception, "Online", "读取在线状态失败，将使用默认状态。");
            return new DesktopOnlineState();
        }
    }

    private static void SaveState(string path, DesktopOnlineState state)
    {
        string directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("在线状态路径缺少父目录。");
        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (FileStream stream = new(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 16 * 1024,
                       FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(
                    stream,
                    state,
                    DesktopOnlineJsonContext.Default.DesktopOnlineState);
                stream.Flush(flushToDisk: true);
            }

            ReplaceWithRetry(temporaryPath, path);
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
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Preserve the original write failure.
                }
            }
        }
    }

    private static void ReplaceWithRetry(string temporaryPath, string targetPath)
    {
        Exception? lastException = null;
        for (int attempt = 1; attempt <= 6; attempt++)
        {
            try
            {
                File.Move(temporaryPath, targetPath, overwrite: true);
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                lastException = exception;
                if (attempt < 6)
                    Thread.Sleep(20 * attempt);
            }
        }

        throw new IOException($"Unable to replace online state file '{targetPath}'.", lastException);
    }
}

internal sealed class DesktopOnlineState
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public Dictionary<string, string> Strings { get; set; } = new(StringComparer.Ordinal);

    public Dictionary<string, bool> Booleans { get; set; } = new(StringComparer.Ordinal);

    public DesktopOnlineState Normalize()
    {
        Strings = new Dictionary<string, string>(Strings ?? new(), StringComparer.Ordinal);
        Booleans = new Dictionary<string, bool>(Booleans ?? new(), StringComparer.Ordinal);
        return this;
    }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true)]
[JsonSerializable(typeof(DesktopOnlineState))]
internal sealed partial class DesktopOnlineJsonContext : JsonSerializerContext;
