using Nexa.Services.Downloads;
using Nexa.Services.Minecraft.Install;
using Nexa.Services.Tasks;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private sealed class ExitBlockingConnection(TaskCompletionSource entered) : IDownloadConnection
    {
        public ValueTask<DownloadConnectionInfo> StartAsync(long offset, CancellationToken token = default) => ValueTask.FromResult(new DownloadConnectionInfo(10, 0, 9, false));
        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return 0;
        }
        public ValueTask StopAsync(CancellationToken token = default) => ValueTask.CompletedTask;
    }

    private static async ValueTask InstallExitPauseRetainsPlanAndCancelRemovesStage()
    {
        foreach (bool editing in new[] { false, true })
            foreach (bool pause in new[] { true, false })
            {
                string root = CreateTempDirectory();
                try
                {
                    MinecraftInstallCommand command = new(root, "1.20.1", InstanceName: "exit-test");
                    string? originalManifest = null;
                    if (editing)
                    {
                        using var initial = new InstallFixture(new() { VanillaJson = VanillaJson(), AssetIndexJson = AssetIndexJson() });
                        AssertTrue((await initial.Install.InstallAsync(command)).IsSuccess);
                        var original = await MinecraftInstallEditService.ReadAsync(new(root, "exit-test"));
                        originalManifest = File.ReadAllText(Path.Combine(root, "versions", "exit-test", "exit-test.json"));
                        command = command with { Loader = InstallLoader.Fabric, LoaderBuild = "0.16.9", EditFingerprint = original.Fingerprint };
                    }
                    TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    using var fixture = new InstallFixture(new() { VanillaJson = VanillaJson(), AssetIndexJson = AssetIndexJson(), LoaderJson = new() { ["mainClass"] = "fabric.Main", ["inheritsFrom"] = "1.20.1" } }, connectionFactory: _ => new ExitBlockingConnection(entered));
                    var running = fixture.Install.InstallAsync(command);
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    AssertTrue((await fixture.Install.StopAsync(new(pause)).WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess);
                    AssertFalse((await running).IsSuccess);
                    AssertEqual(pause ? TaskCenterEntryState.Paused : TaskCenterEntryState.Canceled, fixture.Entry().State);
                    AssertFalse((await fixture.Install.InstallAsync(new(root, "1.20.1", InstanceName: "late"))).IsSuccess);
                    string[] stages = Directory.GetDirectories(Path.Combine(root, editing ? ".nexa-modify" : ".nexa-install-jobs"));
                    AssertEqual(pause ? 1 : 0, stages.Length);
                    if (editing) AssertEqual(originalManifest, File.ReadAllText(Path.Combine(root, "versions", "exit-test", "exit-test.json")));
                    if (pause)
                    {
                        using var next = new InstallFixture(new());
                        AssertTrue((await next.Install.RecoverPendingAsync(new([root]))).IsSuccess);
                        AssertTrue(File.Exists(Path.Combine(root, "versions", "exit-test", "exit-test.json")));
                    }
                }
                finally { Directory.Delete(root, true); }
            }
    }
}
