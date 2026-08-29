using PCL.Services.Files;

namespace PCL.Services.Tests;

// XSR-515: File capability — canonical application folders and the safe file port with
// atomic writes, traversal refusal, and the size cap.
internal static partial class Program
{
    internal static void FolderTreeResolvesCanonicalNames()
    {
        string directory = CreateTempDirectory();
        try
        {
            AppFolders folders = new(directory);
            string logs = folders.EnsureFolder(FolderNames.Logs);
            AssertTrue(logs.StartsWith(directory, StringComparison.Ordinal));
            AssertTrue(Directory.Exists(logs));
            AssertTrue(Directory.Exists(folders.EnsureFolder(FolderNames.UpdateState)));
            AssertTrue(Directory.Exists(folders.EnsureFolder(FolderNames.Profiles)));

            string resolved = folders.ResolveSafePath(FolderNames.Settings, "settings.json");
            AssertTrue(resolved.StartsWith(Path.Combine(directory, FolderNames.Settings), StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    internal static void TraversalIsRefusedOnEveryPortOperation()
    {
        string directory = CreateTempDirectory();
        try
        {
            AppFolders folders = new(directory);
            SafeFilePort port = new(folders);

            // The port confines to the data ROOT (cross-folder reach is allowed), so the
            // escape attempts must actually leave the root.
            foreach (string evil in (string[])["../../outside.bin", "../Nested/../../outside.bin"])
            {
                bool writeRejected = false;
                try
                {
                    port.WriteTextAsync(FolderNames.Logs, evil, "x").GetAwaiter().GetResult();
                }
                catch (InvalidDataException)
                {
                    writeRejected = true;
                }

                AssertTrue(writeRejected);

                bool readRejected = false;
                try
                {
                    port.TryReadTextAsync(FolderNames.Logs, evil).GetAwaiter().GetResult();
                }
                catch (InvalidDataException)
                {
                    readRejected = true;
                }

                AssertTrue(readRejected);
            }

            // Nothing escaped the root.
            AssertEqual(0, Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Length);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    internal static async ValueTask TextAndBytesRoundTripAtomically()
    {
        string directory = CreateTempDirectory();
        try
        {
            AppFolders folders = new(directory);
            SafeFilePort port = new(folders);

            AssertFalse(port.Exists(FolderNames.Cache, "net/file.bin"));
            AssertNull(await port.TryReadTextAsync(FolderNames.Cache, "net/file.bin"));
            AssertNull(await port.TryReadBytesAsync(FolderNames.Cache, "net/file.bin"));

            await port.WriteTextAsync(FolderNames.Cache, "notes/readme.txt", "第一行\nsecond line");
            AssertTrue(port.Exists(FolderNames.Cache, "notes/readme.txt"));
            AssertEqual("第一行\nsecond line", await port.TryReadTextAsync(FolderNames.Cache, "notes/readme.txt"));

            byte[] payload = [0x00, 0xFF, 0x10];
            await port.WriteBytesAsync(FolderNames.Cache, "net/file.bin", payload);
            AssertTrue((await port.TryReadBytesAsync(FolderNames.Cache, "net/file.bin"))!.SequenceEqual(payload));

            // Overwrite replaces content exactly.
            await port.WriteTextAsync(FolderNames.Cache, "notes/readme.txt", "replaced");
            AssertEqual("replaced", await port.TryReadTextAsync(FolderNames.Cache, "notes/readme.txt"));

            // Atomic writes leave no temporary files behind.
            AssertEqual(2, Directory.GetFiles(Path.Combine(directory, FolderNames.Cache), "*", SearchOption.AllDirectories).Length);

            AssertTrue(port.Delete(FolderNames.Cache, "notes/readme.txt"));
            AssertFalse(port.Delete(FolderNames.Cache, "notes/readme.txt"));
            AssertNull(await port.TryReadTextAsync(FolderNames.Cache, "notes/readme.txt"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    internal static async ValueTask SizeCapAndDefaultRootResolutionBehave()
    {
        string directory = CreateTempDirectory();
        try
        {
            AppFolders folders = new(directory);
            SafeFilePort tinyPort = new(folders, maxBytes: 8);

            bool capRejected = false;
            try
            {
                await tinyPort.WriteBytesAsync(FolderNames.Cache, "big.bin", [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09]);
            }
            catch (InvalidDataException failure)
            {
                capRejected = failure.Message.Contains("大小上限", StringComparison.Ordinal);
            }

            AssertTrue(capRejected);
            AssertFalse(tinyPort.Exists(FolderNames.Cache, "big.bin"));
            await tinyPort.WriteBytesAsync(FolderNames.Cache, "ok.bin", [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08]);
            AssertTrue(tinyPort.Exists(FolderNames.Cache, "ok.bin"));

            // Default root resolution honors the environment override.
            Environment.SetEnvironmentVariable("PCL_NEXA_DATA_DIR", directory);
            try
            {
                AppFolders resolved = AppFolders.ResolveDefault();
                AssertEqual(Path.GetFullPath(directory), resolved.Root);
            }
            finally
            {
                Environment.SetEnvironmentVariable("PCL_NEXA_DATA_DIR", null);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        await Task.CompletedTask;
    }
}
