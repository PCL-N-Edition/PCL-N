using System.Text.Json.Nodes;
using PCL.Services.Accounts;
using PCL.Services.Composition;
using PCL.Services.Foundation;
using PCL.Services.Settings;
using PCL.Xsr;
using PCL.Xsr.State;

namespace PCL.Services.Tests;

internal static partial class Program
{
    private static (SettingsService Store, SettingsPolicyService Policy) PolicyFixture(ISettingsPort? port = null)
    {
        var schema = LauncherDefaults.CreateSchema();
        var builder = new XsrStateStoreBuilder();
        SettingsService.DeclareState(builder, schema);
        SettingsPolicyContract.DeclareState(builder);
        var settings = new SettingsService(builder.Build(), schema, port ?? new InMemorySettingsPort());
        return (settings, new(settings));
    }
    private static SettingsEffectiveValue Effective(SettingsPolicyService service, string key, string? instance = null)
    {
        var result = service.Read(new(instance)); AssertTrue(result.IsSuccess);
        return result.Value!.Values.Single(item => item.Key == key);
    }

    private static void SettingsCatalogLocksFinalIa()
    {
        var catalog = SettingsCatalog.Read(new(true));
        AssertEqual(8, catalog.GlobalPages.Count); AssertEqual(9, catalog.InstancePages.Count); AssertEqual(10, catalog.InstanceSettingsSections.Count);
        AssertEqual(532, catalog.Entries.Count);
        AssertFalse(catalog.GlobalPages.Any(page => page.Id == "cloud"));
        AssertFalse(catalog.Entries.Any(entry => entry.Page is "cloud" or "sync" || entry.Label.Contains("云同步", StringComparison.Ordinal)));
        AssertTrue(catalog.InstanceSettingsSections.Any(page => page.Id == "backup"));
        var ids = catalog.Entries.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        AssertEqual(catalog.Entries.Count, ids.Count);
        foreach (var item in catalog.Entries)
        {
            AssertTrue(XsrSemanticId.TryParse(item.Id, out _));
            AssertTrue(item.Parent is null || ids.Contains(item.Parent));
            AssertTrue(item.SettingKey is null || item.Definition is not null);
            AssertTrue(item.SettingKey is null || item.Kind == SettingsCatalogEntryKind.Setting);
        }
        AssertFalse(SettingsCatalog.Read(new()).Entries.Any(item => item.DeveloperOnly));
        AssertTrue(catalog.Entries.Any(item => item.DeveloperOnly));
        AssertTrue(catalog.Entries.Where(item => item.SettingKey == "game.title").Count() >= 3);
        AssertTrue(catalog.Entries.Any(item => item.Label == "修改版本" && item.Kind == SettingsCatalogEntryKind.Action));
        AssertTrue(SettingsPolicySchema.ByKey["updates.channel"].Choices.Contains("alpha", StringComparison.Ordinal));
        foreach (var definition in SettingsPolicySchema.Definitions)
            AssertTrue(definition.Validate(definition.SupportsAuto ? new(SettingsOverrideMode.Auto) : new(SettingsOverrideMode.Custom, definition.DefaultValue)) is null);
    }

