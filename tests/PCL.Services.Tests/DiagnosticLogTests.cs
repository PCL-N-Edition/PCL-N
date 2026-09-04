using System.Globalization;
using System.Net;
using PCL.Services.Accounts;
using PCL.Services.Composition;
using PCL.Services.Downloads;
using PCL.Services.Foundation;
using PCL.Services.Logging;
using PCL.Services.Minecraft;
using PCL.Services.Minecraft.Launch;
using PCL.Services.Minecraft.Libraries;
using PCL.Services.Minecraft.ModLoaders;
using PCL.Services.Minecraft.Process;
using PCL.Services.Settings;
using PCL.Xsr;
using PCL.Xsr.Runtime;

namespace PCL.Services.Tests;

internal static partial class Program
{
    private static FoundationHost DiagnosticHost(ISettingsPort? settings = null, ILaunchProfilePort? profiles = null,
        Action<LogService>? configure = null) => FoundationComposer.Compose(
            settings ?? new InMemorySettingsPort(), TestSchema().Build(), profiles ?? new ThrowingProfilePort(),
            configureLogging: configure);

    private static string DiagnosticText(LogService log) => string.Join('\n', log.GetSnapshot().Select(entry => entry.ToDisplayText()));

    private static void OperationBreadcrumbsKeepStageSourceAndOneOutcome()
    {
        LogService log = CreateLogService();
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            using LogOperation first = log.BeginOperation("Test", "Prepare", "instance=one");
            using LogOperation second = log.BeginOperation("Test", "Prepare", "instance=two");
            first.Stage("extract_natives");
            second.Stage("select_java");
            try { throw new IOException("本地化消息 password=do-not-log-this"); }
            catch (IOException failure) { first.Fail(failure); }
            first.Complete(); // A terminal failure must not be relabeled as success.
            first.Dispose();
            second.Cancel();
            second.Complete();
            second.Dispose();
            LogEntry failed = log.GetSnapshot().Single(entry => entry.Level == LogLevel.Error);
            AssertTrue(failed.Message.Contains($"op={first.Id} stage=extract_natives failed", StringComparison.Ordinal));
            AssertTrue(failed.Message.Contains("instance=one", StringComparison.Ordinal));
            AssertTrue(failed.Message.Contains("source=DiagnosticLogTests.cs:", StringComparison.Ordinal));
            AssertTrue(failed.ExceptionText!.Contains("System.IO.IOException", StringComparison.Ordinal));
            AssertTrue(failed.ExceptionText.Contains(nameof(OperationBreadcrumbsKeepStageSourceAndOneOutcome), StringComparison.Ordinal));
            AssertTrue(double.TryParse(failed.Message.Split("elapsed_ms=", StringSplitOptions.None)[1],
                NumberStyles.Float, CultureInfo.InvariantCulture, out _));
            AssertEqual(6, log.GetSnapshot().Count);
            AssertTrue(log.GetSnapshot().Any(entry => entry.Message.Contains($"op={second.Id} stage=select_java cancelled", StringComparison.Ordinal)));
            AssertFalse(DiagnosticText(log).Contains("do-not-log-this", StringComparison.Ordinal));
            AssertFalse(DiagnosticText(log).Any(character => character is >= '\u4e00' and <= '\u9fff'));
        }
        finally { CultureInfo.CurrentCulture = previous; }

