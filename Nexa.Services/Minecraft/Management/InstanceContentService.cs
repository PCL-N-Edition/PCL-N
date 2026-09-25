using Nexa.Services.Minecraft.Process;
using Nexa.Xsr;
using Nexa.Xsr.State;

namespace Nexa.Services.Minecraft.Management;

public sealed record InstanceModEnabledCommand(string InstanceDirectory, string Name, bool Enabled, long ExpectedSize, long ExpectedModifiedUtcTicks);

public static class InstanceContentService
{
    internal static bool? ModEnabled(string name)
    {
        bool disabled = name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
        string active = disabled ? name[..^9] : name;
        return active.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) || active.EndsWith(".litemod", StringComparison.OrdinalIgnoreCase)
            ? !disabled : null;
    }

    public static async Task<XsrResult> SetModEnabledAsync(InstanceModEnabledCommand command, XsrStateStore store, CancellationToken token = default)
    {
        try
        {
            return await Task.Run(async () =>
            {
                if (!MinecraftVersionPaths.IsSafeReference(command.Name) || ModEnabled(command.Name) is not { } enabled)
                    throw new InvalidDataException("请选择有效的模组文件。");
                var snapshot = await InstanceManagementService.ReadAsync(new(command.InstanceDirectory), token).ConfigureAwait(false);
                using var recoveryOperation = await InstanceRecoveryOperationGate.EnterOperationAsync(
                    Directory.GetParent(snapshot.InstanceDirectory)!.Parent!.FullName, token).ConfigureAwait(false);
                var page = snapshot.Pages.FirstOrDefault(page => page.Id == "mods")
                    ?? throw new InvalidDataException("此版本没有可管理的模组加载器。");
                string source = Path.Combine(page.Directory!, command.Name);
                for (string? current = source; current is not null; current = Path.GetDirectoryName(current))
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("不能修改链接文件或链接目录中的模组。");
                var file = new FileInfo(source);
                if (!file.Exists || file.Length != command.ExpectedSize || file.LastWriteTimeUtc.Ticks != command.ExpectedModifiedUtcTicks)
                    throw new IOException("模组文件已变化，请刷新后重试。");
                if (store.TryResolve(MinecraftProcessStateComposition.SessionsKey, out var sessions)
                    && store.ReadCollection<MinecraftProcessSnapshot>(sessions, cancellationToken: token).Items.Any(item =>
                        item.State is MinecraftProcessState.Created or MinecraftProcessState.Running
                        && (MinecraftLibraryService.PathComparer.Equals(item.InstanceDirectory, snapshot.InstanceDirectory)
                            || MinecraftLibraryService.PathComparer.Equals(item.GameDirectory, snapshot.GameDirectory))))
                    throw new InvalidOperationException("有游戏正在使用此模组目录，请先结束游戏进程。");
                token.ThrowIfCancellationRequested();
                if (enabled == command.Enabled) return XsrResult.Success();
                string destination = Path.Combine(page.Directory!, command.Enabled ? command.Name[..^9] : command.Name + ".disabled");
                if (Path.Exists(destination)) throw new IOException("目标文件已存在，未覆盖任何模组。");
                File.Move(source, destination, overwrite: false);
                return XsrResult.Success();
            }, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { return XsrResult.Failure(XsrRuntimeErrors.Cancelled()); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        { return XsrResult.Failure(MinecraftErrors.InvalidRequest(error.Message)); }
    }
}
