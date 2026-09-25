using System.Text;
using System.Text.Json;
using Nexa.Services.Updates;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    internal static async ValueTask UpdateDeletionRequiresSignedOwnership()
    {
        string root = CreateTempDirectory();
        try
        {
            var (key, fingerprint, sign) = GenerateSigningKey();
            var verifier = new UpdateGpgVerifier(key, fingerprint);
            var identity = ScatterIdentity();
            var map = new UpdateBlockMap
            {
                FormatVersion = 2,
                TargetVersion = identity.Version,
                RuntimeId = identity.RuntimeId,
                RuntimeVariant = identity.RuntimeVariant,
                Configuration = identity.Configuration,
                TargetFiles = [new() { Path = "old.dll", Size = 1, Sha256 = UpdateSha([1]) },
                    new() { Path = "modified.dll", Size = 1, Sha256 = UpdateSha([1]) },
                    new() { Path = "UpdateState/cache", Size = 1, Sha256 = UpdateSha([1]) }],
            };
            byte[] data = JsonSerializer.SerializeToUtf8Bytes(map, UpdateJsonContext.Default.UpdateBlockMap);
            byte[] signature = Encoding.ASCII.GetBytes(sign(data));
            var inventory = await VerifiedUpdateInventory.VerifyAsync(data, signature, identity, verifier);
            foreach (var wrong in new[] { identity with { Version = "1.4.10" }, identity with { RuntimeId = "other" } })
            {
                bool refused = false;
                try { await VerifiedUpdateInventory.VerifyAsync(data, signature, wrong, verifier); }
                catch (InvalidDataException) { refused = true; }
                AssertTrue(refused);
            }
            data[^1] ^= 1;
            bool tampered = false;
            try { await VerifiedUpdateInventory.VerifyAsync(data, signature, identity, verifier); }
            catch (InvalidDataException) { tampered = true; }
            AssertTrue(tampered);

            string install = Path.Combine(root, "install");
            Directory.CreateDirectory(Path.Combine(install, "UpdateState"));
            File.WriteAllBytes(Path.Combine(install, "old.dll"), [1]);
            File.WriteAllBytes(Path.Combine(install, "modified.dll"), [2]);
            File.WriteAllBytes(Path.Combine(install, "user.txt"), [1]);
            File.WriteAllBytes(Path.Combine(install, "UpdateState", "cache"), [1]);
            var (stage, files) = StageTree(root, "stage", ("app", [9], null));
            var plan = UpdateStaging.BuildPlan(install, stage, "app", files, inventory);
            AssertEqual(1, plan.DeletePaths.Count);
            AssertEqual("old.dll", plan.DeletePaths[0]);
            bool missingAuthority = false;
            try { UpdateStaging.ApplyPlan(plan); } catch (InvalidDataException) { missingAuthority = true; }
            AssertTrue(missingAuthority);
            AssertTrue(File.Exists(Path.Combine(stage, "app")));
            plan.DeletePaths.Add("user.txt");
            bool injected = false;
            try { UpdateStaging.ApplyPlan(plan, inventory); } catch (InvalidDataException) { injected = true; }
            AssertTrue(injected);
            AssertTrue(File.Exists(Path.Combine(stage, "app")));
            plan.DeletePaths.Remove("user.txt");
            // Changed after planning: the user's modification must survive apply too.
            File.WriteAllBytes(Path.Combine(install, "old.dll"), [3]);
            AssertEqual(0, UpdateStaging.ApplyPlan(plan, inventory).FilesDeleted);
            AssertTrue(File.Exists(Path.Combine(install, "old.dll")));
            File.WriteAllBytes(Path.Combine(install, "old.dll"), [1]);
            var deleteOnly = UpdateStaging.BuildPlan(install, stage, "app", [], inventory);
            AssertEqual(1, UpdateStaging.ApplyPlan(deleteOnly, inventory).FilesDeleted);
            AssertTrue(File.Exists(Path.Combine(install, "modified.dll")));
            AssertTrue(File.Exists(Path.Combine(install, "user.txt")));
            AssertTrue(File.Exists(Path.Combine(install, "UpdateState", "cache")));
            foreach (string alias in new[] { "./old.dll", "sub/../old.dll" })
            {
                var (aliasStage, aliasFiles) = StageTree(root, "alias-stage", ("old.dll", [1], null));
                aliasFiles[0].Path = alias;
                var aliasPlan = UpdateStaging.BuildPlan(install, aliasStage, "old.dll", aliasFiles, inventory);
                AssertEqual(0, aliasPlan.DeletePaths.Count);
                UpdateStaging.ApplyPlan(aliasPlan, inventory);
                AssertTrue(File.Exists(Path.Combine(install, "old.dll")));
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
