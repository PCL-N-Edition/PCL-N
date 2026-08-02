// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using PCL.Application.Settings;
using PCL.Core.Logging;
using PCL.Desktop.Features.Settings.Views;
using PCL.Desktop.Paths;
using Sentry;
using Sentry.Protocol;

namespace PCL.Desktop.Telemetry;

/// <summary>
/// Process-wide telemetry boundary. Mandatory service diagnostics and optional experience data use
/// separate Sentry projects; PostHog is created only after explicit opt-in.
/// </summary>
internal sealed class LauncherTelemetry : IEssentialServiceReporter, IExperienceTelemetry, IDisposable
{
    public const string ExperienceSettingKey = "TelemetryExperienceProgram";
    public const string AnonymousIdSettingKey = "TelemetryAnonymousId";

    private static readonly object Gate = new();
    private static readonly LauncherTelemetry Instance = new();
    private static readonly TimeSpan ExitFlushTimeout = TimeSpan.FromSeconds(2);

    private SentryClient? _essentialClient;
    private SentryOptions? _essentialOptions;
    private IDisposable? _experienceSentry;
    private PostHogExperienceClient? _postHog;
    private bool _initialized;
    private bool _experienceEnabled;
    private TelemetryOperation? _startupOperation;

    private LauncherTelemetry()
    {
    }

    public static bool ExperienceProgramEnabled
    {
        get
        {
            lock (Gate)
                return Instance._experienceEnabled;
        }
    }

    public static void Initialize(LauncherSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (Gate)
        {
            Instance.EnsureEssentialClient();
            Instance.ApplyConsentCore(settings);
            Instance._initialized = true;
            Instance._startupOperation ??= Instance.StartOperationCore("launcher.startup", "app.startup");
        }
    }

    public static void ApplySettings(LauncherSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (Gate)
        {
            if (!Instance._initialized)
                return;
            Instance.ApplyConsentCore(settings);
        }
    }

    public static void MarkStartupReady()
    {
        TelemetryOperation? startup;
        lock (Gate)
        {
            startup = Instance._startupOperation;
            Instance._startupOperation = null;
        }
        startup?.Complete();
        CaptureEvent("app_started", TelemetryDataPolicy.CreateEnvironmentBuckets());
    }

    public static void ReportUnhandledException(Exception exception, string stage, bool canContinue) =>
        Instance.ReportCriticalFailure(exception, stage, canContinue);

    public static void CaptureEvent(
        string eventName,
        IReadOnlyDictionary<string, string>? properties = null) =>
        Instance.CaptureEventCore(eventName, properties);

    public static void CaptureException(Exception exception, string stage) =>
        Instance.CaptureExceptionCore(exception, stage);

    public static TelemetryOperation StartOperation(string name, string operation) =>
        Instance.StartOperationCore(name, operation);

    public static Task<bool?> IsFeatureEnabledAsync(
        string flagKey,
        CancellationToken cancellationToken = default) =>
        Instance.IsFeatureEnabledCoreAsync(flagKey, cancellationToken);

    public static void ClearPendingExperienceData()
    {
        lock (Gate)
        {
            Instance._postHog?.ClearPending();
            if (!Instance._experienceEnabled)
                return;

            LauncherSettings settings = LauncherSettingsPageBinder.LoadSettings();
            Instance.StopExperienceClients();
            Instance._experienceEnabled = true;
            Instance.StartExperienceClients(settings);
        }
    }

    public static void ResetAnonymousId()
    {
        LauncherSettingsPageBinder.UpdateSettings(settings =>
        {
            settings.RemoveTextOption(AnonymousIdSettingKey);
            return settings;
        });
        lock (Gate)
        {
            if (!Instance._experienceEnabled)
                return;
            LauncherSettings settings = LauncherSettingsPageBinder.LoadSettings();
            Instance.StopExperienceClients();
            Instance._experienceEnabled = true;
            Instance.StartExperienceClients(settings);
        }
    }

    public static void Shutdown()
    {
        PostHogExperienceClient? postHog;
        lock (Gate)
            postHog = Instance._postHog;

        try
        {
            using CancellationTokenSource timeout = new(ExitFlushTimeout);
            postHog?.FlushAsync(timeout.Token).GetAwaiter().GetResult();
        }
        catch
        {
            // A bounded best-effort flush must not delay or break process shutdown.
        }

        try
        {
            SentrySdk.Flush(ExitFlushTimeout);
        }
        catch
        {
            // ignore
        }

        try
        {
            Instance._essentialClient?.FlushAsync(ExitFlushTimeout).GetAwaiter().GetResult();
        }
        catch
        {
            // ignore
        }

        lock (Gate)
        {
            Instance.StopExperienceClients();
            Instance._essentialClient?.Dispose();
            Instance._essentialClient = null;
            Instance._essentialOptions = null;
            Instance._initialized = false;
        }
    }

