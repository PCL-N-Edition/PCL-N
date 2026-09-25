using Nexa.Services.Minecraft.Management;
using Nexa.Services.Tasks;

namespace Nexa.Services.Minecraft.Install;

public sealed partial class MinecraftInstallService
{
    private static bool ValidatePrimaryLoader(MinecraftInstallCommand command)
    {
        bool processor = command.Loader is InstallLoader.Forge or InstallLoader.NeoForge or InstallLoader.Cleanroom or InstallLoader.OptiFine;
        if (command.Loader is not null && !IsProfileJsonLoader(command.Loader.Value) && !processor)
            throw new InvalidOperationException($"{LoaderDisplayName(command.Loader.Value)} 不能作为主加载器安装，请选择对应的基础加载器。");
        if (command.Loader is not null && string.IsNullOrWhiteSpace(command.LoaderBuild)) throw new InvalidOperationException("请选择加载器版本。");
        return processor;
    }
    private static string InstallInstanceId(MinecraftInstallCommand command) => !string.IsNullOrWhiteSpace(command.InstanceName)
        ? SafeName(command.InstanceName)
        : command.Loader is { } loader && command.LoaderBuild is { Length: > 0 } build
            ? SafeName($"{command.GameVersion}-{loader.ToString().ToLowerInvariant()}{build}") : SafeName(command.GameVersion);

    private async Task<MinecraftInstallResult> InstallNewAsync(MinecraftInstallCommand command, ITaskCenterTask task,
        CancellationToken token, string? resumeStage = null)
    {
        _ = ValidatePrimaryLoader(command);
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(command.RootDirectory));
        string instance = InstallInstanceId(command);
        if (command.Loader is not null && instance == SafeName(command.GameVersion))
            throw new InvalidOperationException("加载器实例名称不能与原版版本目录相同。");
        string manifest = $"versions/{instance}/{instance}.json";
        string destination = ForgeInstallService.Contained(root, manifest);
        RecoveryBlobStore.CheckLinks(destination);
        if (File.Exists(destination)) throw new IOException("已有同名版本，请使用修改版本功能。");
        command = command with { RootDirectory = root, InstanceName = instance };
        string stage = resumeStage ?? ForgeInstallService.Contained(root, ".nexa-install-jobs/" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        bool safeToRemove = false;
        FileStream? lease = null;
        try
        {
            if (resumeStage is null)
            {
                string directory = Path.Combine(stage, InstallTaskJournal.DirectoryName); Directory.CreateDirectory(directory);
                string lockPath = Path.Combine(directory, "execution.lock"); RecoveryBlobStore.CheckLinks(lockPath);
                lease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                await InstallTaskJournal.CreateAsync(stage, command, token).ConfigureAwait(false);
            }
            await RunAsync(command with { RootDirectory = stage, ReuseRoot = root }, task, token,
                new PersistentInstallMetadataSource(stage, _metadata), deferCompletion: true).ConfigureAwait(false);
            RecoveryBlobStore.CheckLinks(destination);
            if (File.Exists(destination)) throw new IOException("安装期间出现同名版本，已停止发布。");
            string[] files = Directory.GetFiles(stage, "*", SearchOption.AllDirectories)
                .Select(file => Path.GetRelativePath(stage, file).Replace('\\', '/'))
                .Where(relative => !relative.StartsWith(InstallTaskJournal.DirectoryName + "/", StringComparison.Ordinal))
                .Where(relative => relative == manifest || !(relative.StartsWith("versions/", StringComparison.Ordinal)
                    && relative.EndsWith(".json", StringComparison.Ordinal) && File.Exists(ForgeInstallService.Contained(root, relative))))
                .ToArray();
            var journal = await InstallPublicationJournal.PrepareAsync(root, stage, instance, files, new Dictionary<string, string>(), token).ConfigureAwait(false);
            try { await journal.ApplyAsync(token).ConfigureAwait(false); }
            catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
            {
                if (resumeStage is not null) throw;
                try { await journal.RollbackAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (Exception rollbackError) when (rollbackError is not OutOfMemoryException and not AccessViolationException)
                { throw new AggregateException("安装发布及撤回未完成，已保留事务记录。", error, rollbackError); }
                safeToRemove = true;
                throw;
            }
            safeToRemove = true;
            if (resumeStage is null)
            {
                task.Complete("已安装 " + instance);
                Installed?.Invoke(root);
            }
            return new(instance, Path.Combine(root, "versions", instance));
        }
        finally
        {
            lease?.Dispose();
            if (resumeStage is null && (safeToRemove || !File.Exists(Path.Combine(stage, ".publication", "progress.json"))))
            {
                RecoveryBlobStore.CheckLinks(stage);
                try { Directory.Delete(stage, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }
}
