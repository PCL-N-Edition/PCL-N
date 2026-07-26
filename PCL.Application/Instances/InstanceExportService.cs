// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using PCL.Core.Utils.Hash;

namespace PCL.Application.Instances;

public static class InstanceExportService
{
    private static readonly string[] HostedResourceExtensions =
        [".zip", ".rar", ".jar", ".disabled", ".old"];

    private static readonly string[] HostedResourcePathHints =
        ["mods", "packs", "openloader", "resource"];

    private static readonly string[] RootRuntimeDirectories =
        ["assets", "versions", "libraries"];

    private static readonly string[] RuntimeCacheDirectories =
        ["structureCacheV1", ".fabric", ".git", "avatar-cache", "cosmetic-cache"];

    public static async Task ExportAsync(
        InstanceExportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.InstanceDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.GameDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TargetArchivePath);

        string instanceDirectory = Path.GetFullPath(request.InstanceDirectory);
        string gameDirectory = Path.GetFullPath(request.GameDirectory);
        string targetArchive = Path.GetFullPath(request.TargetArchivePath);
        string targetDirectory = Path.GetDirectoryName(targetArchive) ?? Directory.GetCurrentDirectory();
        Directory.CreateDirectory(targetDirectory);

        string transactionId = Guid.NewGuid().ToString("N");
        string temporaryArchive = Path.Combine(
            targetDirectory,
            $".{Path.GetFileName(targetArchive)}.{transactionId}.tmp");
        string innerArchive = Path.Combine(
            targetDirectory,
            $".{Path.GetFileName(targetArchive)}.{transactionId}.mrpack.tmp");

