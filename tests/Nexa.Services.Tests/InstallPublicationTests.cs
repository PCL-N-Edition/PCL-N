using System.Diagnostics;
using System.Text.Json.Nodes;
using Nexa.Services.Minecraft.Install;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async Task<int> RunInstallPublicationChild(string root, string stage)
    {
        var journal = await InstallPublicationJournal.OpenAsync(root, stage, default);
        await journal.ApplyAsync(default, (_, _) =>
        {
            Console.WriteLine("published-one"); Console.Out.Flush();
            using var wait = new ManualResetEventSlim(); wait.Wait();
        });
        return 0;
    }

    private static async ValueTask InstallPublicationSurvivesProcessTermination()
    {
        string root = CreateTempDirectory();
        try
        {
            foreach (bool rollback in new[] { false, true })
            {
                string stage = Path.Combine(root, ".nexa-modify", Guid.NewGuid().ToString("N"));
                string mod = Path.Combine(root, "mods", "a.jar"), manifest = Path.Combine(root, "versions", "test", "test.json");
                Directory.CreateDirectory(Path.GetDirectoryName(mod)!); Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
                File.WriteAllText(mod, "original-mod"); File.WriteAllText(manifest, "original-manifest");
                Directory.CreateDirectory(Path.Combine(stage, "mods")); Directory.CreateDirectory(Path.Combine(stage, "versions", "test"));
                File.WriteAllText(Path.Combine(stage, "mods", "a.jar"), "replacement-mod");
                File.WriteAllText(Path.Combine(stage, "versions", "test", "test.json"), "replacement-manifest");
                await InstallTaskJournal.CreateAsync(stage, new(root, "1.21.1", InstanceName: "test", EditFingerprint: new('A', 64))
                { InheritVanilla = false }, default);
                await InstallPublicationJournal.PrepareAsync(root, stage, "test", ["mods/a.jar", "versions/test/test.json"],
                    new Dictionary<string, string> { ["mods/a.jar"] = RecoveryTextBlob("original-mod").Sha256 }, default);
                var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                    start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Nexa.Services.Tests.dll"));
                start.ArgumentList.Add("--install-publication-child"); start.ArgumentList.Add(root); start.ArgumentList.Add(stage);
                using var child = Process.Start(start)!;
                try
                {
                    AssertEqual("published-one", await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)));
                    child.Kill(entireProcessTree: true); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
                }
                finally { if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); } }
                AssertEqual("replacement-mod", File.ReadAllText(mod)); AssertEqual("original-manifest", File.ReadAllText(manifest));
                var taskPlan = await InstallTaskJournal.ReadAsync(root, stage, default);
                AssertEqual("test", taskPlan.Command.InstanceName); AssertEqual("1.21.1", taskPlan.Command.GameVersion);
                var reopened = await InstallPublicationJournal.OpenAsync(root, stage, default);
                if (rollback)
                {
                    using var fixture = new InstallFixture(new FakeMetadata());
                    File.WriteAllText(mod, "user-change");
                    try { await fixture.Install.RollbackModificationAsync(root, preparedTaskId(stage)); throw new InvalidOperationException("Rollback overwrote external change."); } catch (IOException) { }
                    AssertEqual(InstallTaskStatus.RollbackRequested, await InstallTaskJournal.ReadStatusAsync(stage, taskPlan, default));
                    try { await fixture.Install.ResumeModificationAsync(root, preparedTaskId(stage)); throw new InvalidOperationException("Rollback intent ignored."); } catch (InvalidOperationException error) when (error.Message != "Rollback intent ignored.") { }
                    AssertEqual("user-change", File.ReadAllText(mod));
                    File.WriteAllText(mod, "replacement-mod");
                    await fixture.Install.RollbackModificationAsync(root, preparedTaskId(stage));
                    await fixture.Install.RollbackModificationAsync(root, preparedTaskId(stage));
                    AssertEqual(InstallTaskStatus.RolledBack, await InstallTaskJournal.ReadStatusAsync(stage, taskPlan, default));
                    AssertEqual("original-mod", File.ReadAllText(mod)); AssertEqual("original-manifest", File.ReadAllText(manifest));
                }
                else
                {
                    using var fixture = new InstallFixture(new FakeMetadata());
                    await fixture.Install.ResumeModificationAsync(root, preparedTaskId(stage));
                    AssertEqual(InstallTaskStatus.Completed, await InstallTaskJournal.ReadStatusAsync(stage, taskPlan, default));
                    await fixture.Install.ResumeModificationAsync(root, preparedTaskId(stage));
                    AssertEqual("replacement-mod", File.ReadAllText(mod)); AssertEqual("replacement-manifest", File.ReadAllText(manifest));
                    File.WriteAllText(mod, "changed-after-commit");
                    try { await reopened.ApplyAsync(default); throw new InvalidOperationException("Committed external change ignored."); } catch (IOException) { }
                    AssertEqual("changed-after-commit", File.ReadAllText(mod));
                }
            }
        }
        finally { Directory.Delete(root, true); }

        static Guid preparedTaskId(string stage) => Guid.ParseExact(Path.GetFileName(stage), "N");
    }

    private static async ValueTask InstallPublicationPreservesChangedFilesAndRejectsTampering()
    {
        string root = CreateTempDirectory();
        try
        {
            string stage = Path.Combine(root, ".nexa-modify", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path.Combine(stage, "mods"));
            File.WriteAllText(Path.Combine(stage, "mods", "a.jar"), "new");
            var journal = await InstallPublicationJournal.PrepareAsync(root, stage, "test", ["mods/a.jar"], new Dictionary<string, string>(), default);
            Directory.CreateDirectory(Path.Combine(root, "mods")); string target = Path.Combine(root, "mods", "a.jar"); File.WriteAllText(target, "external");
            try { await journal.ApplyAsync(default); throw new InvalidOperationException("External edit overwritten."); } catch (IOException) { }
            AssertEqual("external", File.ReadAllText(target));
            string secondStage = Path.Combine(root, ".nexa-modify", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(secondStage, "mods")); File.WriteAllText(Path.Combine(secondStage, "mods", "a.jar"), "external");
            var unchanged = await InstallPublicationJournal.PrepareAsync(root, secondStage, "test", ["mods/a.jar"],
                new Dictionary<string, string> { ["mods/a.jar"] = RecoveryTextBlob("external").Sha256 }, default);
            await unchanged.ApplyAsync(default);
            AssertEqual("external", File.ReadAllText(target));
            string planPath = Path.Combine(stage, ".publication", "plan.json");
            var plan = JsonNode.Parse(File.ReadAllText(planPath))!; plan["files"]![0]!["path"] = "saves/world.dat"; File.WriteAllText(planPath, plan.ToJsonString());
            try { await InstallPublicationJournal.OpenAsync(root, stage, default); throw new InvalidOperationException("Invalid target accepted."); } catch (InvalidDataException) { }
        }
        finally { Directory.Delete(root, true); }
    }
}
