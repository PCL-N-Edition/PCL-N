// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

namespace PCL.Desktop.Telemetry;

/// <summary>Consent state applied to optional product analytics and diagnostics.</summary>
internal readonly record struct TelemetryConsent(bool ExperienceProgramEnabled);

/// <summary>
/// Reports the minimum, non-identifying fault signal needed to keep the launcher service healthy.
/// Implementations must not include messages, stack traces, paths, tokens, account data or a user id.
/// </summary>
internal interface IEssentialServiceReporter
{
    void ReportCriticalFailure(Exception exception, string stage, bool canContinue);
}

/// <summary>Optional experience analytics. Every method is a no-op unless the user opted in.</summary>
internal interface IExperienceTelemetry
{
    void CaptureEvent(string eventName, IReadOnlyDictionary<string, string>? properties = null);

    void CaptureException(Exception exception, string stage);

    TelemetryOperation StartOperation(string name, string operation);

    Task<bool?> IsFeatureEnabledAsync(
        string flagKey,
        CancellationToken cancellationToken = default);
}