        try
        {
            HashSet<string> excludedPaths = new(GetPathComparer())
            {
                targetArchive,
                temporaryArchive,
                innerArchive
            };
            ExportRuleSet rules = ExportRuleSet.Create(request.Rules);
            List<SelectedFile> selectedFiles = await Task.Run(
                    () => CollectSelectedFiles(gameDirectory, rules, excludedPaths, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);

            IReadOnlyDictionary<string, InstanceExportHostedFile> hostedFiles =
                await ResolveHostedFilesAsync(request, selectedFiles, cancellationToken).ConfigureAwait(false);
            Dictionary<string, string> dependencies = BuildDependencies(request, instanceDirectory);

            string modrinthArchive = request.IncludeLauncherFiles ? innerArchive : temporaryArchive;
            await Task.Run(
                    () => CreateModrinthArchive(
                        modrinthArchive,
                        instanceDirectory,
                        request,
                        selectedFiles,
                        hostedFiles,
                        dependencies,
                        cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);

            if (request.IncludeLauncherFiles)
            {
                await Task.Run(
                        () => CreateLauncherArchive(
                            temporaryArchive,
                            innerArchive,
                            request,
                            cancellationToken),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryArchive, targetArchive, overwrite: true);
        }
        finally
        {
            TryDeleteFile(temporaryArchive);
            TryDeleteFile(innerArchive);
        }
    }

    public static string EscapeLiteralRulePath(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        StringBuilder escaped = new(relativePath.Length);
        foreach (char value in NormalizeRelativePath(relativePath))
        {
            escaped.Append(value switch
            {
                '[' => "[[]",
                ']' => "[]]",
                '*' => "[*]",
                '?' => "[?]",
                '#' => "[#]",
                _ => value.ToString()
            });
        }
        return escaped.ToString();
    }

    internal static bool RuleMatches(string relativePath, string rule) =>
        ExportRuleSet.IsMatch(NormalizeRelativePath(relativePath), ExportRuleSet.NormalizePattern(rule));

    private static List<SelectedFile> CollectSelectedFiles(
        string gameDirectory,
        ExportRuleSet rules,
        HashSet<string> excludedPaths,
        CancellationToken cancellationToken)
    {
        List<SelectedFile> result = [];
        if (!Directory.Exists(gameDirectory))
            return result;

        Stack<(string Directory, bool IsRoot)> pendingDirectories = [];
        pendingDirectories.Push((gameDirectory, true));
        while (pendingDirectories.TryPop(out (string Directory, bool IsRoot) current))
        {
            foreach (string file in Directory.EnumerateFiles(current.Directory, "*", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string fullPath = Path.GetFullPath(file);
                if (excludedPaths.Contains(fullPath))
                    continue;

                string relativePath = NormalizeRelativePath(Path.GetRelativePath(gameDirectory, fullPath));
                if (rules.ShouldInclude(relativePath))
                    result.Add(new SelectedFile(fullPath, relativePath));
            }

            foreach (string directory in Directory.EnumerateDirectories(
                         current.Directory,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string directoryName = Path.GetFileName(directory);
                if (RuntimeCacheDirectories.Contains(directoryName, StringComparer.OrdinalIgnoreCase) ||
                    current.IsRoot && RootRuntimeDirectories.Contains(directoryName, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                pendingDirectories.Push((directory, false));
            }
        }
        return result;
    }

    private static async Task<IReadOnlyDictionary<string, InstanceExportHostedFile>> ResolveHostedFilesAsync(
        InstanceExportRequest request,
        IReadOnlyList<SelectedFile> selectedFiles,
        CancellationToken cancellationToken)
    {
        if (request.IncludeBundleFiles || request.ResolveHostedFilesAsync is null)
            return new Dictionary<string, InstanceExportHostedFile>(StringComparer.OrdinalIgnoreCase);

        SelectedFile[] targets = selectedFiles.Where(IsHostedResourceCandidate).ToArray();
        if (targets.Length == 0)
            return new Dictionary<string, InstanceExportHostedFile>(StringComparer.OrdinalIgnoreCase);

        using SemaphoreSlim gate = new(Math.Min(4, Math.Max(1, Environment.ProcessorCount)), 4);
        Task<InstanceExportFile>[] tasks = targets.Select(async file =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await CreateExportFileAsync(file, request.ModrinthUploadMode, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }).ToArray();
        InstanceExportFile[] candidates = await Task.WhenAll(tasks).ConfigureAwait(false);
        IReadOnlyDictionary<string, InstanceExportHostedFile> resolved =
            await request.ResolveHostedFilesAsync(candidates, cancellationToken).ConfigureAwait(false);

        HashSet<string> candidatePaths = targets
            .Select(static file => file.RelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, InstanceExportHostedFile> normalized = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string path, InstanceExportHostedFile hosted) in resolved)
        {
            string normalizedPath = NormalizeRelativePath(path);
            if (candidatePaths.Contains(normalizedPath) &&
                hosted.DownloadUrls.Any(static url => !string.IsNullOrWhiteSpace(url)))
            {
                normalized[normalizedPath] = hosted;
            }
        }
        return normalized;
    }

    private static async Task<InstanceExportFile> CreateExportFileAsync(
        SelectedFile file,
        bool modrinthOnly,
        CancellationToken cancellationToken)
    {
        byte[] sha1;
        byte[] sha512;
        byte[] fingerprint;
        await using (FileStream stream = OpenRead(file.FullPath))
            sha1 = await SHA1Provider.Instance.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
        await using (FileStream stream = OpenRead(file.FullPath))
            sha512 = await SHA512Provider.Instance.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
        await using (FileStream stream = OpenRead(file.FullPath))
            fingerprint = await MurmurHash2Provider.Instance.ComputeHashAsync(stream, cancellationToken)
                .ConfigureAwait(false);

        return new InstanceExportFile(
            file.FullPath,
            file.RelativePath,
            new FileInfo(file.FullPath).Length,
            Convert.ToHexStringLower(sha1),
            Convert.ToHexStringLower(sha512),
            BinaryPrimitives.ReadUInt32LittleEndian(fingerprint),
            modrinthOnly);
    }

    private static void CreateModrinthArchive(
        string archivePath,
        string instanceDirectory,
        InstanceExportRequest request,
        IReadOnlyList<SelectedFile> selectedFiles,
        IReadOnlyDictionary<string, InstanceExportHostedFile> hostedFiles,
        IReadOnlyDictionary<string, string> dependencies,
        CancellationToken cancellationToken)
    {
        HashSet<string> addedEntries = new(StringComparer.OrdinalIgnoreCase);
        using ZipArchive archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);

        foreach (SelectedFile file in selectedFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (hostedFiles.ContainsKey(file.RelativePath))
                continue;
            AddFile(archive, file.FullPath, "overrides/" + file.RelativePath, addedEntries);
        }

        string instanceSettings = Path.Combine(instanceDirectory, "PCL");
        if (Directory.Exists(instanceSettings))
        {
            AddDirectory(
                archive,
                instanceSettings,
                "overrides/PCL",
                addedEntries,
                cancellationToken);
        }

        WriteModrinthIndex(
            archive,
            request,
            selectedFiles,
            hostedFiles,
            dependencies,
            cancellationToken);
    }

    private static void CreateLauncherArchive(
        string archivePath,
        string modrinthArchive,
        InstanceExportRequest request,
        CancellationToken cancellationToken)
    {
        string launcher = string.IsNullOrWhiteSpace(request.LauncherExecutablePath)
            ? string.Empty
            : Path.GetFullPath(request.LauncherExecutablePath);
        if (!File.Exists(launcher))
            throw new FileNotFoundException("选择携带启动器时找不到启动器可执行文件。", launcher);

        HashSet<string> addedEntries = new(StringComparer.OrdinalIgnoreCase);
        using ZipArchive archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
        AddFile(archive, modrinthArchive, "modpack.mrpack", addedEntries);
        AddFile(archive, launcher, Path.GetFileName(launcher), addedEntries);

        if (!request.IncludeLauncherCustom || string.IsNullOrWhiteSpace(request.LauncherDataDirectory))
            return;

        string dataDirectory = Path.GetFullPath(request.LauncherDataDirectory);
        foreach (string folderName in new[] { "Pictures", "Musics" })
        {
            cancellationToken.ThrowIfCancellationRequested();
            string folder = Path.Combine(dataDirectory, folderName);
            if (Directory.Exists(folder))
                AddDirectory(archive, folder, "PCL/" + folderName, addedEntries, cancellationToken);
        }
        foreach (string fileName in new[] { "Custom.xaml", "Setup.ini", "hints.txt", "Logo.png" })
        {
            cancellationToken.ThrowIfCancellationRequested();
            string file = Path.Combine(dataDirectory, fileName);
            if (File.Exists(file))
                AddFile(archive, file, "PCL/" + fileName, addedEntries);
        }
    }

    private static void WriteModrinthIndex(
        ZipArchive archive,
        InstanceExportRequest request,
        IReadOnlyList<SelectedFile> selectedFiles,
        IReadOnlyDictionary<string, InstanceExportHostedFile> hostedFiles,
        IReadOnlyDictionary<string, string> dependencies,
        CancellationToken cancellationToken)
    {
        ZipArchiveEntry entry = archive.CreateEntry("modrinth.index.json", CompressionLevel.Fastest);
        using Stream stream = entry.Open();
        using Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteString("game", "minecraft");
        writer.WriteNumber("formatVersion", 1);
        writer.WriteString("versionId", NormalizePackageValue(request.PackageVersion, "1.0.0"));
        writer.WriteString("name", NormalizePackageValue(request.PackageName, "Minecraft Modpack"));
        writer.WriteString("summary", request.Summary ?? string.Empty);
        writer.WriteStartArray("files");
        foreach (SelectedFile file in selectedFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!hostedFiles.TryGetValue(file.RelativePath, out InstanceExportHostedFile? hosted))
                continue;

            InstanceExportFile metadata = CreateExportFileAsync(file, request.ModrinthUploadMode, cancellationToken)
                .GetAwaiter().GetResult();
            writer.WriteStartObject();
            writer.WriteString("path", file.RelativePath);
            writer.WriteStartObject("hashes");
            writer.WriteString("sha1", metadata.Sha1);
            writer.WriteString("sha512", metadata.Sha512);
            writer.WriteEndObject();
            writer.WriteStartArray("downloads");
            foreach (string url in hosted.DownloadUrls
                         .Where(static url => !string.IsNullOrWhiteSpace(url))
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderByDescending(static url => url.Contains("modrinth.com", StringComparison.OrdinalIgnoreCase)))
            {
                writer.WriteStringValue(url);
            }
            writer.WriteEndArray();
            writer.WriteNumber("fileSize", metadata.Size);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteStartObject("dependencies");
        foreach ((string key, string value) in dependencies.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
                writer.WriteString(key, value);
        }
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static Dictionary<string, string> BuildDependencies(
        InstanceExportRequest request,
        string instanceDirectory)
    {
        Dictionary<string, string> result = new(request.Dependencies, StringComparer.OrdinalIgnoreCase);
        if (!result.TryGetValue("minecraft", out string? minecraft) || string.IsNullOrWhiteSpace(minecraft))
            result["minecraft"] = TryReadMinecraftVersion(instanceDirectory) ?? Path.GetFileName(instanceDirectory);
        return result;
    }

    private static string? TryReadMinecraftVersion(string instanceDirectory)
    {
        string name = Path.GetFileName(instanceDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        string jsonPath = Path.Combine(instanceDirectory, name + ".json");
        if (!File.Exists(jsonPath))
            return null;
        try
        {
            using FileStream stream = File.OpenRead(jsonPath);
            using JsonDocument document = JsonDocument.Parse(stream);
            JsonElement root = document.RootElement;
            foreach (string propertyName in new[] { "inheritsFrom", "clientVersion", "id" })
            {
                if (root.TryGetProperty(propertyName, out JsonElement value) &&
                    value.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(value.GetString()))
                {
                    return value.GetString();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        return null;
    }

    private static bool IsHostedResourceCandidate(SelectedFile file)
    {
        string extension = Path.GetExtension(file.FullPath);
        if (!HostedResourceExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            return false;
        string[] segments = file.RelativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Any(segment => HostedResourcePathHints.Any(
            hint => segment.Contains(hint, StringComparison.OrdinalIgnoreCase)));
    }

    private static void AddDirectory(
        ZipArchive archive,
        string rootDirectory,
        string archiveRoot,
        HashSet<string> addedEntries,
        CancellationToken cancellationToken)
    {
        foreach (string file in Directory.EnumerateFiles(rootDirectory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relativePath = NormalizeRelativePath(Path.GetRelativePath(rootDirectory, file));
            AddFile(archive, file, CombineArchivePath(archiveRoot, relativePath), addedEntries);
        }
    }

    private static void AddFile(
        ZipArchive archive,
        string file,
        string entryName,
        HashSet<string> addedEntries)
    {
        string normalizedEntry = NormalizeRelativePath(entryName);
        if (!addedEntries.Add(normalizedEntry))
            return;
        archive.CreateEntryFromFile(file, normalizedEntry, CompressionLevel.Fastest);
    }

    private static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static string NormalizePackageValue(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string CombineArchivePath(string left, string right) =>
        string.IsNullOrWhiteSpace(left)
            ? NormalizeRelativePath(right)
            : NormalizeRelativePath(left) + "/" + NormalizeRelativePath(right);

    private static string NormalizeRelativePath(string path) =>
        path.Replace('\\', '/').TrimStart('/');

    private static StringComparer GetPathComparer() =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed record SelectedFile(string FullPath, string RelativePath);

    private sealed class ExportRuleSet
    {
        private readonly IReadOnlyList<Rule> _rules;

        private ExportRuleSet(IReadOnlyList<Rule> rules)
        {
            _rules = rules;
        }

        public static ExportRuleSet Create(IEnumerable<string> rules)
        {
            ArgumentNullException.ThrowIfNull(rules);
            List<Rule> result = [];
            foreach (string rawRule in rules)
            {
                string value = rawRule.Trim();
                if (value.Length == 0 || value[0] is '#' or '=')
                    continue;

                bool include = value[0] != '!';
                string pattern = NormalizePattern(include ? value : value[1..]);
                if (pattern.Length > 0)
                    result.Add(new Rule(pattern, include));
            }
            return new ExportRuleSet(result);
        }

        public bool ShouldInclude(string relativePath)
        {
            bool included = false;
            string path = NormalizeRelativePath(relativePath);
            foreach (Rule rule in _rules)
            {
                if (IsMatch(path, rule.Pattern))
                    included = rule.Include;
            }
            return included;
        }

        internal static string NormalizePattern(string rawPattern)
        {
            string pattern = NormalizeRelativePath(rawPattern.Trim());
            return pattern.EndsWith('/') ? pattern + "**" : pattern;
        }

        internal static bool IsMatch(string input, string pattern)
        {
            Dictionary<(int Pattern, int Input), bool> memo = [];
            return Match(0, 0);

            bool Match(int patternIndex, int inputIndex)
            {
                if (memo.TryGetValue((patternIndex, inputIndex), out bool cached))
                    return cached;

                bool result;
                if (patternIndex == pattern.Length)
                {
                    result = inputIndex == input.Length;
                }
                else if (pattern[patternIndex] == '*')
                {
                    bool recursive = patternIndex + 1 < pattern.Length && pattern[patternIndex + 1] == '*';
                    int nextPattern = patternIndex + (recursive ? 2 : 1);
                    if (recursive && nextPattern < pattern.Length && pattern[nextPattern] == '/')
                    {
                        int nextSeparator = input.IndexOf('/', inputIndex);
                        result = Match(nextPattern + 1, inputIndex) ||
                                 (nextSeparator >= 0 && Match(patternIndex, nextSeparator + 1));
                    }
                    else
                    {
                        result = Match(nextPattern, inputIndex) ||
                                 (inputIndex < input.Length &&
                                  (recursive || input[inputIndex] != '/') &&
                                  Match(patternIndex, inputIndex + 1));
                    }
                }
                else if (inputIndex == input.Length)
                {
                    result = false;
                }
                else if (pattern[patternIndex] == '?')
                {
                    result = input[inputIndex] != '/' && Match(patternIndex + 1, inputIndex + 1);
                }
                else if (pattern[patternIndex] == '#')
                {
                    result = char.IsAsciiDigit(input[inputIndex]) && Match(patternIndex + 1, inputIndex + 1);
                }
                else if (pattern[patternIndex] == '[' &&
                         TryReadCharacterClass(pattern, patternIndex, out int nextPattern, out string characters))
                {
                    result = CharacterClassContains(characters, input[inputIndex]) &&
                             Match(nextPattern, inputIndex + 1);
                }
                else
                {
                    result = EqualsIgnoreCase(pattern[patternIndex], input[inputIndex]) &&
                             Match(patternIndex + 1, inputIndex + 1);
                }

                memo[(patternIndex, inputIndex)] = result;
                return result;
            }
        }

        private static bool TryReadCharacterClass(
            string pattern,
            int start,
            out int nextPattern,
            out string characters)
        {
            int contentStart = start + 1;
            int searchStart = contentStart < pattern.Length && pattern[contentStart] == ']'
                ? contentStart + 1
                : contentStart;
            int end = pattern.IndexOf(']', searchStart);
            if (end < 0)
            {
                nextPattern = start;
                characters = string.Empty;
                return false;
            }

            nextPattern = end + 1;
            characters = pattern[contentStart..end];
            return characters.Length > 0;
        }

        private static bool CharacterClassContains(string characters, char value)
        {
            if (value == '/')
                return false;

            bool negated = characters.Length > 1 && characters[0] is '!' or '^';
            bool contains = false;
            for (int index = negated ? 1 : 0; index < characters.Length; index++)
            {
                if (index + 2 < characters.Length && characters[index + 1] == '-')
                {
                    char lower = char.ToUpperInvariant(characters[index]);
                    char upper = char.ToUpperInvariant(characters[index + 2]);
                    char candidate = char.ToUpperInvariant(value);
                    if (candidate >= lower && candidate <= upper)
                        contains = true;
                    index += 2;
                    continue;
                }
                if (EqualsIgnoreCase(characters[index], value))
                    contains = true;
            }
            return negated ? !contains : contains;
        }

        private static bool EqualsIgnoreCase(char left, char right) =>
            char.ToUpperInvariant(left) == char.ToUpperInvariant(right);

        private sealed record Rule(string Pattern, bool Include);
    }
}
