using System.Text.Json;
using PCL.Services.Settings;
using PCL.Xsr;
using PCL.Xsr.State;

namespace PCL.Services.Tests;

// XSR-503: launcher settings file compatibility — the legacy JSON shape, quarantine recovery,
// atomic saves, and the full legacy key/default universe.
internal static partial class Program
{
    private static string CreateTempDirectory()
    {
        string directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "pcl-services-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static SettingsService CreateLauncherService(string directory, string settingsName = "settings.json")
    {
        SettingsSchema schema = LauncherDefaults.CreateSchema();
        XsrStateStoreBuilder builder = new();
        SettingsService.DeclareState(builder, schema);
        return new SettingsService(
            builder.Build(),
            schema,
            new LauncherSettingsJsonPort(System.IO.Path.Combine(directory, settingsName), schema));
    }

    internal static ValueTask LauncherSchemaMatchesLegacyDefaults()
    {
        SettingsSchema schema = LauncherDefaults.CreateSchema();
        AssertEqual(103, schema.Count);
        AssertEqual(44, schema.Definitions.Count(definition => definition.ValueType == SettingValueType.Bool));
        AssertEqual(42, schema.Definitions.Count(definition => definition.ValueType == SettingValueType.I32));
        AssertEqual(17, schema.Definitions.Count(definition => definition.ValueType == SettingValueType.Text));

        AssertTrue(schema.TryGetDefinition(XsrSemanticId.Parse("LaunchAdvanceJvm")) is { } jvm
            && jvm.DefaultValue.StartsWith("-XX:+UseG1GC -XX:-UseAdaptiveSizePolicy", StringComparison.Ordinal)
            && jvm.DefaultValue.EndsWith("-Dlog4j2.formatMsgNoLookups=true", StringComparison.Ordinal));
        AssertTrue(schema.TryGetDefinition(XsrSemanticId.Parse("LaunchArgumentWindowWidth")) is { } width
            && width.DefaultValue == "854");
        AssertTrue(schema.TryGetDefinition(XsrSemanticId.Parse("SystemDebugMode")) is { } debug
            && debug.DefaultValue == "false");
        AssertTrue(schema.TryGetDefinition(XsrSemanticId.Parse("UiLanguage")) is { } language
            && language.DefaultValue == "auto");
        AssertTrue(schema.TryGetDefinition(XsrSemanticId.Parse("LoginMsAuthType")) is { } auth
            && auth.DefaultValue == "1");
        AssertTrue(schema.TryGetDefinition(XsrSemanticId.Parse("ExperimentalMinecraftAiApiModel")) is { } model
            && model.DefaultValue == "gemma-4-e2b");
        return ValueTask.CompletedTask;
    }

    internal static ValueTask JsonPortRoundTripsLegacyShape()
    {
        string directory = CreateTempDirectory();
        try
        {
            string path = System.IO.Path.Combine(directory, "settings.json");
            File.WriteAllText(path, """
                {
                  "schemaVersion": 1,
                  "automaticallyRepairGameIssues": false,
                  "colorMode": "Dark",
                  "booleanOptions": { "SystemDebugMode": true },
                  "integerOptions": { "LaunchRamCustom": 32 },
                  "textOptions": { "UiLogoText": "hi" }
                }
                """);

            LauncherSettingsJsonPort port = new(path, LauncherDefaults.CreateSchema());
            IReadOnlyDictionary<string, string> loaded = port.Load();
            AssertEqual(3, loaded.Count);
            AssertTrue(loaded["SystemDebugMode"] == "true" && loaded["LaunchRamCustom"] == "32" && loaded["UiLogoText"] == "hi");

            port.Save(loaded);
            using JsonDocument saved = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = saved.RootElement;
            AssertEqual(1, root.GetProperty("schemaVersion").GetInt32());
            AssertFalse(root.GetProperty("automaticallyRepairGameIssues").GetBoolean());
            AssertEqual("Dark", root.GetProperty("colorMode").GetString());
            AssertTrue(root.GetProperty("booleanOptions").GetProperty("SystemDebugMode").GetBoolean());
            AssertEqual(32, root.GetProperty("integerOptions").GetProperty("LaunchRamCustom").GetInt32());
            AssertEqual("hi", root.GetProperty("textOptions").GetProperty("UiLogoText").GetString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    internal static ValueTask JsonPortWritesFreshFixedFieldDefaults()
    {
        string directory = CreateTempDirectory();
        try
        {
            string path = System.IO.Path.Combine(directory, "settings.json");
            LauncherSettingsJsonPort port = new(path, LauncherDefaults.CreateSchema());
            port.Save(new Dictionary<string, string>());

            using JsonDocument saved = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = saved.RootElement;
            AssertEqual(1, root.GetProperty("schemaVersion").GetInt32());
            AssertTrue(root.GetProperty("automaticallyRepairGameIssues").GetBoolean());
            AssertEqual("System", root.GetProperty("colorMode").GetString());
            AssertEqual("CatBlue", root.GetProperty("lightColor").GetString());
            AssertEqual("CatBlue", root.GetProperty("darkColor").GetString());
            AssertEqual("PreferOfficialWithMirrorFallback", root.GetProperty("downloadSource").GetString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    internal static ValueTask JsonPortQuarantinesUnsupportedSchema()
    {
        string directory = CreateTempDirectory();
        try
        {
            string path = System.IO.Path.Combine(directory, "settings.json");
            const string original = """{ "schemaVersion": 2 }""";
            File.WriteAllText(path, original);

            LauncherSettingsJsonPort port = new(path, LauncherDefaults.CreateSchema());
            bool threw = false;
            try
            {
                port.Load();
            }
            catch (IOException)
            {
                threw = true;
            }

            AssertTrue(threw);
            AssertTrue(File.Exists(port.QuarantinePath));
            AssertTrue(File.ReadAllText(port.QuarantinePath).Contains("\"schemaVersion\": 2", StringComparison.Ordinal));

            SettingsService service = CreateLauncherSchemaService(port);
            AssertTrue(service.LoadError is not null);
            AssertEqual(SettingsErrors.PersistFailedCode, service.LoadError!.Code);
            AssertTrue(service.GetValue<int>("LaunchArgumentWindowWidth").TryGetValue(out int width) && width == 854);
            XsrStateId id = service.StateStore.Resolve(XsrSemanticId.Parse("LaunchArgumentWindowWidth"));
            AssertEqual(XsrStateAvailability.Unavailable, service.StateStore.Read<int>(id).Availability);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    internal static ValueTask JsonPortRecoversInvalidItems()
    {
        string directory = CreateTempDirectory();
        try
        {
            string path = System.IO.Path.Combine(directory, "settings.json");
            File.WriteAllText(path, """
                {
                  "schemaVersion": 1,
                  "future": { "enabled": true },
                  "booleanOptions": { "SystemDebugMode": true, "BadBool": 42 },
                  "integerOptions": { "LaunchRamCustom": 7, "BadInt": "nope" },
                  "textOptions": { "UiLogoText": "ok", "BadText": [] }
                }
                """);

            LauncherSettingsJsonPort port = new(path, LauncherDefaults.CreateSchema());
            IReadOnlyDictionary<string, string> loaded = port.Load();
            AssertEqual(3, loaded.Count);
            AssertTrue(loaded["SystemDebugMode"] == "true" && loaded["LaunchRamCustom"] == "7" && loaded["UiLogoText"] == "ok");
            AssertTrue(File.Exists(port.QuarantinePath));

            SettingsService service = CreateLauncherSchemaService(port);
            AssertTrue(service.LoadError is null);
            AssertTrue(service.GetValue<bool>("SystemDebugMode").TryGetValue(out bool debug) && debug);
            AssertTrue(service.GetValue<int>("LaunchRamCustom").TryGetValue(out int ram) && ram == 7);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    internal static ValueTask JsonPortMissingFileIsEmpty()
    {
        string directory = CreateTempDirectory();
        try
        {
            string path = System.IO.Path.Combine(directory, "settings.json");
            LauncherSettingsJsonPort port = new(path, LauncherDefaults.CreateSchema());
            AssertEqual(0, port.Load().Count);

            SettingsService service = CreateLauncherSchemaService(port);
            AssertTrue(service.LoadError is null);
            AssertEqual(0, service.SkippedEntryCount);
            AssertTrue(service.GetValue<string>("UiLanguage").TryGetValue(out string? language) && language == "auto");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    internal static ValueTask JsonPortPreservesUnknownContent()
    {
        string directory = CreateTempDirectory();
        try
        {
            string path = System.IO.Path.Combine(directory, "settings.json");
            File.WriteAllText(path, """
                {
                  "schemaVersion": 1,
                  "futureFeature": { "enabled": true, "level": 3 },
                  "booleanOptions": { "SystemDebugMode": false, "SomeFutureToggle": true },
                  "textOptions": { "UiLogoText": "keep" }
                }
                """);

            LauncherSettingsJsonPort port = new(path, LauncherDefaults.CreateSchema());
            SettingsService service = CreateLauncherSchemaService(port);
            AssertTrue(service.SetValue("SystemDebugMode", true).IsSuccess);

            using JsonDocument saved = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = saved.RootElement;
            AssertTrue(root.GetProperty("futureFeature").GetProperty("enabled").GetBoolean());
            AssertEqual(3, root.GetProperty("futureFeature").GetProperty("level").GetInt32());
            AssertTrue(root.GetProperty("booleanOptions").GetProperty("SomeFutureToggle").GetBoolean());
            AssertTrue(root.GetProperty("booleanOptions").GetProperty("SystemDebugMode").GetBoolean());
            AssertEqual("keep", root.GetProperty("textOptions").GetProperty("UiLogoText").GetString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    internal static ValueTask SettingsOverJsonPortEndToEnd()
    {
        string directory = CreateTempDirectory();
        try
        {
            string path = System.IO.Path.Combine(directory, "settings.json");
            SettingsService first = CreateLauncherPathService(path);

            AssertTrue(first.GetValue<string>("LaunchAdvanceJvm").TryGetValue(out string? jvm)
                && jvm.StartsWith("-XX:+UseG1GC", StringComparison.Ordinal));
            AssertTrue(first.SetValue("LaunchRamCustom", 32).IsSuccess);
            AssertTrue(first.SetValue("UiLogoText", "cat launcher").IsSuccess);
            AssertTrue(first.SetValue("SystemHttpProxy", "http://127.0.0.1:7890").IsSuccess);

            using (JsonDocument written = JsonDocument.Parse(File.ReadAllText(path)))
            {
                AssertEqual(32, written.RootElement.GetProperty("integerOptions").GetProperty("LaunchRamCustom").GetInt32());
                AssertEqual("cat launcher", written.RootElement.GetProperty("textOptions").GetProperty("UiLogoText").GetString());
                AssertTrue(written.RootElement.GetProperty("automaticallyRepairGameIssues").GetBoolean());
            }

            SettingsService restarted = CreateLauncherPathService(path);
            AssertTrue(restarted.GetValue<int>("LaunchRamCustom").TryGetValue(out int ram) && ram == 32);
            AssertTrue(restarted.GetValue<string>("UiLogoText").TryGetValue(out string? logo) && logo == "cat launcher");
            AssertTrue(restarted.GetValue<string>("SystemHttpProxy").TryGetValue(out string? proxy) && proxy == "http://127.0.0.1:7890");
            AssertTrue(restarted.GetValue<int>("UiMusicVolume").TryGetValue(out int volume) && volume == 500);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    private static SettingsService CreateLauncherSchemaService(ISettingsPort port)
    {
        SettingsSchema schema = LauncherDefaults.CreateSchema();
        XsrStateStoreBuilder builder = new();
        SettingsService.DeclareState(builder, schema);
        return new SettingsService(builder.Build(), schema, port);
    }

    private static SettingsService CreateLauncherPathService(string path)
    {
        SettingsSchema schema = LauncherDefaults.CreateSchema();
        XsrStateStoreBuilder builder = new();
        SettingsService.DeclareState(builder, schema);
        return new SettingsService(builder.Build(), schema, new LauncherSettingsJsonPort(path, schema));
    }

    internal static ValueTask LinePortRejectsUnrepresentableValues()
    {
        string directory = CreateTempDirectory();
        try
        {
            string path = System.IO.Path.Combine(directory, "settings.ini");
            var (service, _) = CreateService(new SettingsFilePort(path));

            XsrResult rejected = service.SetValue(KeyLabel, "two\nlines");
            AssertFalse(rejected.IsSuccess);
            AssertEqual(SettingsErrors.InvalidValueCode, rejected.Error!.Code);
            AssertTrue(service.GetValue<string>(KeyLabel).TryGetValue(out string? label) && label == "default");
            AssertFalse(File.Exists(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        return ValueTask.CompletedTask;
    }
}