    void IEssentialServiceReporter.ReportCriticalFailure(Exception exception, string stage, bool canContinue) =>
        ReportCriticalFailure(exception, stage, canContinue);

    void IExperienceTelemetry.CaptureEvent(
        string eventName,
        IReadOnlyDictionary<string, string>? properties) =>
        CaptureEventCore(eventName, properties);

    void IExperienceTelemetry.CaptureException(Exception exception, string stage) =>
        CaptureExceptionCore(exception, stage);

    TelemetryOperation IExperienceTelemetry.StartOperation(string name, string operation) =>
        StartOperationCore(name, operation);

    Task<bool?> IExperienceTelemetry.IsFeatureEnabledAsync(
        string flagKey,
        CancellationToken cancellationToken) =>
        IsFeatureEnabledCoreAsync(flagKey, cancellationToken);

    void IDisposable.Dispose() => Shutdown();

    private void EnsureEssentialClient()
    {
        if (_essentialClient is not null)
            return;

        string dsn = ReadConfiguration("PCL_SENTRY_ESSENTIAL_DSN", "SENTRY_ESSENTIAL_DSN");
        if (string.IsNullOrWhiteSpace(dsn))
            return;

        SentryOptions options = new()
        {
            Dsn = dsn,
            Release = TelemetryDataPolicy.Release,
            Environment = TelemetryDataPolicy.ReleaseChannel,
            SendDefaultPii = false,
            MaxBreadcrumbs = 0,
            MaxCacheItems = 0,
            AutoSessionTracking = false,
            TracesSampleRate = 0,
            ShutdownTimeout = TimeSpan.Zero,
            FlushTimeout = TimeSpan.FromSeconds(1),
            CaptureFailedRequests = false,
            DisableSentryHttpMessageHandler = true
        };
        options.DisableAppDomainUnhandledExceptionCapture();
        options.DisableUnobservedTaskExceptionCapture();
        options.DisableAppDomainProcessExitFlush();
        options.SetBeforeSend(ScrubEssentialEvent);
        _essentialOptions = options;
        _essentialClient = new SentryClient(options);
    }

    private void ApplyConsentCore(LauncherSettings settings)
    {
        bool enabled = settings.GetBooleanOption(
            ExperienceSettingKey,
            LauncherSettingDefaults.GetBoolean(ExperienceSettingKey));
        if (enabled == _experienceEnabled && (!enabled || _postHog is not null || _experienceSentry is not null))
            return;

        StopExperienceClients();
        _experienceEnabled = enabled;
        if (enabled)
            StartExperienceClients(settings);
        else
            RemoveAnonymousIdIfPresent(settings);
    }

    private void StartExperienceClients(LauncherSettings settings)
    {
        string anonymousId = settings.GetTextOption(AnonymousIdSettingKey, string.Empty);
        if (string.IsNullOrWhiteSpace(anonymousId))
        {
            anonymousId = Guid.NewGuid().ToString("N");
            string idToPersist = anonymousId;
            LauncherSettingsPageBinder.UpdateSettings(current =>
            {
                current.SetTextOption(AnonymousIdSettingKey, idToPersist);
                return current;
            });
        }

        string sentryDsn = ReadConfiguration("PCL_SENTRY_DSN", "SENTRY_DSN");
        if (!string.IsNullOrWhiteSpace(sentryDsn))
        {
            SentryOptions options = new()
            {
                Dsn = sentryDsn,
                Release = TelemetryDataPolicy.Release,
                Environment = TelemetryDataPolicy.ReleaseChannel,
                IsGlobalModeEnabled = true,
                SendDefaultPii = false,
                IsEnvironmentUser = false,
                MaxBreadcrumbs = 50,
                MaxCacheItems = 0,
                AutoSessionTracking = true,
                TracesSampleRate = 1.0,
                ShutdownTimeout = TimeSpan.Zero,
                FlushTimeout = TimeSpan.FromSeconds(2),
                CaptureFailedRequests = false,
                DisableSentryHttpMessageHandler = true,
                CacheDirectoryPath = null
            };
            options.DisableAppDomainUnhandledExceptionCapture();
            options.DisableUnobservedTaskExceptionCapture();
            options.DisableAppDomainProcessExitFlush();
            options.SetBeforeSend(ScrubExperienceEvent);
            options.SetBeforeSendTransaction(static transaction => transaction);
            _experienceSentry = SentrySdk.Init(options);
        }

        string postHogToken = ReadConfiguration("PCL_POSTHOG_PROJECT_TOKEN", "POSTHOG_PROJECT_TOKEN");
        string postHogHost = ReadConfiguration("PCL_POSTHOG_HOST", "POSTHOG_HOST");
        if (!string.IsNullOrWhiteSpace(postHogToken) &&
            Uri.TryCreate(
                string.IsNullOrWhiteSpace(postHogHost) ? "https://us.i.posthog.com/" : postHogHost,
                UriKind.Absolute,
                out Uri? host) &&
            host.Scheme == Uri.UriSchemeHttps)
        {
            _postHog = new PostHogExperienceClient(postHogToken, EnsureTrailingSlash(host), anonymousId);
        }
    }

