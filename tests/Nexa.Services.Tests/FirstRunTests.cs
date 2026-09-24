using Nexa.Services.Files;
using Nexa.Services.Settings;
using Nexa.Services.Setup;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async ValueTask FirstRunPersistsChoiceAndPreservesExistingData()
    {
        string temp = Path.Combine(Path.GetTempPath(), "nexa-setup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            string initial = Path.Combine(temp, "default"), chosen = Path.Combine(temp, "chosen"), locator = Path.Combine(temp, "bootstrap", "storage.json");
            var service = new FirstRunService(initial, locator);
            AssertTrue(service.Read().Required);
            AssertFalse(Directory.Exists(initial)); // Query has no initialization side effects.
            AssertTrue((await service.CompleteAsync(new(chosen, false))).IsSuccess);
            AssertFalse(service.Read().Required);
            AssertEqual(chosen, LauncherStorageLocation.Read(locator));
            var settings = new LauncherSettingsJsonPort(Path.Combine(chosen, "settings", "settings.json"), LauncherDefaults.CreateSchema());
            AssertEqual("false", settings.Load()["TelemetryExperienceProgram"]);
            AssertFalse((await service.CompleteAsync(new(chosen, true))).IsSuccess);
            AssertEqual("false", settings.Load()["TelemetryExperienceProgram"]);
            AssertFalse(new FirstRunService(chosen, locator).Read().Required);

            string locked = Path.Combine(temp, "locked");
            Directory.CreateDirectory(Path.Combine(locked, "profiles"));
            Directory.CreateDirectory(Path.Combine(locked, "settings"));
            var overridden = new FirstRunService(locked, locator, locationLocked: true);
            AssertFalse((await overridden.CompleteAsync(new(Path.Combine(temp, "other"), true))).IsSuccess);
            AssertTrue((await overridden.CompleteAsync(new(locked, true))).IsSuccess);
            AssertEqual(chosen, LauncherStorageLocation.Read(locator));
            AssertEqual("true", new LauncherSettingsJsonPort(Path.Combine(locked, "settings", "settings.json"), LauncherDefaults.CreateSchema()).Load()["TelemetryExperienceProgram"]);
        }
        finally { Directory.Delete(temp, true); }
    }

    private static async ValueTask FirstRunFailedCommitCanRetryWithoutOverwriting()
    {
        string temp = Path.Combine(Path.GetTempPath(), "nexa-setup-failure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            string initial = Path.Combine(temp, "initial"), chosen = Path.Combine(temp, "chosen"), blocker = Path.Combine(temp, "blocker");
            await File.WriteAllTextAsync(blocker, "existing");
            var service = new FirstRunService(initial, Path.Combine(blocker, "storage.json"));
            AssertFalse((await service.CompleteAsync(new(chosen, true))).IsSuccess);
            AssertFalse(File.Exists(Path.Combine(chosen, "settings", "settings.json")));
            AssertEqual(0, Directory.GetFileSystemEntries(chosen).Length);
            AssertEqual("existing", await File.ReadAllTextAsync(blocker));
            string locator = Path.Combine(temp, "storage.json");
            var retry = new FirstRunService(initial, locator);
            AssertTrue((await retry.CompleteAsync(new(chosen, false))).IsSuccess);

            string occupied = Path.Combine(temp, "occupied"); Directory.CreateDirectory(occupied);
            await File.WriteAllTextAsync(Path.Combine(occupied, "save.txt"), "keep");
            AssertFalse((await new FirstRunService(initial, locator).CompleteAsync(new(occupied, true))).IsSuccess);
            AssertEqual("keep", await File.ReadAllTextAsync(Path.Combine(occupied, "save.txt")));
            AssertEqual(chosen, LauncherStorageLocation.Read(locator));
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            try { await retry.CompleteAsync(new(Path.Combine(temp, "cancel"), false), cancel.Token); }
            catch (OperationCanceledException) { }
            AssertFalse(Directory.Exists(Path.Combine(temp, "cancel")));
        }
        finally { Directory.Delete(temp, true); }
    }
}
