// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using PCL.Core.Logging;
using PCL.Platform.Paths;

namespace PCL.Desktop.Features.Launching;

/// <summary>
/// Offline rewrite of <c>com.mojang:authlib</c> jars (ASM string/method patches) then
/// classpath swap — the durable alternative to authlib-injector javaagent / JVMTI.
/// </summary>
internal static class AuthlibJarPatcher
{
    public static bool IsAuthlibJarPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        string name = Path.GetFileName(path.Trim().Trim('"'));
        // com.mojang:authlib artifacts are always named authlib-<version>.jar
        return name.StartsWith("authlib-", StringComparison.OrdinalIgnoreCase) &&
               name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) &&
               !name.Contains("injector", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns a rewritten jar path, or the original path if patching is unnecessary/failed.
    /// </summary>
    public static string EnsurePatched(string originalJarPath, AuthlibPatchProfile profile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(originalJarPath);
        ArgumentNullException.ThrowIfNull(profile);

        string fullOriginal = Path.GetFullPath(originalJarPath);
        if (!File.Exists(fullOriginal))
            return originalJarPath;

        string cacheDir = GetCacheDirectory();
        Directory.CreateDirectory(cacheDir);

        // profile.CacheKey already embeds AuthlibClassTransformer.PatchRevision.
        string keyMaterial = fullOriginal + "|" +
                             new FileInfo(fullOriginal).Length + "|" +
                             new FileInfo(fullOriginal).LastWriteTimeUtc.Ticks + "|" +
                             profile.CacheKey;
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(keyMaterial)))
            .ToLowerInvariant()[..16];
        string fileName = Path.GetFileNameWithoutExtension(fullOriginal) + "-pcln-" + hash + ".jar";
        string target = Path.Combine(cacheDir, fileName);
        if (File.Exists(target) && new FileInfo(target).Length > 32)
            return target;

        try
        {
            PatchJar(fullOriginal, target, profile);
            PortableLog.Info(
                "AuthlibPatch",
                $"已生成修补后的 Authlib：{Path.GetFileName(fullOriginal)} → {fileName}（{profile.CacheKey}）。");
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                                       NotSupportedException or ArgumentException)
        {
            PortableLog.Warn(ex, "AuthlibPatch", $"修补 Authlib 失败，回退原 jar：{fullOriginal}");
            TryDelete(target);
            return originalJarPath;
        }
    }

    public static string[] RewriteClasspath(IReadOnlyList<string> classpath, AuthlibPatchProfile profile)
    {
        ArgumentNullException.ThrowIfNull(classpath);
        ArgumentNullException.ThrowIfNull(profile);

        string[] result = new string[classpath.Count];
        for (int i = 0; i < classpath.Count; i++)
        {
            string entry = classpath[i];
            result[i] = IsAuthlibJarPath(entry) ? EnsurePatched(entry, profile) : entry;
        }

        return result;
    }

    /// <summary>Replaces authlib paths inside a full JVM command line / -cp string.</summary>
    public static string RewriteArgumentString(string arguments, IReadOnlyList<string> originalClasspath, string[] patchedClasspath)
    {
        if (string.IsNullOrEmpty(arguments) || originalClasspath.Count != patchedClasspath.Length)
            return arguments;

        string result = arguments;
        for (int i = 0; i < originalClasspath.Count; i++)
        {
            if (string.Equals(originalClasspath[i], patchedClasspath[i], StringComparison.Ordinal))
                continue;
            result = ReplacePathToken(result, originalClasspath[i], patchedClasspath[i]);
        }

        return result;
    }

    public static string StripJavaAgentArguments(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments) ||
            !arguments.Contains("-javaagent:", StringComparison.OrdinalIgnoreCase))
        {
            return arguments;
        }

        // Remove -javaagent:...authlib... tokens and related -Dauthlibinjector.* properties.
        List<string> tokens = SplitArgumentsPreservingQuotes(arguments);
        List<string> kept = [];
        foreach (string token in tokens)
        {
            if (token.StartsWith("-javaagent:", StringComparison.OrdinalIgnoreCase) &&
                token.Contains("authlib", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (token.StartsWith("-Dauthlibinjector.", StringComparison.OrdinalIgnoreCase))
                continue;

            kept.Add(NeedsQuotes(token) ? Quote(token) : token);
        }

        return string.Join(' ', kept);
    }

    public static string[] StripJavaAgentVmArguments(IReadOnlyList<string> vmArguments)
    {
        return vmArguments
            .Where(static argument =>
                !(argument.StartsWith("-javaagent:", StringComparison.OrdinalIgnoreCase) &&
                  argument.Contains("authlib", StringComparison.OrdinalIgnoreCase)) &&
                !argument.StartsWith("-Dauthlibinjector.", StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    private static void PatchJar(string sourcePath, string targetPath, AuthlibPatchProfile profile)
    {
        string temp = targetPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using ZipArchive source = ZipFile.OpenRead(sourcePath);
            using FileStream output = new(temp, FileMode.Create, FileAccess.Write, FileShare.None);
            using ZipArchive dest = new(output, ZipArchiveMode.Create);

            int transformed = 0;
            foreach (ZipArchiveEntry entry in source.Entries)
            {
                ZipArchiveEntry created = dest.CreateEntry(entry.FullName, CompressionLevel.Optimal);
                using Stream entryIn = entry.Open();
                using MemoryStream buffer = new();
                entryIn.CopyTo(buffer);
                byte[] bytes = buffer.ToArray();

                if (entry.FullName.EndsWith(".class", StringComparison.OrdinalIgnoreCase) &&
                    entry.FullName.Contains("com/mojang/authlib/", StringComparison.OrdinalIgnoreCase))
                {
                    string className = entry.FullName
                        .Replace('\\', '/')
                        .TrimStart('/')
                        [..^".class".Length]
                        .Replace('/', '.');
                    byte[]? patched = AuthlibClassTransformer.Transform(className, bytes, profile);
                    if (patched is not null)
                    {
                        bytes = patched;
                        transformed++;
                    }
                }

                using Stream entryOut = created.Open();
                entryOut.Write(bytes, 0, bytes.Length);
            }

            if (transformed == 0)
                throw new InvalidDataException("Authlib jar 中没有可修补的 class（可能不是 Mojang authlib）。");
        }
        catch
        {
            TryDelete(temp);
            throw;
        }

        File.Move(temp, targetPath, overwrite: true);
    }

    private static string GetCacheDirectory()
    {
        DefaultPlatformPathProvider paths = new();
        return Path.Combine(paths.ApplicationDataDirectory, "PCL-N", "patched-authlib");
    }

    private static string ReplacePathToken(string arguments, string oldPath, string newPath)
    {
        string result = arguments.Replace(oldPath, newPath, StringComparison.Ordinal);
        // Quoted Windows paths in -cp lists
        string oldQuoted = "\"" + oldPath + "\"";
        string newQuoted = "\"" + newPath + "\"";
        if (result.Contains(oldQuoted, StringComparison.Ordinal))
            result = result.Replace(oldQuoted, newQuoted, StringComparison.Ordinal);
        return result;
    }

    private static List<string> SplitArgumentsPreservingQuotes(string arguments)
    {
        List<string> tokens = [];
        StringBuilder current = new();
        bool inQuotes = false;
        for (int i = 0; i < arguments.Length; i++)
        {
            char c = arguments[i];
            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
            tokens.Add(current.ToString());
        return tokens;
    }

    private static bool NeedsQuotes(string token) =>
        token.Contains(' ', StringComparison.Ordinal) || token.Contains('\t', StringComparison.Ordinal);

    private static string Quote(string token) => "\"" + token + "\"";

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // ignore
        }
    }
}