    private void StopExperienceClients()
    {
        _experienceEnabled = false;
        _postHog?.DisableAndClear();
        _postHog?.Dispose();
        _postHog = null;
        _experienceSentry?.Dispose();
        _experienceSentry = null;
        try
        {
            SentrySdk.ConfigureScope(static scope => scope.Clear());
        }
        catch
        {
            // ignore
        }
    }

    private static void RemoveAnonymousIdIfPresent(LauncherSettings settings)
    {
        if (!settings.TryGetTextOption(AnonymousIdSettingKey, out string? id) || string.IsNullOrWhiteSpace(id))
            return;
        LauncherSettingsPageBinder.UpdateSettings(current =>
        {
            current.RemoveTextOption(AnonymousIdSettingKey);
            return current;
        });
    }

    private void ReportCriticalFailure(Exception exception, string stage, bool canContinue)
    {
        ArgumentNullException.ThrowIfNull(exception);
        try
        {
            SentryClient? essential;
            bool experience;
            lock (Gate)
            {
                essential = _essentialClient;
                experience = _experienceEnabled;
            }

            if (essential is not null)
            {
                string normalizedStage = TelemetryDataPolicy.NormalizeName(stage);
                string fingerprint = TelemetryDataPolicy.CreateFailureFingerprint(exception, normalizedStage);
                SentryEvent signal = new()
                {
                    Message = new SentryMessage { Message = "critical_failure" },
                    Level = SentryLevel.Fatal,
                    Fingerprint = [fingerprint]
                };
                signal.SetTag("app_version", TelemetryDataPolicy.Release);
                signal.SetTag("release_channel", TelemetryDataPolicy.ReleaseChannel);
                signal.SetTag("platform", TelemetryDataPolicy.Platform);
                signal.SetTag("architecture", TelemetryDataPolicy.Architecture);
                signal.SetTag("process_role", "desktop");
                signal.SetTag("failure_stage", normalizedStage);
                signal.SetTag("failure_category", TelemetryDataPolicy.NormalizeName(exception.GetType().Name));
                signal.SetTag("critical_failure", canContinue ? "recoverable" : "fatal");
                essential.CaptureEvent(
                    signal,
                    new Scope(_essentialOptions),
                    new SentryHint());
                if (!canContinue)
                {
                    essential.FlushAsync(TimeSpan.FromMilliseconds(750))
                        .GetAwaiter()
                        .GetResult();
                }
            }

            if (experience)
                CaptureExceptionCore(exception, stage);
        }
        catch
        {
            // Diagnostics must never recurse into the crash handler.
        }
    }

