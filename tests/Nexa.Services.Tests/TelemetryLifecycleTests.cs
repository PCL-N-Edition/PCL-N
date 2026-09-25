using Nexa.Services.Logging;
using Nexa.Services.Minecraft.Process;
using Nexa.Services.Settings;
using Nexa.Services.Telemetry;
using Nexa.Xsr;
using Nexa.Xsr.State;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private sealed class ConsentBlockedTransport : ITelemetryTransport
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<bool> SendAsync(IReadOnlyList<TelemetryEvent> batch, CancellationToken cancellationToken = default)
        {
            Started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return true;
        }
    }

    private static async ValueTask DiagnosticRevocationPreservesNecessaryFacts()
    {
        var schema = LauncherDefaults.CreateSchema();
        var builder = new XsrStateStoreBuilder();
        SettingsService.DeclareState(builder, schema);
        TelemetryService.DeclareState(builder);
        var store = builder.Build();
        var settings = new SettingsService(store, schema, new InMemorySettingsPort());
        using var telemetry = new TelemetryService(store);
        using var session = new LauncherTelemetrySession(telemetry, settings,
            new RecordingTransport { Accept = false }, CreateLogService(), "2.0.0");
        AssertFalse(telemetry.Consent);
        session.Record("game.started", "ok");
        AssertEqual(1, telemetry.PendingCount);
        settings.SetValue("TelemetryExperienceProgram", true);
        session.Record("game.started", "ok");
        var blocked = new ConsentBlockedTransport();
        var flush = telemetry.FlushAsync(blocked);
        await blocked.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        settings.SetValue("TelemetryExperienceProgram", false);
        try { await flush; throw new InvalidOperationException("Diagnostic upload was not cancelled."); }
        catch (OperationCanceledException) { }
        AssertEqual(1, telemetry.PendingCount);
        var accepted = new RecordingTransport();
        await telemetry.FlushAsync(accepted);
        AssertTrue(accepted.Batches.Single().All(item => item.Level == TelemetryLevel.Necessary));
        AssertEqual("app.started", accepted.Batches.Single().Single().Name);
        AssertEqual(0, telemetry.PendingCount);
    }

    private static async ValueTask TelemetryLifecycleRecordsOnlyBoundedFacts()
    {
        var builder = new XsrStateStoreBuilder();
        var schema = LauncherDefaults.CreateSchema();
        SettingsService.DeclareState(builder, schema);
        TelemetryService.DeclareState(builder);
        MinecraftProcessStateComposition.DeclareState(builder);
        var store = builder.Build();
        var settings = new SettingsService(store, schema, new InMemorySettingsPort());
        var telemetry = new TelemetryService(store);
        var transport = new RecordingTransport();
        using var session = new LauncherTelemetrySession(telemetry, settings, transport, CreateLogService(), "2.0.0.alpha.4");
        var stateId = store.Resolve(MinecraftProcessStateComposition.SessionsKey);
        var process = new MinecraftProcessSnapshot(Guid.NewGuid(), "private-instance-name", 99, MinecraftProcessState.Created, null, DateTimeOffset.UtcNow, null)
        { InstanceDirectory = "private-path" };
        void Publish(MinecraftProcessSnapshot current)
        {
            var snapshot = store.ReadCollection<MinecraftProcessSnapshot>(stateId);
            store.PublishDelta(stateId, new XsrCollectionDelta<MinecraftProcessSnapshot, Guid>(snapshot.Revision, [current], []));
            session.OnChanged(new(stateId, MinecraftProcessStateComposition.SessionsKey, XsrStateKind.Collection, snapshot.Revision + 1,
                XsrStateAvailability.Available, XsrStateChangeReason.CollectionDeltaApplied));
        }
        Publish(process);
        Publish(process with { State = MinecraftProcessState.Running });
        Publish(process with { State = MinecraftProcessState.Failed, ExitCode = 1 });
        Publish(process with { State = MinecraftProcessState.Failed, ExitCode = 1 });
        session.Write(new(1, DateTimeOffset.UtcNow, LogLevel.Error, "Launcher", "private-exception", "private-token"), "private-log");
        await telemetry.FlushAsync(transport);
        var events = transport.Batches.SelectMany(batch => batch).ToArray();
        AssertEqual(1, events.Count(item => item.Name == "game.started"));
        AssertEqual(1, events.Count(item => item.Name == "game.exited"));
        AssertEqual(1, events.Count(item => item.Name == "app.failure"));
        AssertTrue(events.All(CloudflareTelemetryTransport.IsAllowed));
        AssertFalse(TelemetryService.SerializeBatch(events).Contains("private", StringComparison.Ordinal));
        session.Dispose();
        session.Record("app.failure", "failed");
        AssertEqual(0, telemetry.PendingCount);
    }
}
