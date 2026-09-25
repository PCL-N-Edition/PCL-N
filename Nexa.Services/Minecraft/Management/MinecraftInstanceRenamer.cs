using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Nexa.Services.Minecraft.Process;
using Nexa.Services.Settings;
using Nexa.Xsr.State;

namespace Nexa.Services.Minecraft.Management;

internal static class MinecraftInstanceRenamer
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    internal static void EnsureIdle(string root, XsrStateStore store, CancellationToken token)
    {
        string versions = Path.Combine(Path.GetFullPath(root), "versions");
        if (store.TryResolve(MinecraftProcessStateComposition.SessionsKey, out var state)
            && store.ReadCollection<MinecraftProcessSnapshot>(state, cancellationToken: token).Items.Any(item =>
                item.State is MinecraftProcessState.Created or MinecraftProcessState.Running
                && !string.IsNullOrEmpty(item.InstanceDirectory)
                && Path.GetRelativePath(versions, item.InstanceDirectory) is { } relative
                && !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
            throw new InvalidOperationException("此游戏目录中有版本正在运行，请结束游戏后再修改。");
    }

    internal static async Task RenameAsync(string root, string oldName, string newName, string gameVersion, string fingerprint,
        SettingsPolicyService settings, XsrStateStore store, CancellationToken token)
    {
        if (!MinecraftVersionPaths.IsSafeReference(oldName) || !MinecraftVersionPaths.IsSafeReference(newName)
            || newName.TrimEnd(' ', '.') != newName || newName.Any(c => c is '<' or '>' or '"' or '|' or '?' or '*'))
            throw new InvalidDataException("版本名称包含无效字符。");
        if (oldName == newName) return;
        using var recoveryOperation = await InstanceRecoveryOperationGate.EnterOperationAsync(Path.GetFullPath(root), token).ConfigureAwait(false);
        await Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            root = Path.GetFullPath(root);
            string versions = Path.Combine(root, "versions");
            string source = Path.Combine(versions, oldName), destination = Path.Combine(versions, newName);
            RecoveryBlobStore.CheckLinks(source); RecoveryBlobStore.CheckLinks(destination);
            bool samePath = MinecraftLibraryService.PathComparer.Equals(source, destination);
            if (!samePath && Path.Exists(destination)) throw new IOException("已有同名版本，未覆盖任何文件。");
            EnsureIdle(root, store, token);
            string manifest = Path.Combine(source, oldName + ".json");
            var edits = new List<(string OriginalPath, string TargetPath, byte[] Before, byte[] After)>();
            int count = 0; long bytesRead = 0;
            foreach (string directory in Directory.EnumerateDirectories(versions))
            {
                if (++count > 10000) throw new InvalidDataException("版本枚举超过限制。");
                RecoveryBlobStore.CheckLinks(directory);
                foreach (string file in Directory.EnumerateFiles(directory, "*.json"))
                {
                    token.ThrowIfCancellationRequested();
                    if (++count > 10000) throw new InvalidDataException("版本清单枚举超过限制。");
                    RecoveryBlobStore.CheckLinks(file);
                    byte[] before = await ReadManifestAsync(file, token).ConfigureAwait(false);
                    if ((bytesRead += before.Length) > 64 * 1024 * 1024) throw new InvalidDataException("版本清单总读取预算超限。");
                    bool primary = MinecraftLibraryService.PathComparer.Equals(file, manifest);
                    if (primary && Convert.ToHexString(SHA256.HashData(before)) != fingerprint) throw new IOException("原版本已更改，请重新打开修改页。");
                    JsonObject? json;
                    try { json = JsonNode.Parse(before) as JsonObject; }
                    catch (System.Text.Json.JsonException) when (!primary) { continue; }
                    if (json is null) { if (primary) throw new InvalidDataException("原版本清单无效。"); continue; }
                    bool changed = primary;
                    if (primary) { json["id"] = newName; json["_minecraftVersion"] = gameVersion; }
                    foreach (string key in new[] { "inheritsFrom", "jar" })
                        if (json[key] is JsonValue reference && reference.TryGetValue<string>(out var value) && string.Equals(value, oldName, StringComparison.OrdinalIgnoreCase))
                        { json[key] = newName; changed = true; }
                    if (json["_nexaInstall"]?["managedMods"] is JsonArray mods)
                        foreach (var mod in mods.OfType<JsonObject>())
                            if (mod["path"]?.GetValue<string>() is { } path && path.StartsWith("versions/" + oldName + "/", StringComparison.Ordinal))
                            { mod["path"] = "versions/" + newName + path[("versions/" + oldName).Length..]; changed = true; }
                    if (!changed) continue;
                    string target = primary ? Path.Combine(destination, newName + ".json")
                        : MinecraftLibraryService.PathComparer.Equals(directory, source) ? Path.Combine(destination, Path.GetFileName(file)) : file;
                    edits.Add((file, target, before, System.Text.Encoding.UTF8.GetBytes(json.ToJsonString())));
                }
            }
            if (!edits.Any(item => MinecraftLibraryService.PathComparer.Equals(item.OriginalPath, manifest))) throw new FileNotFoundException("原版本清单不存在。", manifest);
            if (!samePath && File.Exists(Path.Combine(source, newName + ".json"))) throw new IOException("版本目录中已有目标名称的清单。");
            string oldJar = Path.Combine(source, oldName + ".jar");
            bool hasJar = File.Exists(oldJar);
            RecoveryBlobStore.CheckLinks(oldJar);
            if (hasJar && !samePath && File.Exists(Path.Combine(source, newName + ".jar"))) throw new IOException("版本目录中已有目标名称的核心文件。");
            string transaction = Path.Combine(root, ".nexa-rename", Guid.NewGuid().ToString("N"));
            RecoveryBlobStore.CheckLinks(transaction); Directory.CreateDirectory(transaction);
            for (int i = 0; i < edits.Count; i++) await File.WriteAllBytesAsync(Path.Combine(transaction, i + ".json.backup"), edits[i].Before, token).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(transaction, "paths.txt"), string.Join('\n', edits.Select(item => item.OriginalPath)), token).ConfigureAwait(false);
            string transit = Path.Combine(transaction, "instance");
            bool moved = false, jarMoved = false, manifestMoved = false, committed = false, reverted = false;
            var temporaryFiles = new List<string>();
            try
            {
                EnsureIdle(root, store, token); token.ThrowIfCancellationRequested();
                foreach (var edit in edits)
                    if (!(await ReadManifestAsync(edit.OriginalPath, token).ConfigureAwait(false)).AsSpan().SequenceEqual(edit.Before)) throw new IOException("版本清单已改变，请重试。");
                Directory.Move(source, transit); moved = true;
                Directory.Move(transit, destination);
                File.Move(Path.Combine(destination, oldName + ".json"), Path.Combine(transaction, "original.json")); manifestMoved = true;
                if (hasJar)
                {
                    File.Move(Path.Combine(destination, oldName + ".jar"), Path.Combine(transaction, "client.jar"));
                    jarMoved = true;
                    File.Move(Path.Combine(transaction, "client.jar"), Path.Combine(destination, newName + ".jar"));
                }
                foreach (var edit in edits)
                {
                    string temporary = edit.TargetPath + ".nexa-" + Guid.NewGuid().ToString("N") + ".tmp";
                    RecoveryBlobStore.CheckLinks(edit.TargetPath);
                    temporaryFiles.Add(temporary);
                    await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        await output.WriteAsync(edit.After, token).ConfigureAwait(false);
                    File.Move(temporary, edit.TargetPath, true);
                }
                var result = settings.MoveInstanceSettings(source, destination);
                if (!result.IsSuccess) throw new IOException(result.Error?.Message ?? "无法迁移实例设置。");
                committed = true;
            }
            finally
            {
                if (!committed && moved)
                {
                    string current = Directory.Exists(transit) ? transit : destination;
                    foreach (var edit in edits)
                    {
                        string target = MinecraftLibraryService.PathComparer.Equals(Path.GetDirectoryName(edit.OriginalPath), source)
                            ? Path.Combine(current, Path.GetFileName(edit.OriginalPath)) : edit.OriginalPath;
                        File.WriteAllBytes(target, edit.Before);
                    }
                    if (manifestMoved && !samePath) File.Delete(Path.Combine(current, newName + ".json"));
                    if (jarMoved)
                    {
                        string jar = File.Exists(Path.Combine(transaction, "client.jar")) ? Path.Combine(transaction, "client.jar") : Path.Combine(current, newName + ".jar");
                        string temporaryJar = Path.Combine(transaction, "rollback.jar");
                        File.Move(jar, temporaryJar);
                        File.Move(temporaryJar, Path.Combine(current, oldName + ".jar"));
                    }
                    if (current != transit) Directory.Move(current, transit);
                    Directory.Move(transit, source);
                    reverted = true;
                }
                if (committed || reverted || !moved)
                {
                    foreach (string file in temporaryFiles) try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                    try { Directory.Delete(transaction, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            }
        }
        finally { Gate.Release(); }
    }

    private static async Task<byte[]> ReadManifestAsync(string path, CancellationToken token)
    {
        RecoveryBlobStore.CheckLinks(path);
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        if (input.Length > 4 * 1024 * 1024) throw new InvalidDataException("版本清单过大，无法安全改名。");
        byte[] buffer = new byte[(int)input.Length + 1];
        int total = 0, read;
        while (total < buffer.Length && (read = await input.ReadAsync(buffer.AsMemory(total), token).ConfigureAwait(false)) > 0) total += read;
        if (total != buffer.Length - 1) throw new IOException("版本清单读取期间发生变化。");
        return buffer[..total];
    }
}