    private static void SettingsInheritanceIsExplicitAndIsolated()
    {
        var (_, service) = PolicyFixture();
        string first = Path.GetFullPath("settings-fixture-a"), second = Path.GetFullPath("settings-fixture-b");
        AssertEqual(SettingsLayer.Builtin, Effective(service, "game.memory", first).Source);
        AssertTrue(service.Set(new("game.memory", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "4096"))).IsSuccess);
        AssertEqual("4096", Effective(service, "game.memory", first).Value.Value);
        AssertTrue(service.Set(new("game.memory", SettingsLayer.Instance, new(SettingsOverrideMode.Auto), first)).IsSuccess);
        AssertEqual(SettingsOverrideMode.Auto, Effective(service, "game.memory", first).Value.Mode);
        AssertEqual(SettingsLayer.Instance, Effective(service, "game.memory", first).Source);
        AssertTrue(service.Set(new("game.memory", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "8192"))).IsSuccess);
        AssertEqual(SettingsOverrideMode.Auto, Effective(service, "game.memory", first).Value.Mode);
        AssertEqual("8192", Effective(service, "game.memory", second).Value.Value);
        AssertTrue(service.Set(new("game.memory", SettingsLayer.Instance, new(SettingsOverrideMode.Inherit), first)).IsSuccess);
        AssertEqual("8192", Effective(service, "game.memory", first).Value.Value);
        AssertFalse(service.Set(new("game.memory", SettingsLayer.Instance, new(SettingsOverrideMode.Auto), "same-name")).IsSuccess);
        AssertFalse(service.Set(new("developer.enabled", SettingsLayer.Instance, new(SettingsOverrideMode.Custom, "true"), first)).IsSuccess);
        AssertFalse(service.Set(new("game.memory", SettingsLayer.Global, new(SettingsOverrideMode.Auto, "0"))).IsSuccess);
    }

    private static void SettingsOverlayPrecedenceIsPreviewOnly()
    {
        var definition = SettingsPolicySchema.ByKey["game.memory"];
        var custom = new SettingsOverride(SettingsOverrideMode.Custom, "4096");
        var value = SettingsPolicyService.ResolveValue(definition, custom, new(SettingsOverrideMode.Auto), custom, new(SettingsOverrideMode.Custom, "8192"));
        AssertEqual(SettingsLayer.Temporary, value.Source); AssertEqual("8192", value.Value.Value);
        value = SettingsPolicyService.ResolveValue(definition, custom, null, new(SettingsOverrideMode.Auto), new(SettingsOverrideMode.Inherit));
        AssertEqual(SettingsLayer.Profile, value.Source); AssertEqual(SettingsOverrideMode.Auto, value.Value.Mode);
        var (store, service) = PolicyFixture();
        var preview = service.Preview(new([new("game.memory", SettingsLayer.Global, custom)]));
        AssertTrue(preview.IsSuccess);
        AssertEqual("4096", preview.Value!.Values.Single(item => item.Key == "game.memory").Value.Value);
        AssertEqual(SettingsOverrideMode.Auto, Effective(service, "game.memory").Value.Mode);
        AssertFalse(service.Set(new("game.memory", SettingsLayer.Profile, custom, Path.GetFullPath("profile-instance"))).IsSuccess);
        AssertFalse(service.Set(new("game.memory", SettingsLayer.Temporary, custom)).IsSuccess);
        AssertEqual(0L, store.Revision);
    }

    private static async ValueTask SettingsPolicyUsesSealedFoundationRoutes()
    {
        string directory = CreateTempDirectory();
        try
        {
            var host = FoundationComposer.Compose(new InMemorySettingsPort(), LauncherDefaults.CreateSchema(), new LaunchProfileFilePort(Path.Combine(directory, "profiles.json")));
            var runtime = FoundationRuntimeComposer.Compose(host);
            AssertTrue(runtime.Commands.TryResolve(SettingsPolicyContract.SetCommand, out var set));
            AssertTrue(runtime.Queries.TryResolve(SettingsPolicyContract.EffectiveQuery, out var read));
            AssertTrue(runtime.Queries.TryResolve(SettingsPolicyContract.CatalogQuery, out var catalog));
            AssertTrue((await runtime.Commands.Dispatch(set, new SettingsMutation("game.width", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "1440"))).Completion).IsSuccess);
            var result = await runtime.Queries.QueryAsync<SettingsEffectiveQuery, SettingsEffectiveSnapshot>(read, new());
            AssertTrue(result.IsSuccess);
            AssertEqual("1440", result.Value!.Values.Single(item => item.Key == "game.width").Value.Value);
            AssertEqual(1L, host.StateStore.Read<long>(host.StateStore.Resolve(SettingsPolicyContract.RevisionKey)).Value);
            var content = await runtime.Queries.QueryAsync<SettingsCatalogQuery, SettingsCatalogSnapshot>(catalog, new(true));
            AssertTrue(content.IsSuccess); AssertEqual(532, content.Value!.Entries.Count);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void SettingsLegacyStrategiesKeepTheirMeaning()
    {
        var (store, service) = PolicyFixture();
        AssertTrue(store.SetRawValues(new Dictionary<string, string> { ["LaunchRamType"] = "1", ["LaunchRamCustom"] = "15", ["LaunchArgumentWindowType"] = "0" }).IsSuccess);
        AssertEqual("3072", Effective(service, "game.memory").Value.Value);
        AssertEqual("fullscreen", Effective(service, "game.window-mode").Value.Value);
        AssertTrue(service.Set(new("game.memory", SettingsLayer.Global, new(SettingsOverrideMode.Auto))).IsSuccess);
        AssertEqual(SettingsOverrideMode.Auto, Effective(service, "game.memory").Value.Mode);
        AssertTrue(service.Set(new("game.memory", SettingsLayer.Global, new(SettingsOverrideMode.Inherit))).IsSuccess);
        AssertEqual(SettingsOverrideMode.Auto, Effective(service, "game.memory").Value.Mode);
        AssertEqual(SettingsLayer.Builtin, Effective(service, "game.memory").Source);
    }

    private static void SettingsBatchFailurePublishesNothing()
    {
        var port = new PolicyFailingPort(); var (store, service) = PolicyFixture(port);
        var before = store.StateStore.Read<long>(store.StateStore.Resolve(SettingsPolicyContract.RevisionKey));
        port.Fail = true;
        AssertFalse(service.Set(new("game.width", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "1280"))).IsSuccess);
        AssertEqual("854", Effective(service, "game.width").Value.Value);
        AssertEqual(0L, store.Revision);
        AssertEqual(before.Revision, store.StateStore.Read<long>(store.StateStore.Resolve(SettingsPolicyContract.RevisionKey)).Revision);
        port.Fail = false;
        AssertFalse(store.SetRawValues(new Dictionary<string, string> { ["LaunchArgumentWindowWidth"] = "1280", ["LaunchArgumentWindowHeight"] = "bad" }).IsSuccess);
        AssertEqual("854", Effective(service, "game.width").Value.Value);
        AssertEqual(0L, store.Revision);
        AssertTrue(service.SetBatch(new([
            new("network.proxy-address", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "http://localhost:8080")),
            new("network.proxy-mode", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "2"))], 0)).IsSuccess);
        AssertEqual(1L, store.Revision);
        AssertFalse(service.Set(new("network.proxy-address", SettingsLayer.Global, new(SettingsOverrideMode.Custom, ""))).IsSuccess);
    }

    private static void SettingsImportIsPreviewedAtomicAndPrivate()
    {
        var (store, service) = PolicyFixture();
        AssertTrue(service.Set(new("network.proxy-password", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "never-export-this"))).IsSuccess);
        string exported = service.Export(new()).Value!;
        AssertFalse(exported.Contains("never-export-this", StringComparison.Ordinal));
        AssertFalse(exported.Contains("proxy-password", StringComparison.Ordinal));
        const string input = """{"version":1,"scope":"global","values":{"game.width":{"mode":"Custom","value":"1280"},"game.height":{"mode":"Custom","value":"720"}}}""";
        var preview = service.PreviewImport(new(input));
        AssertEqual(0, preview.Errors.Count); AssertEqual(2, preview.Changes.Count);
        AssertEqual("854", Effective(service, "game.width").Value.Value);
        AssertTrue(service.Set(new("developer.enabled", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "true"))).IsSuccess);
        AssertFalse(service.ApplyImport(new(input, preview.Revision)).IsSuccess);
        preview = service.PreviewImport(new(input));
        AssertTrue(service.ApplyImport(new(input, preview.Revision)).IsSuccess);
        AssertEqual(preview.Revision + 1, store.Revision);
        AssertEqual("1280", Effective(service, "game.width").Value.Value);
        AssertEqual("720", Effective(service, "game.height").Value.Value);
        string invalid = input.Replace("1280", "-1", StringComparison.Ordinal);
        AssertTrue(service.PreviewImport(new(invalid)).Errors.Count > 0);
        AssertFalse(service.ApplyImport(new(invalid, store.Revision)).IsSuccess);
        const string secret = """{"version":1,"scope":"global","values":{"network.proxy-password":{"mode":"Custom","value":"injected"}}}""";
        AssertTrue(service.PreviewImport(new(secret)).Errors.Count > 0);
        AssertEqual("never-export-this", Effective(service, "network.proxy-password").Value.Value);
    }

    private static void SettingsLayersSurviveRestartAndPreserveUnknowns()
    {
        string directory = CreateTempDirectory();
        try
        {
            string path = Path.Combine(directory, "settings.json");
            File.WriteAllText(path, """{"schemaVersion":1,"customFuture":{"keep":true},"integerOptions":{"LaunchArgumentWindowWidth":1024},"textOptions":{"FutureKey":"keep"}}""");
            var schema = LauncherDefaults.CreateSchema();
            var (store, service) = PolicyFixture(new LauncherSettingsJsonPort(path, schema));
            string instance = Path.Combine(directory, "versions", "same-name");
            AssertEqual("1024", Effective(service, "game.width").Value.Value);
            AssertTrue(service.Set(new("game.width", SettingsLayer.Instance, new(SettingsOverrideMode.Custom, "1600"), instance)).IsSuccess);
            AssertTrue(service.Set(new("game.width", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "1280"))).IsSuccess);
            var (_, reopened) = PolicyFixture(new LauncherSettingsJsonPort(path, schema));
            AssertEqual("1600", Effective(reopened, "game.width", instance).Value.Value);
            AssertEqual("1280", Effective(reopened, "game.width").Value.Value);
            var saved = JsonNode.Parse(File.ReadAllText(path))!;
            AssertTrue(saved["customFuture"]!["keep"]!.GetValue<bool>());
            AssertEqual("keep", saved["textOptions"]!["FutureKey"]!.GetValue<string>());
            AssertTrue(store.SetRawValue("LaunchArgumentWindowWidth", "1920").IsSuccess);
            AssertEqual("1920", Effective(service, "game.width").Value.Value);
            AssertTrue(store.SetRawValue(SettingsPolicySchema.StorageKey, "{\"version\":99,\"global\":{},\"instances\":{}}").IsSuccess);
            string before = File.ReadAllText(path);
            AssertFalse(service.Set(new("game.width", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "800"))).IsSuccess);
            AssertFalse(service.Read(new()).IsSuccess);
            AssertEqual(before, File.ReadAllText(path));
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class PolicyFailingPort : ISettingsPort
    {
        private readonly InMemorySettingsPort _inner = new();
        public bool Fail { get; set; }
        public IReadOnlyDictionary<string, string> Load() => _inner.Load();
        public void Save(IReadOnlyDictionary<string, string> values) { if (Fail) throw new IOException("fixture failure"); _inner.Save(values); }
    }
}
