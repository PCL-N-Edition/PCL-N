using System.Text.Json;
using Nexa.Services.Minecraft.Install;
using Nexa.Services.Minecraft.Management;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async ValueTask RecoveryRejectsImportedAndTransplantedAuthority()
    {
        string root = CreateTempDirectory();
        try
        {
            string authority = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Nexa", "InstallRecoveryAuthority", "v1");
            bool nestedAuthorityRejected = false;
            try { RecoveryRecordAuthority.VerifyAbsent(Path.Combine(authority, ".nexa-modify", Guid.NewGuid().ToString("N"), ".task", "plan.json")); }
            catch (InvalidDataException) { nestedAuthorityRejected = true; }
            AssertTrue(nestedAuthorityRejected);
            foreach (bool modifying in new[] { false, true })
            {
                var id = Guid.NewGuid();
                string stage = Path.Combine(root, modifying ? ".nexa-modify" : ".nexa-install-jobs", id.ToString("N"));
                Directory.CreateDirectory(Path.Combine(stage, ".task"));
                var command = new MinecraftInstallCommand(root, "1.20.1", InstallLoader.Forge, "47.4.20",
                    InstanceName: "foreign", EditFingerprint: modifying ? new('A', 64) : null)
                {
                    InheritVanilla = false,
                    LocalInstaller = new(Path.Combine(root, "foreign.jar"), new('A', 64), "1.20.1", InstallLoader.Forge, "47.4.20")
                };
                File.WriteAllBytes(Path.Combine(stage, ".task", "plan.json"), JsonSerializer.SerializeToUtf8Bytes(
                    new InstallTaskPlan(1, id, DateTimeOffset.UtcNow, command), InstallTaskJsonContext.Default.InstallTaskPlan));
                await RejectRecoveryRecord(() => InstallTaskJournal.ReadAsync(root, stage, default));
                using var fixture = new InstallFixture(new FakeMetadata());
                await RejectRecoveryRecord(() => fixture.Install.ResumeInstallationAsync(root, id, !modifying));
            }

            string approved = await MetadataTaskStage(root);
            string original = Path.Combine(approved, ".task", "plan.json");
            byte[] bytes = File.ReadAllBytes(original);
            var plan = await InstallTaskJournal.ReadAsync(root, approved, default);
            File.WriteAllBytes(original, JsonSerializer.SerializeToUtf8Bytes(plan with
            { Command = plan.Command with { GameVersion = "1.20.1" } }, InstallTaskJsonContext.Default.InstallTaskPlan));
            await RejectRecoveryRecord(() => InstallTaskJournal.ReadAsync(root, approved, default));
            File.WriteAllBytes(original, bytes);
            AssertEqual(plan.Id, (await InstallTaskJournal.ReadAsync(root, approved, default)).Id);
            string otherRoot = Path.Combine(root, "other");
            string transplanted = Path.Combine(otherRoot, ".nexa-modify", plan.Id.ToString("N"));
            Directory.CreateDirectory(Path.Combine(transplanted, ".task"));
            File.WriteAllBytes(Path.Combine(transplanted, ".task", "plan.json"), JsonSerializer.SerializeToUtf8Bytes(plan with
            { Command = plan.Command with { RootDirectory = otherRoot } }, InstallTaskJsonContext.Default.InstallTaskPlan));
            await RejectRecoveryRecord(() => InstallTaskJournal.ReadAsync(otherRoot, transplanted, default));
            await InstallTaskJournal.WriteStatusAsync(approved, plan, InstallTaskStatus.Completed, default);
            File.Delete(Path.Combine(approved, ".task", "status.json"));
            await RejectRecoveryRecord(() => InstallTaskJournal.ReadStatusAsync(approved, plan, default));

            string packStage = Path.Combine(root, ".nexa-pack-jobs", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(packStage);
            var pack = new MinecraftModpackPreview(Path.Combine(root, "foreign.mrpack"), new('A', 64), "foreign", "1",
                "modrinth", "1.20.1", null, null, "foreign", 0, 0);
            File.WriteAllBytes(Path.Combine(packStage, "intent.json"), JsonSerializer.SerializeToUtf8Bytes(
                new ModpackIntent(1, new(pack, root)), ModpackJournalJson.Default.ModpackIntent));
            await RejectRecoveryRecord(() => ModpackInstallJournal.OpenAsync(root, packStage, default));
            using var recovery = new InstallFixture(new FakeMetadata());
            AssertFalse((await recovery.Install.RecoverPendingAsync(new([root]))).IsSuccess);
            AssertFalse(Directory.Exists(Path.Combine(root, "versions", "foreign")));
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task RejectRecoveryRecord<T>(Func<Task<T>> read)
    {
        bool rejected = false;
        try { await read(); } catch (InvalidDataException) { rejected = true; }
        AssertTrue(rejected);
    }
}
