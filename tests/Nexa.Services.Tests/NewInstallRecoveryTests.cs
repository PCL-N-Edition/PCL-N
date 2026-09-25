using System.Diagnostics;
using Nexa.Services.Minecraft.Install;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async Task<int> RunInterruptedNewInstallChild(string root)
    {
        string firstSource = "";
        using var fixture = new InstallFixture(new() { VanillaJson = VanillaJson(), AssetIndexJson = AssetIndexJson() }, connectionFactory: source =>
        {
            if (firstSource.Length != 0)
            {
                Console.WriteLine(firstSource); Console.Out.Flush();
                using var wait = new ManualResetEventSlim(); wait.Wait();
            }
            if (source.Contains("/client/", StringComparison.Ordinal)) firstSource = source;
            return new ServingConnection(PayloadFor(source));
        });
        await fixture.Install.InstallAsync(new(root, "1.20.1", InstanceName: "resumable"));
        return 0;
    }

    private static async ValueTask NewInstallationRecoversAfterKilledDownloaderWithoutRepeatingVerifiedFile()
    {
        string root = CreateTempDirectory();
        try
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Nexa.Services.Tests.dll"));
            start.ArgumentList.Add("--interrupted-new-install"); start.ArgumentList.Add(root);
            using var child = Process.Start(start)!;
            string? completedSource;
            try
            {
                completedSource = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
                AssertTrue(completedSource?.StartsWith("https://", StringComparison.Ordinal) == true);
                child.Kill(entireProcessTree: true); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            }
            finally { if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); } }
            string manifest = Path.Combine(root, "versions", "resumable", "resumable.json");
            AssertFalse(File.Exists(manifest));
            string stage = Directory.GetDirectories(Path.Combine(root, ".nexa-install-jobs")).Single();
            string injected = Path.Combine(stage, "libraries", "attacker.jar");
            Directory.CreateDirectory(Path.GetDirectoryName(injected)!);
            File.WriteAllText(injected, "untrusted leftover");
            var plan = await InstallTaskJournal.ReadAsync(root, stage, default);
            AssertTrue(plan.Command.EditFingerprint is null);
            List<string> downloaded = []; var metadata = new FakeMetadata();
            using var recovery = new InstallFixture(metadata, connectionFactory: source => { downloaded.Add(source); return new ServingConnection(PayloadFor(source)); });
            AssertTrue((await recovery.Install.RecoverPendingAsync(new([root]))).IsSuccess);
            AssertTrue(File.Exists(manifest)); AssertEqual(0, metadata.VanillaReads);
            AssertFalse(File.Exists(Path.Combine(root, "libraries", "attacker.jar")));
            if (downloaded.Contains(completedSource!)) throw new InvalidOperationException("Repeated: " + completedSource + " transfers: " + string.Join(",", downloaded)); AssertTrue(downloaded.Count > 0);
            AssertEqual(InstallTaskStatus.Completed, await InstallTaskJournal.ReadStatusAsync(stage, plan, default));
            int transfers = downloaded.Count;
            AssertTrue((await recovery.Install.RecoverPendingAsync(new([root]))).IsSuccess);
            AssertEqual(transfers, downloaded.Count);
            string original = File.ReadAllText(manifest);
            AssertFalse((await recovery.Install.InstallAsync(new(root, "1.20.1", InstanceName: "resumable"))).IsSuccess);
            AssertEqual(original, File.ReadAllText(manifest));
        }
        finally { Directory.Delete(root, true); }
    }
}
