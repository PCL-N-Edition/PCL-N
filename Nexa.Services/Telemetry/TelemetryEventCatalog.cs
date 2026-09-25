namespace Nexa.Services.Telemetry;

public enum TelemetryLevel { Necessary, Diagnostic }

/// <summary>Fixed event purpose. Callers cannot promote diagnostic events to necessary.</summary>
public static class TelemetryEventCatalog
{
    public static TelemetryLevel? Level(string name) => name switch
    {
        "app.started" or "app.failure" or "update.checked" or "rollout.checked" => TelemetryLevel.Necessary,
        "game.started" or "game.exited" or "task.started" or "task.finished" or "feature.exposed" or "diagnostic.session" or "diagnostic.metric" or "diagnostic.error" or "feature.used" or "feature.invoked" => TelemetryLevel.Diagnostic,
        _ => null
    };
}