    private void CaptureEventCore(
        string eventName,
        IReadOnlyDictionary<string, string>? properties)
    {
        PostHogExperienceClient? postHog;
        lock (Gate)
        {
            if (!_experienceEnabled)
                return;
            postHog = _postHog;
        }

        try
        {
            IReadOnlyDictionary<string, string> safe = TelemetryDataPolicy.SanitizeProperties(properties);
            postHog?.Capture(eventName, safe);
            SentrySdk.AddBreadcrumb(
                TelemetryDataPolicy.NormalizeName(eventName),
                "product",
                "event",
                safe.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal),
                BreadcrumbLevel.Info);
        }
        catch
        {
            // ignore
        }
    }

    private void CaptureExceptionCore(Exception exception, string stage)
    {
        ArgumentNullException.ThrowIfNull(exception);
        lock (Gate)
        {
            if (!_experienceEnabled || _experienceSentry is null)
                return;
        }

        try
        {
            string safeStage = TelemetryDataPolicy.NormalizeName(stage);
            SentrySdk.CaptureException(exception, scope =>
            {
                scope.SetTag("failure_stage", safeStage);
                scope.SetTag("process_role", "desktop");
            });
        }
        catch
        {
            // ignore
        }
    }

    private TelemetryOperation StartOperationCore(string name, string operation)
    {
        lock (Gate)
        {
            if (!_experienceEnabled || _experienceSentry is null)
                return TelemetryOperation.NoOp;
        }

        try
        {
            ITransactionTracer transaction = SentrySdk.StartTransaction(
                TelemetryDataPolicy.NormalizeName(name),
                TelemetryDataPolicy.NormalizeName(operation));
            return new TelemetryOperation(transaction);
        }
        catch
        {
            return TelemetryOperation.NoOp;
        }
    }

    private async Task<bool?> IsFeatureEnabledCoreAsync(
        string flagKey,
        CancellationToken cancellationToken)
    {
        PostHogExperienceClient? postHog;
        lock (Gate)
        {
            if (!_experienceEnabled)
                return null;
            postHog = _postHog;
        }

        if (postHog is null)
            return null;
        try
        {
            return await postHog.IsFeatureEnabledAsync(
                    TelemetryDataPolicy.NormalizeName(flagKey),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static SentryEvent ScrubEssentialEvent(SentryEvent sentryEvent)
    {
        sentryEvent.User = null!;
        sentryEvent.Request = null!;
        sentryEvent.SentryExceptions = null!;
        foreach (string key in sentryEvent.Extra.Keys.ToArray())
            sentryEvent.SetExtra(key, "<removed>");
        return sentryEvent;
    }

    private static SentryEvent ScrubExperienceEvent(SentryEvent sentryEvent)
    {
        sentryEvent.User = null!;
        sentryEvent.Request = null!;
        foreach (string key in sentryEvent.Extra.Keys.ToArray())
            sentryEvent.SetExtra(key, "<removed>");
        if (sentryEvent.SentryExceptions is not null)
        {
            foreach (SentryException exception in sentryEvent.SentryExceptions)
            {
                exception.Value = TelemetryDataPolicy.RedactText(exception.Value);
                if (exception.Stacktrace?.Frames is null)
                    continue;
                foreach (SentryStackFrame frame in exception.Stacktrace.Frames)
                {
                    frame.AbsolutePath = null;
                    frame.FileName = string.IsNullOrWhiteSpace(frame.FileName)
                        ? null
                        : Path.GetFileName(frame.FileName);
                    frame.ContextLine = null;
                    frame.PreContext.Clear();
                    frame.PostContext.Clear();
                    frame.Vars.Clear();
                }
            }
        }
        return sentryEvent;
    }

    private static string ReadConfiguration(string primary, string fallback)
    {
        string? value = Environment.GetEnvironmentVariable(primary);
        return string.IsNullOrWhiteSpace(value)
            ? Environment.GetEnvironmentVariable(fallback) ?? string.Empty
            : value;
    }

    private static Uri EnsureTrailingSlash(Uri uri) =>
        uri.AbsoluteUri.Length > 0 && uri.AbsoluteUri[^1] == '/'
            ? uri
            : new Uri(uri.AbsoluteUri + "/", UriKind.Absolute);
}

internal sealed class TelemetryOperation : IDisposable
{
    internal static TelemetryOperation NoOp { get; } = new(null);

    private ITransactionTracer? _transaction;

    internal TelemetryOperation(ITransactionTracer? transaction)
    {
        _transaction = transaction;
    }

    public void Complete() => Finish(SpanStatus.Ok);

    public void Cancel() => Finish(SpanStatus.Cancelled);

    public void Fail(Exception exception)
    {
        ITransactionTracer? transaction = Interlocked.Exchange(ref _transaction, null);
        transaction?.Finish(exception, SpanStatus.InternalError);
    }

    public void Dispose() => Complete();

    private void Finish(SpanStatus status)
    {
        ITransactionTracer? transaction = Interlocked.Exchange(ref _transaction, null);
        transaction?.Finish(status);
    }
}
