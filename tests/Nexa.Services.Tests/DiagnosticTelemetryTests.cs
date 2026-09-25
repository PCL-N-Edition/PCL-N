using Nexa.Services.Settings;
using Nexa.Services.Telemetry;
using Nexa.Xsr.State;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async ValueTask DiagnosticMetricsAndErrorsRespectConsentAndSchema()
    {
        var schema = LauncherDefaults.CreateSchema(); var builder = new XsrStateStoreBuilder();
        SettingsService.DeclareState(builder, schema); TelemetryService.DeclareState(builder);
        var store = builder.Build(); var settings = new SettingsService(store, schema, new InMemorySettingsPort());
        using var telemetry = new TelemetryService(store);
        var transport = new RecordingTransport();
        using var session = new LauncherTelemetrySession(telemetry, settings, transport, CreateLogService(), "2.0.0");
        session.RecordMetric("launcher.working_set.mib", 100);
        session.RecordFeature("ui.install.private-name");
        session.RecordError("launch", "error", "System.InvalidOperationException: private-token\n at Nexa.Desktop.Program.Run(String privateArg) in C:\\Users\\private-path:line 1");
        await telemetry.FlushAsync(transport);
        AssertFalse(transport.Batches.SelectMany(batch => batch).Any(item => item.Level == TelemetryLevel.Diagnostic));
        settings.SetValue("TelemetryExperienceProgram", true);
        session.RecordMetric("launcher.working_set.mib", 100);
        session.RecordMetric("launcher.working_set.mib", double.NaN);
        session.RecordMetric("private.metric", 1);
        for (int i = 0; i < 200; i++) session.RecordMetric("scheduler.duration.ms", 1);
        session.RecordMetric("network.request.ms", 25);
        session.RecordFeature("ui.install.private-name"); session.RecordFeature("ui.install.private-name");
        session.RecordError("launch", "error", "System.InvalidOperationException: private-token\n at Nexa.Desktop.Program.Run(String privateArg) in C:\\Users\\private-path:line 1");
        await telemetry.FlushAsync(transport);
        var facts = transport.Batches.SelectMany(batch => batch).ToArray();
        AssertEqual(1, facts.Count(item => item.Name == "feature.used"));
        AssertEqual(2, facts.Count(item => item.Name == "feature.invoked"));
        AssertEqual(2, facts.Count(item => item.Properties.GetValueOrDefault("metric") == "scheduler.duration.ms"));
        AssertEqual(1, facts.Count(item => item.Properties.GetValueOrDefault("metric") == "network.request.ms"));
        AssertTrue(facts.All(CloudflareTelemetryTransport.IsAllowed));
        var error = facts.Single(item => item.Name == "diagnostic.error");
        AssertEqual("System.InvalidOperationException", error.Properties["code"]);
        AssertEqual("Nexa.Desktop.Program.Run", error.Properties["stack"]);
        foreach (string secret in new[] { "private-token", "private-path", "private-name", "privateArg" }) AssertFalse(TelemetryService.SerializeBatch(facts).Contains(secret, StringComparison.Ordinal));
        settings.SetValue("TelemetryExperienceProgram", false);
        session.RecordMetric("launcher.working_set.mib", 100);
        AssertEqual(0, telemetry.PendingCount);
    }
}