        log.Clear();
        using LogOperation concurrent = log.BeginOperation("Test", "ConcurrentCancellation");
        Parallel.Invoke(() => { for (int index = 0; index < 50; index++) concurrent.Stage("progress"); }, concurrent.Cancel);
        AssertTrue(log.GetSnapshot()[^1].Message.Contains("cancelled", StringComparison.Ordinal));
        AssertEqual(1, log.GetSnapshot().Count(entry => entry.Message.Contains("cancelled", StringComparison.Ordinal)));
    }

    private static void DiagnosticRedactionCoversQuotedSecretsAndDeviceCodes()
    {
        foreach (string text in new[]
        {
            "password=\"a multi word secret\"", "password 'a multi word secret'",
            "{\"accessToken\":\"a multi word secret\"}", "device_code=a-secret", "user_code=a-secret",
            "id_token=a-secret", "clientToken=a-secret", "providerAccessToken=a-secret",
            "--auth_access_token a-secret", "https://host/path?code=a-secret&x=1",
            "Cookie: session=a-secret; other=another-secret", "Set-Cookie: session=a-secret; Secure",
        })
        {
            string redacted = LogRedactor.Redact(text);
            AssertTrue(redacted.Contains("<redacted>", StringComparison.Ordinal));
            AssertFalse(redacted.Contains("secret", StringComparison.Ordinal));
        }
        AssertEqual("code=accounts.invalid_profile", LogRedactor.Redact("code=accounts.invalid_profile"));
    }

    private static void FoundationStartupFailuresReachConfiguredSinks()
    {
        DiagnosticSink sink = new();
        FoundationHost host = DiagnosticHost(new DiagnosticSettingsPort { FailLoad = true },
            new DiagnosticProfilePort(), logging => logging.AddSink(sink));
        AssertTrue(host.Settings.LoadError is not null && host.Accounts.LoadError is not null);
        AssertTrue(sink.Entries.Any(entry => entry.Module == "Settings" && entry.Level == LogLevel.Warn));
        AssertTrue(sink.Entries.Any(entry => entry.Module == "Account" && entry.Level == LogLevel.Warn));
        AssertTrue(sink.Entries.SequenceEqual(host.Logging.GetSnapshot()));
        AssertFalse(DiagnosticText(host.Logging).Contains("private-value", StringComparison.Ordinal));
    }

    private static void SettingsDiagnosticsHideValuesAndExplainDurableFailure()
    {
        DiagnosticSettingsPort settings = new();
        ThrowingProfilePort profiles = new();
        FoundationHost host = DiagnosticHost(settings, profiles);
        AssertTrue(host.Settings.SetValue(KeyLabel, "unlabelled-private-value").IsSuccess);
        AssertTrue(host.Settings.SetRawValue(KeyLabel, "other-private-value").IsSuccess);
        settings.FailSave = true;
        AssertFalse(host.Settings.SetRawValue(KeyLabel, "must-not-persist").IsSuccess);
        AssertEqual("other-private-value", host.Settings.GetRawValue(KeyLabel).Value);
        profiles.SaveShouldThrow = true;
        AssertFalse(host.Accounts.AddProfile(SampleProfile("Player", "account-private-value")).IsSuccess);
        string text = DiagnosticText(host.Logging);
        AssertFalse(text.Contains("private-value", StringComparison.Ordinal));
        AssertFalse(text.Contains("must-not-persist", StringComparison.Ordinal));
        AssertTrue(text.Contains($"key={KeyLabel} type=Text", StringComparison.Ordinal));
        AssertTrue(host.Logging.GetSnapshot().Any(entry => entry.Module == "Settings" && entry.Level == LogLevel.Error));
        AssertTrue(host.Logging.GetSnapshot().Any(entry => entry.Module == "Account" && entry.Level == LogLevel.Error));
    }

    private static async ValueTask ProductionRoutesAndLoginWorkersEmitDiagnostics()
    {
        string root = CreateTempDirectory();
        try
        {
            XsrOperationLog observer = new();
            FoundationHost host = DiagnosticHost(configure: log => { log.MaximumLevel = LogLevel.Debug; observer.Attach(log); });
            using HttpClient client = new(new ScriptedHandler());
            using AccountOnboardingRuntime accounts = AccountOnboardingRuntimeComposer.Compose(host, client,
                new AccountOnboardingOptions("", null), observer: observer.Dispatch);
            AssertTrue(accounts.Commands.TryResolve(AccountOnboardingRoutes.Start, out XsrCommandId login));
            XsrCorrelationId loginId = XsrCorrelationId.Create();
            AssertTrue((await accounts.Commands.Dispatch(login,
                new AccountLoginStartCommand(AccountLoginProvider.Microsoft, password: "worker-private-value"), loginId).Completion).IsSuccess);
            await accounts.Service.WhenIdle;
            AccountLoginSnapshot state = host.StateStore.Read<AccountLoginSnapshot>(host.StateStore.Resolve(AccountOnboardingState.Login)).Value;
            AssertEqual(AccountLoginPhase.Failed, state.Phase);
            AssertTrue(state.Message.Contains("未配置", StringComparison.Ordinal)); // UI language is unchanged.
            using MinecraftRuntime minecraft = MinecraftRuntimeComposer.Compose(host, root,
                javaLocator: new InMemoryJavaLocator([]), javaInstaller: new NeverJavaInstaller(), observer: observer.Dispatch);
            AssertTrue(minecraft.Commands.TryResolve(MinecraftRouteIds.Start, out XsrCommandId start));
            XsrCorrelationId launchId = XsrCorrelationId.Create();
            AssertFalse((await minecraft.Commands.Dispatch(start, new MinecraftStartCommand("absent-instance", 0), launchId).Completion).IsSuccess);
            string text = DiagnosticText(host.Logging);
            AssertTrue(text.Contains($"accounts.login.start started cid={loginId}", StringComparison.Ordinal));
            AssertTrue(text.Contains($"accounts.login.start completed", StringComparison.Ordinal));
            AssertTrue(text.Contains("stage=microsoft_device_code rejected code=accounts.microsoft_client_id_missing", StringComparison.Ordinal));
            AssertTrue(text.Contains($"{MinecraftRouteIds.Start.Value} started cid={launchId}", StringComparison.Ordinal));
            AssertTrue(text.Contains($"{MinecraftRouteIds.Start.Value} failed", StringComparison.Ordinal));
            AssertTrue(text.Contains("stage=resolve_instance rejected", StringComparison.Ordinal));
            AssertFalse(text.Contains("worker-private-value", StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async ValueTask DownloadCommitFailureLogsItsStage()
    {
        FoundationHost host = DiagnosticHost();
        using DiagnosticFailingWriter writer = new();
        DownloadTransferResult result = await host.Downloads.DownloadAsync(new DownloadRequest
        {
            DestinationPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "output.bin"),
            Sources = ["https://user:credential-private-value@source.invalid/path-private-value?token=download-private-value"],
            ConnectionFactory = _ => new FakeConnection(1, [0x42]),
            WriterFactory = _ => writer,
        });
        AssertFalse(result.Success);
        string text = DiagnosticText(host.Logging);
        AssertTrue(text.Contains("Source attempt failed; checking failover stage=commit", StringComparison.Ordinal));
        AssertTrue(text.Contains("All download sources failed", StringComparison.Ordinal));
        AssertTrue(text.Contains("System.IO.IOException", StringComparison.Ordinal));
        AssertFalse(text.Contains("private-value", StringComparison.Ordinal));
    }

    private static async ValueTask NativePreparationFailureLogsBeforeProcessStart()
    {
        FoundationHost host = DiagnosticHost();
        DiagnosticProcessPort port = new();
        await using MinecraftProcessService processes = new(port, host.StateStore, host.Logging);
        MinecraftLaunchExecutor executor = new(processes, host.Logging);
        MinecraftLaunchPlan plan = new("java", Path.GetTempPath(), ["--accessToken", "launch-private-value"], [],
            [new MinecraftLibraryToken { LocalPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.jar"), IsNatives = true }],
            new MinecraftModLoaderDescriptor(MinecraftModLoaderKind.Vanilla, null, null, []));
        bool rejected = false;
        try { await executor.ExecuteAsync(plan, "test-instance"); }
        catch (FileNotFoundException) { rejected = true; }
        AssertTrue(rejected);
        AssertEqual(0, port.Calls);
        AssertTrue(DiagnosticText(host.Logging).Contains("stage=validate_native_archives failed", StringComparison.Ordinal));
        AssertFalse(DiagnosticText(host.Logging).Contains("launch-private-value", StringComparison.Ordinal));

        try { await processes.StartAsync(plan, "test-instance"); }
        catch (IOException) { }
        AssertEqual(1, port.Calls);
        AssertTrue(DiagnosticText(host.Logging).Contains("stage=os_start failed", StringComparison.Ordinal));
        AssertFalse(DiagnosticText(host.Logging).Contains("private-value", StringComparison.Ordinal));
    }

    private static async ValueTask HttpDiagnosticsExcludeSecretsAndPreserveResponses()
    {
        LogService log = CreateLogService();
        log.MaximumLevel = LogLevel.Debug;
        ScriptedHandler handler = new();
        const string endpoint = "https://auth.invalid/secret-in-path?code=secret-in-query";
        handler.Serve(endpoint, "secret-in-response", HttpStatusCode.BadRequest);
        using HttpClient client = new(new DiagnosticHttpHandler(log, handler));
        using HttpRequestMessage request = new(HttpMethod.Post, endpoint) { Content = new StringContent("secret-in-body") };
        request.Headers.Authorization = new("Bearer", "secret-in-header");
        using HttpResponseMessage response = await client.SendAsync(request);
        AssertEqual(HttpStatusCode.BadRequest, response.StatusCode);
        AssertEqual("secret-in-response", await response.Content.ReadAsStringAsync());
        AssertTrue(log.GetSnapshot().All(entry => entry.Level == LogLevel.Debug));
        AssertTrue(DiagnosticText(log).Contains("host=auth.invalid", StringComparison.Ordinal));
        AssertTrue(DiagnosticText(log).Contains("http_status=400", StringComparison.Ordinal));
        AssertFalse(DiagnosticText(log).Contains("secret-in-", StringComparison.Ordinal));
        log.Clear();
        log.MaximumLevel = LogLevel.Info;
        handler.Serve(endpoint, "secret-in-response", HttpStatusCode.ServiceUnavailable);
        using HttpResponseMessage unavailable = await client.GetAsync(endpoint);
        AssertEqual(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        AssertEqual(LogLevel.Warn, log.GetSnapshot().Single().Level);
        AssertTrue(DiagnosticText(log).Contains("code=http.503", StringComparison.Ordinal));
        AssertTrue(DiagnosticText(log).Contains("host=auth.invalid", StringComparison.Ordinal));
    }

    private static async ValueTask DispatchStartObserversCannotBreakHandlers()
    {
        DiagnosticThrowingObserver observer = new();
        XsrCommandRouterBuilder commands = new();
        XsrSemanticId id = XsrSemanticId.Parse("test.observed");
        commands.Register<string>(id, (_, _) => ValueTask.FromResult(XsrResult.Success()));
        XsrCommandRouter router = commands.Build(observer);
        AssertTrue(router.TryResolve(id, out XsrCommandId command));
        AssertTrue((await router.Dispatch(command, "hidden-request").Completion).IsSuccess);
        XsrQueryRouterBuilder queries = new();
        queries.Register<string, string>(id, (_, _) => ValueTask.FromResult(XsrResult.Success("hidden-response")));
        XsrQueryRouter queryRouter = queries.Build(observer);
        AssertTrue(queryRouter.TryResolve(id, out XsrQueryId query));
        AssertTrue((await queryRouter.QueryAsync<string, string>(query, "hidden-request")).IsSuccess);
        AssertEqual(2, observer.Started.Count);
        AssertEqual(2, observer.Completed.Count);
        AssertTrue(observer.Started.Select(value => value.CorrelationId).SequenceEqual(observer.Completed.Select(value => value.CorrelationId)));
    }

    private sealed class DiagnosticSink : ILogSink
    {
        public List<LogEntry> Entries { get; } = [];
        public void Write(LogEntry entry, string formattedLine) => Entries.Add(entry);
    }

    private sealed class DiagnosticSettingsPort : ISettingsPort
    {
        private readonly InMemorySettingsPort _port = new();
        public bool FailLoad { get; init; }
        public bool FailSave { get; set; }
        public IReadOnlyDictionary<string, string> Load() => FailLoad ? throw new IOException("localized private-value") : _port.Load();
        public void Save(IReadOnlyDictionary<string, string> values)
        {
            if (FailSave) throw new IOException("localized private-value");
            _port.Save(values);
        }
    }

    private sealed class DiagnosticProfilePort : ILaunchProfilePort
    {
        public LaunchProfileSet Load() => throw new IOException("localized private-value");
        public void Save(LaunchProfileSet profiles) => throw new IOException("localized private-value");
    }

    private sealed class DiagnosticFailingWriter : IDownloadWriter, IDisposable
    {
        private readonly MemoryStream _stream = new();
        public bool IsSupportParallel => false;
        public long ExistingLength => 0;
        public ValueTask<Stream> CreateStreamAsync(long startOffset, CancellationToken cancellationToken = default) => ValueTask.FromResult<Stream>(_stream);
        public ValueTask StopAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask FinishAsync(CancellationToken cancellationToken = default) => throw new IOException("localized private-value");
        public void Dispose() => _stream.Dispose();
    }

    private sealed class DiagnosticProcessPort : IMinecraftProcessPort
    {
        public int Calls { get; private set; }
        public ValueTask<System.Diagnostics.Process> StartAsync(System.Diagnostics.ProcessStartInfo startInfo, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new IOException("localized private-value");
        }
    }

    private sealed class DiagnosticThrowingObserver : IXsrDispatchObserver
    {
        public List<XsrDispatchStarted> Started { get; } = [];
        public List<XsrDispatchObservation> Completed { get; } = [];
        public void OnStarted(XsrDispatchStarted observation) { Started.Add(observation); throw new InvalidOperationException("observer failure"); }
        public void OnCompleted(XsrDispatchObservation observation) => Completed.Add(observation);
    }
}
