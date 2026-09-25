using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Nexa.Services.Downloads;
using Nexa.Services.Minecraft.Downloads;
using Nexa.Services.Minecraft.Launch;
using Nexa.Services.Tasks;

namespace Nexa.Services.Minecraft.Install;

public sealed partial class MinecraftInstallService
{
    private async Task PrepareComponentEditAsync(MinecraftInstallCommand command, MinecraftInstallEditSnapshot original,
        MinecraftInstallEditPlan plan, string stage, ITaskCenterTask task, CancellationToken token)
    {
        string relative = $"versions/{original.InstanceId}/{original.InstanceId}.json";
        var profile = await MinecraftVersionJsonReader.ReadAsync(ForgeInstallService.Contained(original.RootDirectory, relative), token).ConfigureAwait(false);
        var managed = original.ManagedMods.Where(file => file.Loader is null || !plan.ChangedLoaders.Contains(file.Loader.Value)).ToList();
        foreach (var addon in command.Addons ?? [])
        {
            if (!plan.ChangedLoaders.Contains(addon.Kind)) continue;
            var downloads = await ResolveAddonDownloadsAsync(command.GameVersion, addon, token,
                new PersistentInstallMetadataSource(stage, _metadata)).ConfigureAwait(false);
            var first = downloads[0];
            string path = ForgeInstallService.Contained(stage, original.ModsRelativeDirectory + "/" + SafeName(first.FileName));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            task.Report(StagePlan[3], "正在更新 " + LoaderDisplayName(addon.Kind), 0.5, 0, downloads.Count, 0);
            var result = await _downloads.DownloadAsync(new DownloadRequest
            {
                Sources = downloads.Where(file => string.IsNullOrWhiteSpace(first.Sha1) || file.Sha1 == first.Sha1).Select(file => file.Url.ToString()).ToArray(),
                DestinationPath = path,
                ConnectionFactory = url => _connectionFactory?.Invoke(url) ?? new HttpConnection(_http, url),
            }, cancellationToken: token).ConfigureAwait(false);
            if (!result.Success || !await MinecraftFileVerifier.VerifyAsync(new(path, first.Size > 0 ? first.Size : null, first.Sha1), token).ConfigureAwait(false))
                throw new IOException("附属组件下载或校验失败：" + first.FileName);
            await using var stream = File.OpenRead(path);
            string hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
            managed.Add(new(Path.GetRelativePath(stage, path).Replace('\\', '/'), hash, addon.Kind));
        }
        JsonObject receipt = new() { ["game"] = original.GameVersion, ["loader"] = command.Loader?.ToString(), ["build"] = command.LoaderBuild };
        receipt["addons"] = new JsonArray((command.Addons ?? []).Select(addon => (JsonNode?)new JsonObject { ["loader"] = addon.Kind.ToString(), ["build"] = addon.Version }).ToArray());
        receipt["managedMods"] = new JsonArray(managed.Select(file => (JsonNode?)new JsonObject { ["path"] = file.Path, ["sha256"] = file.Sha256, ["loader"] = file.Loader?.ToString() }).ToArray());
        profile["_nexaInstall"] = receipt;
        string destination = ForgeInstallService.Contained(stage, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await File.WriteAllTextAsync(destination, profile.ToJsonString(JsonOptions), token).ConfigureAwait(false);
    }
}
