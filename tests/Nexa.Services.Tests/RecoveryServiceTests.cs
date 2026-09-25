using Nexa.Services.Minecraft.Launch;
using Nexa.Services.Minecraft.Management;
using Nexa.Services.Minecraft.ModLoaders;
using Nexa.Services.Minecraft.Process;
using Nexa.Services.Settings;
using Nexa.Xsr.State;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async ValueTask JvmHostSuccessfulExitAutomaticallyCreatesRecoveryBaseline()
    {
        string root = CreateTempDirectory();
        try
        {
            string instance = Path.Combine(root, "versions", "test"); Directory.CreateDirectory(instance);
            File.WriteAllText(Path.Combine(instance, "test.json"), """{"id":"test","_minecraftVersion":"1.20.1"}""");
            string jar = Path.Combine(instance, "test.jar"); File.WriteAllText(jar, "client");
            var (_, settings) = PolicyFixture();
            var builder = new XsrStateStoreBuilder(); MinecraftProcessStateComposition.DeclareState(builder);
            var state = builder.Build();
            var processes = new MinecraftProcessService(new RecoveryExitPort(), hostStore: state);
            var host = new JvmHostService(processes, recovery: new InstanceRecoveryService(settings, state));
            MinecraftLaunchPlan plan = new("java", instance, ["example.Main"], [], [], new MinecraftModLoaderDescriptor(MinecraftModLoaderKind.Vanilla, null, "example.Main", []))
            { MainClassIndex = 0, MinecraftRootDirectory = root, InstanceDirectory = instance, GameDirectory = instance, ClientJarPath = jar };
            await using var session = await host.StartAsync(plan, "test");
            session.ConfirmGameWindow();
            session.Process.StandardInput.Close();
            await session.WaitForExitAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            var store = new RecoverySnapshotStore(instance, instance);
            RecoverySnapshot? snapshot = null;
            long start = Environment.TickCount64;
            while (snapshot is null && Environment.TickCount64 - start < 10000)
            {
                snapshot = await store.ReadAsync();
                if (snapshot is null) await Task.Delay(25);
            }
            AssertTrue(snapshot is not null);
            AssertEqual(2, snapshot!.Files.Count);
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class RecoveryExitPort : IMinecraftProcessPort
    {
        public ValueTask<System.Diagnostics.Process> StartAsync(System.Diagnostics.ProcessStartInfo ignored, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = OperatingSystem.IsWindows()
                ? new System.Diagnostics.ProcessStartInfo("cmd", "/c set /p value= & exit /b 0")
                : new System.Diagnostics.ProcessStartInfo("/bin/sh", "-c \"read value; exit 0\"");
            info.UseShellExecute = false; info.CreateNoWindow = true;
            info.RedirectStandardInput = true;
            return ValueTask.FromResult(System.Diagnostics.Process.Start(info)!);
        }
    }

    private static async ValueTask RecoveryServiceCapturesOnlyConfirmedCompleteSuccessfulExits()
    {
        string root = CreateTempDirectory();
        try
        {
            string instance = Path.Combine(root, "versions", "test");
            Directory.CreateDirectory(instance);
            File.WriteAllText(Path.Combine(instance, "test.json"), """{"id":"test","_minecraftVersion":"1.20.1"}""");
            string jar = Path.Combine(instance, "test.jar"); File.WriteAllText(jar, "client");
            File.WriteAllText(Path.Combine(instance, "options.txt"), "fov:1");
            var (_, settings) = PolicyFixture();
            var builder = new XsrStateStoreBuilder(); MinecraftProcessStateComposition.DeclareState(builder);
            var state = builder.Build();
            var service = new InstanceRecoveryService(settings, state);
            MinecraftLaunchPlan plan = new("java", instance, [], [], [], new MinecraftModLoaderDescriptor(MinecraftModLoaderKind.Vanilla, null, "example.Main", []))
            { MinecraftRootDirectory = root, InstanceDirectory = instance, GameDirectory = instance, ClientJarPath = jar };
            var session = new MinecraftProcessSnapshot(Guid.NewGuid(), "test", 1, MinecraftProcessState.Exited, 0, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow)
            { InstanceDirectory = instance, GameDirectory = instance, GameWindowConfirmed = true };
            var store = new RecoverySnapshotStore(instance, instance);
            AssertFalse(await service.RecordSuccessfulExitAsync(plan, session with { GameWindowConfirmed = false }, false));
            AssertTrue(await store.ReadAsync() is null);
            AssertTrue(await service.RecordSuccessfulExitAsync(plan, session, false));
            var original = (await store.ReadAsync())!;
            AssertEqual(3, original.Files.Count);
            AssertTrue(original.SettingsDocument.Contains("game.jvm", StringComparison.Ordinal));
            File.WriteAllText(Path.Combine(instance, "options.txt"), "fov:2");
            AssertFalse(await service.RecordSuccessfulExitAsync(plan, session, true));
            AssertFalse(await service.RecordSuccessfulExitAsync(plan, session with { State = MinecraftProcessState.Failed, ExitCode = 1 }, false));
            AssertFalse(await service.RecordSuccessfulExitAsync(plan with { ClientJarPath = Path.Combine(root, "missing.jar") }, session, false));
            AssertEqual(original.Revision, (await store.ReadAsync())!.Revision);
            using (var operation = await InstanceRecoveryOperationGate.EnterOperationAsync(root))
                AssertFalse(await service.RecordSuccessfulExitAsync(plan, session, false));
            var id = state.Resolve(MinecraftProcessStateComposition.SessionsKey);
            var running = session with { SessionId = Guid.NewGuid(), State = MinecraftProcessState.Running, EndedAt = null };
            state.PublishDelta(id, new XsrCollectionDelta<MinecraftProcessSnapshot, Guid>(0, [running], []));
            AssertFalse(await service.RecordSuccessfulExitAsync(plan, session, false));
            AssertEqual(original.Revision, (await store.ReadAsync())!.Revision);
            state.PublishDelta(id, new XsrCollectionDelta<MinecraftProcessSnapshot, Guid>(state.ReadCollection<MinecraftProcessSnapshot>(id).Revision, [], [running.SessionId]));
            AssertTrue(await service.RecordSuccessfulExitAsync(plan, session, false));
            AssertFalse(original.Revision == (await store.ReadAsync())!.Revision);
            AssertEqual(3, Directory.GetFiles(Path.Combine(instance, "Nexa", "Recovery", "objects"), "*.br").Length);
            string pending = Path.Combine(instance, "Nexa", "Recovery", "transactions");
            Directory.CreateDirectory(pending);
            File.WriteAllText(Path.Combine(instance, "options.txt"), "fov:3");
            AssertTrue(await service.RecordSuccessfulExitAsync(plan, session, false));
            AssertEqual(4, Directory.GetFiles(Path.Combine(instance, "Nexa", "Recovery", "objects"), "*.br").Length);
            Directory.Delete(pending);
            AssertTrue(await service.RecordSuccessfulExitAsync(plan, session, false));
            AssertEqual(3, Directory.GetFiles(Path.Combine(instance, "Nexa", "Recovery", "objects"), "*.br").Length);
            AssertTrue(settings.Set(new("recovery.keep-history", SettingsLayer.Instance, new(SettingsOverrideMode.Custom, "true"), instance)).IsSuccess);
            AssertTrue(await service.RecordSuccessfulExitAsync(plan, session, false));
            AssertEqual(2, (await store.ListAsync()).Count);
            AssertFalse((await store.ReadAsync())!.SettingsDocument.Contains("recovery.keep-history", StringComparison.Ordinal));
            AssertTrue(settings.Set(new("recovery.keep-history", SettingsLayer.Instance, new(SettingsOverrideMode.Custom, "false"), instance)).IsSuccess);
            AssertTrue(await service.RecordSuccessfulExitAsync(plan, session, false));
            AssertEqual(1, (await store.ListAsync()).Count);
        }
        finally { Directory.Delete(root, true); }
    }
}
