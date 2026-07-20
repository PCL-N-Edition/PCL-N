// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.Desktop.Diagnostics;

/// <summary>
/// Short-TTL cache for File/Directory.Exists. UI sampling showed Exists as a top exclusive cost
/// during logo/instance probes; animations must not wait on repeated kernel path checks.
/// </summary>
internal static class PathExistenceCache
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Entry> Cache = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private const int TtlMs = 2500;
    private const int MaxEntries = 512;

    public static bool FileExists(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        long now = Environment.TickCount64;
        lock (Gate)
        {
            if (Cache.TryGetValue(path, out Entry entry) &&
                entry.IsFile &&
                now - entry.CheckedAtMs >= 0 &&
                now - entry.CheckedAtMs < TtlMs)
            {
                return entry.Exists;
            }
        }

        bool exists = File.Exists(path);
        Store(path, isFile: true, exists, now);
        return exists;
    }

    public static bool DirectoryExists(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        long now = Environment.TickCount64;
        lock (Gate)
        {
            if (Cache.TryGetValue(path, out Entry entry) &&
                !entry.IsFile &&
                now - entry.CheckedAtMs >= 0 &&
                now - entry.CheckedAtMs < TtlMs)
            {
                return entry.Exists;
            }
        }

        bool exists = Directory.Exists(path);
        Store(path, isFile: false, exists, now);
        return exists;
    }

    public static void Invalidate(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        lock (Gate)
            Cache.Remove(path);
    }

    private static void Store(string path, bool isFile, bool exists, long now)
    {
        lock (Gate)
        {
            if (Cache.Count >= MaxEntries)
                Cache.Clear();
            Cache[path] = new Entry(isFile, exists, now);
        }
    }

    private readonly record struct Entry(bool IsFile, bool Exists, long CheckedAtMs);
}
