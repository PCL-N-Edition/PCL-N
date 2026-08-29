using System.Security.Cryptography;
using System.Text.Json;
using PCL.Services.Updates;

namespace PCL.Services.Tests;

// XSR-512: staged install core — verify the staged tree, flatten single-package roots,
// build the install plan with managed leftovers, and apply it with safe paths, atomic
// replaces, re-verification, and deletes.
internal static partial class Program
{
    private static string UpdateSha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private static (string StagedRoot, List<UpdateFileEntry> Files) StageTree(
        string directory,
        string stagedName,
        params (string RelativePath, byte[] Content, int? UnixMode)[] files)
    {
        string stagedRoot = Path.Combine(directory, stagedName);
        List<UpdateFileEntry> entries = [];
        foreach ((string relativePath, byte[] content, int? unixMode) in files)
        {
            string path = Path.Combine(stagedRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, content);
            entries.Add(new UpdateFileEntry
            {
                Path = relativePath,
                Sha256 = UpdateSha(content),
                Size = content.Length,
                UnixMode = unixMode,
            });
        }

        return (stagedRoot, entries);
    }

    internal static void StagedTreeVerificationRejectsMismatches()
    {
        string directory = CreateTempDirectory();
        try
        {
            (string stagedRoot, List<UpdateFileEntry> files) = StageTree(
                directory,
                "staged",
                ("PCL-N-Edition.exe", [0x01, 0x02], null),
                ("data/config.json", "{\"a\":1}"u8.ToArray(), null));

            // A matching tree verifies silently.
            UpdateStaging.VerifyStagedTree(stagedRoot, files);

            // A missing file fails naming the path.
            File.Delete(Path.Combine(stagedRoot, "data", "config.json"));
            bool missingRejected = false;
            try
            {
                UpdateStaging.VerifyStagedTree(stagedRoot, files);
            }
            catch (InvalidDataException failure)
            {
                missingRejected = failure.Message.Contains("config.json", StringComparison.Ordinal);
            }

            AssertTrue(missingRejected);

            // A hash mismatch fails.
            (string corruptRoot, List<UpdateFileEntry> corruptFiles) = StageTree(
                directory,
                "corrupt",
                ("PCL-N-Edition.exe", [0x01, 0x02], null));
            File.WriteAllBytes(Path.Combine(corruptRoot, "PCL-N-Edition.exe"), [0x01, 0x03]);
            bool hashRejected = false;
            try
            {
                UpdateStaging.VerifyStagedTree(corruptRoot, corruptFiles);
            }
            catch (InvalidDataException failure)
            {
                hashRejected = failure.Message.Contains("SHA-256", StringComparison.Ordinal);
            }

            AssertTrue(hashRejected);

            // A size mismatch fails.
            (string sizeRoot, List<UpdateFileEntry> sizeFiles) = StageTree(
                directory,
                "sizes",
                ("PCL-N-Edition.exe", [0x01, 0x02], null));
            File.WriteAllBytes(Path.Combine(sizeRoot, "PCL-N-Edition.exe"), [0x01, 0x02, 0x03]);
            bool sizeRejected = false;
            try
            {
                UpdateStaging.VerifyStagedTree(sizeRoot, sizeFiles);
            }
            catch (InvalidDataException failure)
            {
                sizeRejected = failure.Message.Contains("大小不匹配", StringComparison.Ordinal);
            }

            AssertTrue(sizeRejected);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    internal static void FlattenSingleRootCollapsesWrapperFolders()
    {
        string directory = CreateTempDirectory();
        try
        {
            // Nested wrapper folders collapse until mixed content appears.
            string staged = Path.Combine(directory, "staged", "PCL_N_bundle", "inner");
            Directory.CreateDirectory(staged);
            File.WriteAllBytes(Path.Combine(staged, "launcher.exe"), [0x01]);
            Directory.CreateDirectory(Path.Combine(staged, "data"));

            UpdateStaging.FlattenSingleRoot(Path.Combine(directory, "staged"));

            AssertTrue(File.Exists(Path.Combine(directory, "staged", "launcher.exe")));
            AssertTrue(Directory.Exists(Path.Combine(directory, "staged", "data")));
            AssertFalse(Directory.Exists(Path.Combine(directory, "staged", "PCL_N_bundle")));

            // A root that directly holds files stays untouched.
            string direct = Path.Combine(directory, "direct");
            Directory.CreateDirectory(direct);
            File.WriteAllBytes(Path.Combine(direct, "a.bin"), [0x09]);
            UpdateStaging.FlattenSingleRoot(direct);
            AssertTrue(File.Exists(Path.Combine(direct, "a.bin")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    internal static void BuildPlanInventoriesManagedLeftovers()
    {
        string directory = CreateTempDirectory();
        try
        {
            (string stagedRoot, List<UpdateFileEntry> files) = StageTree(
                directory,
                "staged",
                ("PCL-N-Edition.exe", [0x01], null),
                ("data/new.json", [0x02], null));

            string installRoot = Path.Combine(directory, "install");
            Directory.CreateDirectory(Path.Combine(installRoot, "data"));
            File.WriteAllBytes(Path.Combine(installRoot, "PCL-N-Edition.exe"), [0x09]);
            File.WriteAllBytes(Path.Combine(installRoot, "data", "old.json"), [0x03]);
            File.WriteAllBytes(Path.Combine(installRoot, "legacy.dll"), [0x04]);
            Directory.CreateDirectory(Path.Combine(installRoot, "UpdateState"));
            File.WriteAllBytes(Path.Combine(installRoot, "UpdateState", "installed.blockmap.json"), [0x05]);

            UpdateInstallPlan plan = UpdateStaging.BuildPlan(installRoot, stagedRoot, "PCL-N-Edition.exe", files);

            AssertEqual(1, plan.FormatVersion);
            AssertEqual("PCL-N-Edition.exe", plan.EntryRelativePath);
            AssertEqual(2, plan.Files.Count);
            AssertTrue(plan.DeletePaths.Contains("legacy.dll", StringComparer.OrdinalIgnoreCase));
            AssertTrue(plan.DeletePaths.Contains("data/old.json", StringComparer.OrdinalIgnoreCase));
            AssertEqual(2, plan.DeletePaths.Count);
            AssertFalse(plan.DeletePaths.Any(path => path.StartsWith("UpdateState", StringComparison.Ordinal)));

            // Round trips through the plan file contract.
            byte[] serialized = JsonSerializer.SerializeToUtf8Bytes(plan, UpdateJsonContext.Default.UpdateInstallPlan);
            UpdateInstallPlan? restored = JsonSerializer.Deserialize(serialized, UpdateJsonContext.Default.UpdateInstallPlan);
            AssertTrue(restored is not null);
            AssertEqual(plan.DeletePaths.Count, restored!.DeletePaths.Count);
            AssertEqual(plan.Files[0].Path, restored!.Files[0].Path);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    internal static void ApplyPlanPlacesFilesAndRunsDeletes()
    {
        string directory = CreateTempDirectory();
        try
        {
            (string stagedRoot, List<UpdateFileEntry> files) = StageTree(
                directory,
                "staged",
                ("PCL-N-Edition.exe", [0x01, 0x02, 0x03], 493),
                ("data/config.json", "{\"new\":true}"u8.ToArray(), null));

            string installRoot = Path.Combine(directory, "install");
            Directory.CreateDirectory(installRoot);
            File.WriteAllBytes(Path.Combine(installRoot, "PCL-N-Edition.exe"), [0x99]);
            File.WriteAllBytes(Path.Combine(installRoot, "obsolete.bin"), [0x88]);

            UpdateInstallPlan plan = UpdateStaging.BuildPlan(installRoot, stagedRoot, "PCL-N-Edition.exe", files);
            plan.DeletePaths.Add("gone/missing.bin");

            UpdateStaging.ApplyPlan(plan);

            byte[] landed = File.ReadAllBytes(Path.Combine(installRoot, "PCL-N-Edition.exe"));
            AssertTrue(landed.SequenceEqual<byte>([0x01, 0x02, 0x03]));
            AssertEqual("{\"new\":true}", File.ReadAllText(Path.Combine(installRoot, "data", "config.json")));
            AssertFalse(File.Exists(Path.Combine(installRoot, "obsolete.bin")));
            AssertFalse(File.Exists(Path.Combine(stagedRoot, "PCL-N-Edition.exe")));

            // Idempotent replay: the staged files are gone now, so a second apply refuses
            // instead of corrupting the installation.
            bool refused = false;
            try
            {
                UpdateStaging.ApplyPlan(plan);
            }
            catch (InvalidDataException)
            {
                refused = true;
            }

            AssertTrue(refused);
            AssertEqual(3, File.ReadAllBytes(Path.Combine(installRoot, "PCL-N-Edition.exe")).Length);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    internal static void UnsafePathsAreRefusedEverywhere()
    {
        string directory = CreateTempDirectory();
        try
        {
            string installRoot = Path.Combine(directory, "install");
            Directory.CreateDirectory(installRoot);
            string outside = Path.Combine(directory, "outside.bin");
            File.WriteAllBytes(outside, [0x77]);

            // Traversal in a manifest entry is refused during planning.
            UpdateFileEntry escaping = new()
            {
                Path = "../../outside.bin",
                Sha256 = UpdateSha([0x77]),
                Size = 1,
            };
            bool planRejected = false;
            try
            {
                UpdateStaging.BuildPlan(installRoot, directory, "launcher", [escaping]);
            }
            catch (InvalidDataException)
            {
                planRejected = true;
            }

            AssertTrue(planRejected);
            AssertTrue(File.ReadAllBytes(outside).SequenceEqual<byte>([0x77]));

            // Traversal in a delete entry is refused during apply.
            UpdateInstallPlan plan = new()
            {
                InstallRoot = installRoot,
                StagedRoot = directory,
                EntryRelativePath = "launcher",
                Files = [],
                DeletePaths = ["../../outside.bin"],
            };
            bool applyRejected = false;
            try
            {
                UpdateStaging.ApplyPlan(plan);
            }
            catch (InvalidDataException)
            {
                applyRejected = true;
            }

            AssertTrue(applyRejected);
            AssertTrue(File.ReadAllBytes(outside).SequenceEqual<byte>([0x77]));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
