// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using fNbt;
using System.Collections.Concurrent;

namespace PCL.Application.Instances;

public sealed class MinecraftServerListService
{
    private const int ReplaceAttemptCount = 6;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> AccessLocks = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public static async Task<IReadOnlyList<MinecraftServerEntry>> LoadAsync(
        string minecraftRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(minecraftRoot);

        string serversFile = GetServersFile(minecraftRoot);
        SemaphoreSlim accessLock = GetAccessLock(serversFile);
        await accessLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(
                    () => LoadEntries(serversFile, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            accessLock.Release();
        }
    }

    public static async Task AddAsync(
        string minecraftRoot,
        MinecraftServerEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(minecraftRoot);
        ArgumentNullException.ThrowIfNull(entry);
        if (string.IsNullOrWhiteSpace(entry.Name))
            throw new ArgumentException("服务器名称不能为空。", nameof(entry));
        if (string.IsNullOrWhiteSpace(entry.Address))
            throw new ArgumentException("服务器地址不能为空。", nameof(entry));

        string serversFile = GetServersFile(minecraftRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(serversFile)
                                  ?? throw new InvalidOperationException("服务器列表文件没有父目录。"));

        SemaphoreSlim accessLock = GetAccessLock(serversFile);
        await accessLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(
                () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    NbtFile nbtFile = File.Exists(serversFile) ? LoadExistingServerFile(serversFile) : CreateEmptyServerFile();
                    NbtList servers = nbtFile.RootTag.Get<NbtList>("servers") ?? new NbtList("servers", NbtTagType.Compound);
                    if (servers.Parent is null)
                        nbtFile.RootTag.Add(servers);

                    servers.Add(new NbtCompound
                    {
                        new NbtString("name", entry.Name.Trim()),
                        new NbtString("ip", entry.Address.Trim())
                    });
                    if (!string.IsNullOrWhiteSpace(entry.Icon))
                        ((NbtCompound)servers[^1]).Add(new NbtString("icon", entry.Icon));

                    SaveServerFile(serversFile, nbtFile, cancellationToken);
                },
                cancellationToken)
            .ConfigureAwait(false);
        }
        finally
        {
            accessLock.Release();
        }
    }

    public static Task<bool> UpdateAsync(
        string minecraftRoot,
        MinecraftServerEntry original,
        MinecraftServerEntry updated,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(original);
        ValidateEntry(updated, nameof(updated));
        return MutateAsync(
            minecraftRoot,
            servers =>
            {
                int index = FindServerIndex(servers, original);
                if (index < 0 || servers[index] is not NbtCompound server)
                    return false;

                server["name"] = new NbtString("name", updated.Name.Trim());
                server["ip"] = new NbtString("ip", updated.Address.Trim());
                if (!string.IsNullOrWhiteSpace(updated.Icon))
                    server["icon"] = new NbtString("icon", updated.Icon);
                return true;
            },
            cancellationToken);
    }

    public static Task<bool> RemoveAsync(
        string minecraftRoot,
        MinecraftServerEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return MutateAsync(
            minecraftRoot,
            servers =>
            {
                int index = FindServerIndex(servers, entry);
                if (index < 0)
                    return false;

                servers.RemoveAt(index);
                return true;
            },
            cancellationToken);
    }

    private static async Task<bool> MutateAsync(
        string minecraftRoot,
        Func<NbtList, bool> mutate,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(minecraftRoot);
        ArgumentNullException.ThrowIfNull(mutate);
        string serversFile = GetServersFile(minecraftRoot);
        SemaphoreSlim accessLock = GetAccessLock(serversFile);
        await accessLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(
                () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!File.Exists(serversFile))
                        return false;

                    NbtFile nbtFile = LoadExistingServerFile(serversFile);
                    NbtList? servers = nbtFile.RootTag.Get<NbtList>("servers");
                    if (servers is null || !mutate(servers))
                        return false;

                    SaveServerFile(serversFile, nbtFile, cancellationToken);
                    return true;
                },
                cancellationToken)
            .ConfigureAwait(false);
        }
        finally
        {
            accessLock.Release();
        }
    }

    private static string GetServersFile(string minecraftRoot) =>
        Path.Combine(Path.GetFullPath(minecraftRoot), "servers.dat");

    private static SemaphoreSlim GetAccessLock(string serversFile) =>
        AccessLocks.GetOrAdd(serversFile, static _ => new SemaphoreSlim(1, 1));

    private static List<MinecraftServerEntry> LoadEntries(
        string serversFile,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(serversFile))
            return [];

        NbtFile nbtFile = LoadExistingServerFile(serversFile);
        NbtList? servers = nbtFile.RootTag.Get<NbtList>("servers");
        if (servers is null)
            return [];

        List<MinecraftServerEntry> result = new(servers.Count);
        foreach (NbtTag tag in servers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (tag is not NbtCompound server)
                continue;

            string name = server.Get<NbtString>("name")?.Value ?? "Unknown";
            string address = server.Get<NbtString>("ip")?.Value ?? "Unknown";
            string? icon = server.Get<NbtString>("icon")?.Value;
            result.Add(new MinecraftServerEntry(name, address, icon));
        }

        return result;
    }

    private static void SaveServerFile(
        string serversFile,
        NbtFile nbtFile,
        CancellationToken cancellationToken)
    {
        string directory = Path.GetDirectoryName(serversFile)
                           ?? throw new InvalidOperationException("服务器列表文件没有父目录。");
        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(serversFile)}.{Guid.NewGuid():N}.tmp");
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
                nbtFile.SaveToStream(stream, NbtCompression.GZip);
                stream.Flush(flushToDisk: true);
            }

            Exception? lastException = null;
            for (int attempt = 1; attempt <= ReplaceAttemptCount; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    File.Move(temporaryPath, serversFile, overwrite: true);
                    temporaryPath = string.Empty;
                    return;
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                    lastException = exception;
                    if (attempt < ReplaceAttemptCount)
                        Thread.Sleep(TimeSpan.FromMilliseconds(25 * attempt));
                }
            }

            throw new IOException(
                $"Unable to replace Minecraft server list '{serversFile}' after {ReplaceAttemptCount} attempts.",
                lastException);
        }
        finally
        {
            if (!string.IsNullOrEmpty(temporaryPath))
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }

    private static int FindServerIndex(NbtList servers, MinecraftServerEntry entry)
    {
        for (int index = 0; index < servers.Count; index++)
        {
            if (servers[index] is not NbtCompound server)
                continue;

            string name = server.Get<NbtString>("name")?.Value ?? string.Empty;
            string address = server.Get<NbtString>("ip")?.Value ?? string.Empty;
            if (string.Equals(name, entry.Name, StringComparison.Ordinal) &&
                string.Equals(address, entry.Address, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private static void ValidateEntry(MinecraftServerEntry entry, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(entry, parameterName);
        if (string.IsNullOrWhiteSpace(entry.Name))
            throw new ArgumentException("服务器名称不能为空。", parameterName);
        if (string.IsNullOrWhiteSpace(entry.Address))
            throw new ArgumentException("服务器地址不能为空。", parameterName);
    }

    private static NbtFile LoadExistingServerFile(string serversFile)
    {
        try
        {
            using FileStream stream = new(
                serversFile,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            NbtFile nbtFile = new();
            nbtFile.LoadFromStream(stream, NbtCompression.AutoDetect);
            return nbtFile.RootTag is null ? CreateEmptyServerFile() : nbtFile;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return CreateEmptyServerFile();
        }
    }

    private static NbtFile CreateEmptyServerFile()
    {
        NbtCompound rootTag = new("");
        rootTag.Add(new NbtList("servers", NbtTagType.Compound));
        return new NbtFile(rootTag);
    }
}
