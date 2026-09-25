using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nexa.Services.Settings;

namespace Nexa.Services.Minecraft.Management;

internal sealed record RenameStep(string Kind, string Source, string Target, byte[]? Before, byte[]? After, string? Hash);
internal sealed record RenamePlan(int Schema, string Root, string Source, string Destination, string Directory, string Marker,
    RenameStep[] Steps, RenameSettingsPlan Settings);
internal sealed record RenameProgress(string Plan, string Phase, int Attempted);

internal sealed class InstanceRenameJournal
{
    private readonly RenamePlan _plan;
    private readonly string _hash;
    private InstanceRenameJournal(RenamePlan plan, string hash) { _plan = plan; _hash = hash; }
    internal string Destination => _plan.Destination;
    internal static async Task<InstanceRenameJournal> PrepareAsync(string root, string transaction, string source, string destination,
        string oldName, string newName, bool hasJar,
        IReadOnlyList<(string OriginalPath, string TargetPath, byte[] Before, byte[] After)> edits,
        SettingsPolicyService settings, CancellationToken token)
    {
        RecoveryBlobStore.CheckLinks(transaction); Directory.CreateDirectory(transaction);
        string marker = ".nexa-rename-" + Guid.NewGuid().ToString("N");
        string transit = Path.Combine(transaction, "instance");
        List<RenameStep> steps = [new("write", "", Path.Combine(source, marker), null, "owned"u8.ToArray(), null),
            new("directory", source, transit, null, null, null), new("directory", transit, destination, null, null, null)];
        string primary = Path.Combine(source, oldName + ".json");
        var original = edits.Single(edit => MinecraftLibraryService.PathComparer.Equals(edit.OriginalPath, primary));
        steps.Add(new("file", Path.Combine(destination, oldName + ".json"), Path.Combine(transaction, "original.json"), null, null, Convert.ToHexString(SHA256.HashData(original.Before))));
        if (hasJar)
        {
            await using var input = File.OpenRead(Path.Combine(source, oldName + ".jar"));
            string hash = Convert.ToHexString(await SHA256.HashDataAsync(input, token).ConfigureAwait(false));
            steps.Add(new("file", Path.Combine(destination, oldName + ".jar"), Path.Combine(transaction, "client.jar"), null, null, hash));
            steps.Add(new("file", Path.Combine(transaction, "client.jar"), Path.Combine(destination, newName + ".jar"), null, null, hash));
        }
        foreach (var edit in edits)
        {
            if (edit.Before.Length > 4 * 1024 * 1024 || edit.After.Length > 4 * 1024 * 1024) throw new InvalidDataException("改名清单过大。");
            steps.Add(new("write", "", edit.TargetPath, MinecraftLibraryService.PathComparer.Equals(edit.OriginalPath, primary) ? null : edit.Before, edit.After, null));
        }
        var plan = new RenamePlan(1, root, source, destination, transaction, marker, steps.ToArray(), settings.PrepareRenameSettings(source, destination));
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(plan, RenameJson.Default.RenamePlan);
        if (bytes.Length > 192 * 1024 * 1024) throw new InvalidDataException("改名记录过大。");
        await WriteAsync(Path.Combine(transaction, "plan.json"), bytes, false, token).ConfigureAwait(false);
        return new(plan, Convert.ToHexString(SHA256.HashData(bytes)));
    }
    internal static async Task<InstanceRenameJournal> OpenAsync(string root, string transaction, CancellationToken token)
    {
        byte[] bytes = await ReadAsync(Path.Combine(transaction, "plan.json"), 192 * 1024 * 1024, token).ConfigureAwait(false);
        var plan = JsonSerializer.Deserialize(bytes, RenameJson.Default.RenamePlan) ?? throw new InvalidDataException("改名记录为空。");
        string? parent = Path.GetDirectoryName(transaction);
        bool standalone = parent == Path.Combine(root, ".nexa-rename") && Guid.TryParseExact(Path.GetFileName(transaction), "N", out _);
        bool nested = Path.GetFileName(transaction) == ".rename" && Path.GetDirectoryName(parent) == Path.Combine(root, ".nexa-modify")
            && Guid.TryParseExact(Path.GetFileName(parent), "N", out _);
        if ((!standalone && !nested) || plan.Schema != 1 || plan.Root != root || plan.Directory != transaction || plan.Steps.Length is < 4 or > 10010
            || Path.GetDirectoryName(plan.Source) != Path.Combine(root, "versions") || Path.GetDirectoryName(plan.Destination) != Path.Combine(root, "versions")
            || !MinecraftVersionPaths.IsSafeReference(Path.GetFileName(plan.Source)) || !MinecraftVersionPaths.IsSafeReference(Path.GetFileName(plan.Destination))
            || !plan.Marker.StartsWith(".nexa-rename-", StringComparison.Ordinal) || !Guid.TryParseExact(plan.Marker[13..], "N", out _)
            || plan.Settings.Source != plan.Source || plan.Settings.Destination != plan.Destination) throw new InvalidDataException("改名记录身份无效。");
        foreach (var step in plan.Steps)
        {
            foreach (string path in step.Source.Length == 0 ? new[] { step.Target } : [step.Source, step.Target])
            {
                string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                if (!Path.IsPathFullyQualified(path) || path != Path.GetFullPath(path)
                    || !(relative.StartsWith("versions/", StringComparison.Ordinal) || path.StartsWith(transaction + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
                    throw new InvalidDataException("改名路径越界。");
                RecoveryBlobStore.CheckLinks(path);
            }
            if (step.Kind is not ("write" or "file" or "directory")) throw new InvalidDataException("改名操作无效。");
            if (step.Kind == "file")
            {
                string oldName = Path.GetFileName(plan.Source), newName = Path.GetFileName(plan.Destination);
                bool allowed = step.Source == Path.Combine(plan.Destination, oldName + ".json") && step.Target == Path.Combine(transaction, "original.json")
                    || step.Source == Path.Combine(plan.Destination, oldName + ".jar") && step.Target == Path.Combine(transaction, "client.jar")
                    || step.Source == Path.Combine(transaction, "client.jar") && step.Target == Path.Combine(plan.Destination, newName + ".jar");
                if (!allowed || step.Hash is not { Length: 64 } || !step.Hash.All(char.IsAsciiHexDigit)) throw new InvalidDataException("改名文件移动无效。");
            }
            if (step.Kind == "write" && step.Target != Path.Combine(plan.Source, plan.Marker))
            {
                string relative = Path.GetRelativePath(Path.Combine(root, "versions"), step.Target).Replace('\\', '/');
                if (relative.Split('/') is not [var instance, var filename] || !MinecraftVersionPaths.IsSafeReference(instance)
                    || !filename.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || step.Before?.Length > 4 * 1024 * 1024
                    || step.After is not { Length: <= 4 * 1024 * 1024 }) throw new InvalidDataException("改名清单范围无效。");
            }
            if (step.Kind == "directory" && !((step.Source == plan.Source && step.Target == Path.Combine(transaction, "instance"))
                || (step.Source == Path.Combine(transaction, "instance") && step.Target == plan.Destination))) throw new InvalidDataException("改名目录移动无效。");
        }
        return new(plan, Convert.ToHexString(SHA256.HashData(bytes)));
    }
    internal async Task RemoveCommittedMarkerAsync(CancellationToken token)
    {
        try
        {
            if ((await ProgressAsync(token).ConfigureAwait(false)).Phase != "committed") return;
            string path = Path.Combine(_plan.Destination, _plan.Marker);
            RecoveryBlobStore.CheckLinks(path);
            if (File.Exists(path) && (await ReadAsync(path, 16, token).ConfigureAwait(false)).AsSpan().SequenceEqual("owned"u8)) File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
    internal async Task ApplyAsync(SettingsPolicyService settings, CancellationToken token, Action<int>? progressObserver = null)
    {
        var progress = await ProgressAsync(token).ConfigureAwait(false);
        if (progress.Phase == "committed") return;
        if (progress.Phase != "applying") throw new InvalidOperationException("改名已进入回滚。");
        for (int i = Math.Max(0, progress.Attempted - 1); i <= _plan.Steps.Length; i++)
        {
            await SaveProgressAsync("applying", i + 1, token).ConfigureAwait(false);
            if (i == _plan.Steps.Length) settings.ApplyRenameSettings(_plan.Settings, false);
            else await StepAsync(_plan.Steps[i], false, token).ConfigureAwait(false);
            progressObserver?.Invoke(i + 1);
        }
        await SaveProgressAsync("committed", _plan.Steps.Length + 1, CancellationToken.None).ConfigureAwait(false);
    }
    internal async Task RollbackAsync(SettingsPolicyService settings, CancellationToken token)
    {
        var progress = await ProgressAsync(token).ConfigureAwait(false);
        if (progress.Phase == "rolled-back") return;
        await SaveProgressAsync("rolling-back", progress.Attempted, token).ConfigureAwait(false);
        for (int i = progress.Attempted - 1; i >= 0; i--)
        {
            if (i == _plan.Steps.Length) settings.ApplyRenameSettings(_plan.Settings, true);
            else await StepAsync(_plan.Steps[i], true, token).ConfigureAwait(false);
            await SaveProgressAsync("rolling-back", i, token).ConfigureAwait(false);
        }
        await SaveProgressAsync("rolled-back", 0, CancellationToken.None).ConfigureAwait(false);
    }
    private async Task StepAsync(RenameStep step, bool reverse, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (step.Kind == "write")
        {
            byte[]? before = reverse ? step.After : step.Before, after = reverse ? step.Before : step.After;
            byte[]? current = File.Exists(step.Target) ? await ReadAsync(step.Target, 4 * 1024 * 1024, token).ConfigureAwait(false) : null;
            static bool Same(byte[]? a, byte[]? b) => a is null ? b is null : b is not null && a.AsSpan().SequenceEqual(b);
            if (Same(current, after)) return;
            if (!Same(current, before)) throw new IOException("改名文件已被再次修改：" + Path.GetFileName(step.Target));
            RecoveryBlobStore.CheckLinks(step.Target);
            if (after is null) File.Delete(step.Target);
            else await WriteAsync(step.Target, after, true, token).ConfigureAwait(false);
            return;
        }
        string from = reverse ? step.Target : step.Source, to = reverse ? step.Source : step.Target;
        RecoveryBlobStore.CheckLinks(from); RecoveryBlobStore.CheckLinks(to);
        bool directory = step.Kind == "directory";
        bool source = directory ? Directory.Exists(from) : File.Exists(from), target = directory ? Directory.Exists(to) : File.Exists(to);
        if (source == target) throw new IOException("改名移动目标冲突或丢失。");
        string currentPath = source ? from : to;
        if (directory)
        {
            byte[] marker = await ReadAsync(Path.Combine(currentPath, _plan.Marker), 16, token).ConfigureAwait(false);
            if (!marker.AsSpan().SequenceEqual("owned"u8)) throw new IOException("改名目录归属无效。");
        }
        else
        {
            await using var input = File.OpenRead(currentPath);
            if (Convert.ToHexString(await SHA256.HashDataAsync(input, token).ConfigureAwait(false)) != step.Hash) throw new IOException("改名文件身份已改变。");
        }
        if (source) { if (directory) Directory.Move(from, to); else File.Move(from, to); }
    }
    private async Task<RenameProgress> ProgressAsync(CancellationToken token)
    {
        string path = Path.Combine(_plan.Directory, "progress.json");
        if (!File.Exists(path)) return new(_hash, "applying", 0);
        var value = JsonSerializer.Deserialize(await ReadAsync(path, 4096, token).ConfigureAwait(false), RenameJson.Default.RenameProgress);
        if (value is null || value.Plan != _hash || value.Attempted < 0 || value.Attempted > _plan.Steps.Length + 1
            || value.Phase is not ("applying" or "rolling-back" or "rolled-back" or "committed")) throw new InvalidDataException("改名进度无效。");
        return value;
    }
    private Task SaveProgressAsync(string phase, int count, CancellationToken token) => WriteAsync(Path.Combine(_plan.Directory, "progress.json"),
        JsonSerializer.SerializeToUtf8Bytes(new RenameProgress(_hash, phase, count), RenameJson.Default.RenameProgress), true, token);
    private static async Task<byte[]> ReadAsync(string path, int limit, CancellationToken token)
    {
        RecoveryBlobStore.CheckLinks(path); await using var input = File.OpenRead(path);
        if (input.Length > limit) throw new InvalidDataException("改名记录过大。");
        byte[] bytes = new byte[(int)input.Length]; await input.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        if (input.ReadByte() != -1) throw new IOException("改名记录已改变。"); return bytes;
    }
    private static async Task WriteAsync(string path, byte[] bytes, bool overwrite, CancellationToken token)
    {
        RecoveryBlobStore.CheckLinks(path); string temporary = path + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { await output.WriteAsync(bytes, token).ConfigureAwait(false); await output.FlushAsync(token).ConfigureAwait(false); output.Flush(true); }
            token.ThrowIfCancellationRequested(); File.Move(temporary, path, overwrite);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
[JsonSerializable(typeof(RenamePlan))]
[JsonSerializable(typeof(RenameProgress))]
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
internal partial class RenameJson : JsonSerializerContext;

