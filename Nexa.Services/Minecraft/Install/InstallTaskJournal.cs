using System.Text.Json;
using System.Text.Json.Serialization;
using Nexa.Services.Minecraft.Management;

namespace Nexa.Services.Minecraft.Install;

internal sealed record InstallTaskPlan(int Schema, Guid Id, DateTimeOffset CreatedAt, MinecraftInstallCommand Command);

/// <summary>Immutable intent, written before preparing artifacts. No UI selection is needed to reopen it.</summary>
internal static class InstallTaskJournal
{
    internal const string DirectoryName = ".task";
    private const int MaxBytes = 1024 * 1024;

    internal static async Task<InstallTaskPlan> CreateAsync(string stage, MinecraftInstallCommand command, CancellationToken token)
    {
        var plan = new InstallTaskPlan(1, Guid.ParseExact(Path.GetFileName(stage), "N"), DateTimeOffset.UtcNow, command);
        Validate(stage, command.RootDirectory, plan);
        string folder = Path.Combine(stage, DirectoryName);
        RecoveryBlobStore.CheckLinks(folder);
        Directory.CreateDirectory(folder);
        string target = Path.Combine(folder, "plan.json"), temporary = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".part");
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(plan, InstallTaskJsonContext.Default.InstallTaskPlan);
        if (bytes.Length > MaxBytes) throw new InvalidDataException("安装任务记录过大。");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            { await output.WriteAsync(bytes, token).ConfigureAwait(false); await output.FlushAsync(token).ConfigureAwait(false); output.Flush(true); }
            token.ThrowIfCancellationRequested(); RecoveryBlobStore.CheckLinks(target);
            File.Move(temporary, target, overwrite: false);
        }
        finally { RecoveryBlobStore.CheckLinks(temporary); if (File.Exists(temporary)) File.Delete(temporary); }
        // Return the serialized copy, so mutable lists passed by a caller cannot alter the persisted intent.
        return JsonSerializer.Deserialize(bytes, InstallTaskJsonContext.Default.InstallTaskPlan)!;
    }

    internal static async Task<InstallTaskPlan> ReadAsync(string root, string stage, CancellationToken token)
    {
        ValidateStage(root, stage);
        string path = Path.Combine(stage, DirectoryName, "plan.json"); RecoveryBlobStore.CheckLinks(path);
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        if (input.Length > MaxBytes) throw new InvalidDataException("安装任务记录过大。");
        byte[] bytes = new byte[(int)input.Length]; await input.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        if (input.ReadByte() != -1) throw new InvalidDataException("安装任务记录在读取期间发生变化。");
        var plan = JsonSerializer.Deserialize(bytes, InstallTaskJsonContext.Default.InstallTaskPlan) ?? throw new InvalidDataException("安装任务记录为空。");
        Validate(stage, root, plan);
        return plan;
    }

    private static void ValidateStage(string root, string stage)
    {
        if (!Path.IsPathFullyQualified(root) || root != Path.GetFullPath(root)
            || !Path.IsPathFullyQualified(stage) || stage != Path.GetFullPath(stage)
            || !MinecraftLibraryService.PathComparer.Equals(Directory.GetParent(stage)?.FullName, Path.Combine(root, ".nexa-modify"))
            || !Guid.TryParseExact(Path.GetFileName(stage), "N", out _)) throw new InvalidDataException("安装任务目录无效。");
        RecoveryBlobStore.CheckLinks(root); RecoveryBlobStore.CheckLinks(stage);
    }

    private static void Validate(string stage, string root, InstallTaskPlan plan)
    {
        ValidateStage(root, stage);
        var command = plan.Command ?? throw new InvalidDataException("安装任务缺少计划。");
        if (plan.Schema != 1 || plan.Id == Guid.Empty || plan.Id.ToString("N") != Path.GetFileName(stage)
            || !MinecraftLibraryService.PathComparer.Equals(command.RootDirectory, root)
            || !MinecraftVersionPaths.IsSafeReference(command.GameVersion)
            || !MinecraftVersionPaths.IsSafeReference(command.InstanceName)
            || command.NewInstanceName is { } name && !MinecraftVersionPaths.IsSafeReference(name)
            || command.EditFingerprint is not { Length: 64 } fingerprint || !fingerprint.All(char.IsAsciiHexDigit)
            || command.InheritVanilla is null || command.PreparingEdit || command.ReuseRoot is not null || command.ModsRelativeDirectory is not null
            || command.Loader is { } loader && !Enum.IsDefined(loader)
            || command.Loader is not null && !ValidText(command.LoaderBuild, 256)
            || command.Loader is null && command.LoaderBuild is not null)
            throw new InvalidDataException("安装任务身份或版本选择无效。");
        var addons = command.Addons ?? [];
        if (addons.Count > 12 || addons.Select(item => item.Kind).Distinct().Count() != addons.Count)
            throw new InvalidDataException("安装附加组件重复或过多。");
        foreach (var addon in addons)
        {
            if (!Enum.IsDefined(addon.Kind) || !ValidText(addon.Version, 256) || addon.Downloads?.Count > 16)
                throw new InvalidDataException("安装附加组件无效。");
            foreach (var download in addon.Downloads ?? [])
                if (!ValidText(download.Source, 64) || !MinecraftVersionPaths.IsSafeReference(download.FileName)
                    || download.Url is not { IsAbsoluteUri: true } url || url.Scheme != Uri.UriSchemeHttps || url.UserInfo.Length != 0
                    || url.AbsoluteUri.Length > 8192 || download.Size < 0 || download.Size > RecoveryBlobStore.MaxFileBytes
                    || download.Sha1 is { } sha && (sha.Length != 40 || !sha.All(char.IsAsciiHexDigit)))
                    throw new InvalidDataException("安装下载记录无效。");
        }
        if (command.LocalInstaller is { } local && (!Path.IsPathFullyQualified(local.Path)
            || local.Sha256 is not { Length: 64 } hash || !hash.All(char.IsAsciiHexDigit)
            || local.Loader != command.Loader || local.Game != command.GameVersion || local.Build != command.LoaderBuild))
            throw new InvalidDataException("本地安装器身份无效。");
    }

    private static bool ValidText(string? text, int maximum) => !string.IsNullOrWhiteSpace(text) && text.Length <= maximum && !text.Any(char.IsControl);
}

[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(InstallTaskPlan))]
internal sealed partial class InstallTaskJsonContext : JsonSerializerContext;
