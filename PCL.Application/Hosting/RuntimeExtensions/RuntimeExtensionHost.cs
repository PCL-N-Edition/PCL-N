using System.Collections.Concurrent;
using System.Security.Cryptography;
using PCL.Application.Accounts;
using PCL.Application.Downloads;
using PCL.Application.Launching;
using PCL.Application.Settings;
using PCL.Platform.Abstractions.Processes;
using PCL.Platform.Abstractions.Security;

namespace PCL.Application.Hosting.RuntimeExtensions;

internal sealed class RuntimeExtensionHost : IRuntimeExtensionHost
{
    public RuntimeExtensionHost(
        IHostSettingsPageGroupRegistry settingsPageGroups,
        IHostSettingsPageRegistry settingsPages,
        IHostWorkQueue? workQueue = null,
        IHostNotifications? notifications = null,
        IHostInstanceQuery? instances = null,
        IHostUiComposition? uiComposition = null,
        IHostDeveloperDiagnostics? developerDiagnostics = null,
        IHostDynamicNavigation? navigation = null,
        IHostRawUiAccess? rawUiAccess = null,
        IHostSecureStorage? secureStorage = null,
        IHostUriLauncher? uriLauncher = null,
        string? applicationDataDirectory = null,
        string? cacheDirectory = null,
        IGameSessionRegistry? gameSessions = null,
        IProcessService? processes = null,
        IHostClipboard? clipboard = null,
        IAccountProviderRegistry? accounts = null,
        IDownloadSourceRegistry? downloads = null,
        ILaunchPipelineBuilder? launching = null,
        IHostBackgroundTasks? backgroundTasks = null,
        IHostFileArtifactRegistry? fileArtifacts = null,
        IHostLocalization? localization = null,
        IHostWindowActivation? windowActivation = null,
        IHostFeedbackSubmissionService? feedbackSubmission = null)
    {
        SettingsPageGroups = settingsPageGroups ?? throw new ArgumentNullException(nameof(settingsPageGroups));
        SettingsPages = settingsPages ?? throw new ArgumentNullException(nameof(settingsPages));
        Localization = localization ?? SystemHostLocalization.Instance;
        WorkQueue = workQueue ?? ImmediateHostWorkQueue.Instance;
        Notifications = notifications ?? CapturingHostNotifications.Instance;
        DeveloperDiagnostics = developerDiagnostics ?? new InMemoryHostDeveloperDiagnostics();
        SecureStorage = secureStorage ?? InMemoryHostSecureStorage.Instance;
        UriLauncher = uriLauncher ?? UnavailableHostUriLauncher.Instance;
        WindowActivation = windowActivation ?? NullHostWindowActivation.Instance;
        FeedbackSubmission = feedbackSubmission ?? new HostFeedbackSubmissionRegistry();
        Processes = processes;
        Clipboard = clipboard;
        Accounts = accounts ?? new AccountProviderRegistry();
        Downloads = downloads ?? new DownloadSourceRegistry();
        Launching = launching ?? new LaunchPipelineBuilder();
        BackgroundTasks = backgroundTasks ?? NullHostBackgroundTasks.Instance;
        FileArtifacts = fileArtifacts ?? NullHostFileArtifactRegistry.Instance;
        ApplicationDataDirectory = applicationDataDirectory ?? Path.GetTempPath();
        CacheDirectory = cacheDirectory ?? Path.GetTempPath();
        Instances = instances;
        GameSessions = gameSessions ?? GameSessionRegistry.Shared;
        UiComposition = uiComposition;
        Navigation = navigation;
        RawUiAccess = rawUiAccess;
    }

    public IHostSettingsPageGroupRegistry SettingsPageGroups { get; }
    public IHostSettingsPageRegistry SettingsPages { get; }
    public IHostLocalization Localization { get; }
    public IHostWorkQueue WorkQueue { get; }
    public IHostNotifications Notifications { get; }
    public IHostDeveloperDiagnostics DeveloperDiagnostics { get; }
    public IHostSecureStorage SecureStorage { get; }
    public IHostUriLauncher UriLauncher { get; }
    public IHostWindowActivation WindowActivation { get; }
    public IHostFeedbackSubmissionService FeedbackSubmission { get; }
    public IProcessService? Processes { get; }
    public IHostClipboard? Clipboard { get; }
    public IAccountProviderRegistry Accounts { get; }
    public IDownloadSourceRegistry Downloads { get; }
    public ILaunchPipelineBuilder Launching { get; }
    public IHostBackgroundTasks BackgroundTasks { get; }
    public IHostFileArtifactRegistry FileArtifacts { get; }
    public string ApplicationDataDirectory { get; }
    public string CacheDirectory { get; }
    public IHostInstanceQuery? Instances { get; }
    public IGameSessionRegistry GameSessions { get; }
    public IHostUiComposition? UiComposition { get; }
    public IHostDynamicNavigation? Navigation { get; }
    public IHostRawUiAccess? RawUiAccess { get; }
}

internal sealed class SystemHostLocalization : IHostLocalization
{
    public static SystemHostLocalization Instance { get; } = new();
    public string CurrentCulture => System.Globalization.CultureInfo.CurrentUICulture.Name;
    public string CurrentFormatCulture => System.Globalization.CultureInfo.CurrentCulture.Name;
    public event EventHandler? LanguageChanged { add { } remove { } }
}

internal sealed class NullHostFileArtifactRegistry : IHostFileArtifactRegistry
{
    public static NullHostFileArtifactRegistry Instance { get; } = new();

    public IDisposable Register(IHostFileArtifactHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return NullRegistration.Instance;
    }

    public ValueTask<HostFileArtifactResult> InstallAsync(
        string filePath,
        HostFileArtifactContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new NotSupportedException($"当前 Host 无法处理文件：{Path.GetFileName(filePath)}");
    }

    private sealed class NullRegistration : IDisposable
    {
        public static NullRegistration Instance { get; } = new();
        public void Dispose() { }
    }
}

internal sealed class NullHostBackgroundTasks : IHostBackgroundTasks
{
    public static NullHostBackgroundTasks Instance { get; } = new();
    public IHostBackgroundTask Begin(string title, bool openTaskManager = true) => NullHostBackgroundTask.Instance;
}

internal sealed class NullHostBackgroundTask : IHostBackgroundTask
{
    public static NullHostBackgroundTask Instance { get; } = new();
    public CancellationToken Token => CancellationToken.None;
    public void Report(HostBackgroundTaskProgress progress) { }
    public void Complete(string stage) { }
    public void Fail(string message, bool canceled = false) { }
    public void Dispose() { }
}

internal sealed class InMemoryHostDeveloperDiagnostics : IHostDeveloperDiagnostics
{
    public bool IsEnabled { get; private set; }
    public void SetEnabled(bool enabled) => IsEnabled = enabled;
}

internal sealed class InMemoryHostSecureStorage : IHostSecureStorage
{
    public static InMemoryHostSecureStorage Instance { get; } = new();
    private readonly ConcurrentDictionary<string, byte[]> _values = new(StringComparer.Ordinal);

    public ValueTask<SecureStorageReadResult> ReadAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_values.TryGetValue(key, out byte[]? value)
            ? new SecureStorageReadResult(SecureStorageStatus.Success, value.ToArray())
            : new SecureStorageReadResult(SecureStorageStatus.NotFound));
    }

    public ValueTask<SecureStorageOperationResult> WriteAsync(string key, ReadOnlyMemory<byte> value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _values.AddOrUpdate(key, value.ToArray(), (_, previous) =>
        {
            CryptographicOperations.ZeroMemory(previous);
            return value.ToArray();
        });
        return ValueTask.FromResult(new SecureStorageOperationResult(SecureStorageStatus.Success));
    }

    public ValueTask<SecureStorageOperationResult> DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_values.TryRemove(key, out byte[]? value))
            CryptographicOperations.ZeroMemory(value);
        return ValueTask.FromResult(new SecureStorageOperationResult(SecureStorageStatus.Success));
    }

    public ValueTask<SecureStorageReadResult> UnprotectLegacyWindowsAsync(
        ReadOnlyMemory<byte> encrypted,
        ReadOnlyMemory<byte> entropy,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new SecureStorageReadResult(SecureStorageStatus.Unavailable));
    }
}

internal sealed class UnavailableHostUriLauncher : IHostUriLauncher
{
    public static UnavailableHostUriLauncher Instance { get; } = new();
    public ValueTask<bool> OpenAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(false);
    }
}

internal sealed class NullHostWindowActivation : IHostWindowActivation
{
    public static NullHostWindowActivation Instance { get; } = new();

    public ValueTask ActivateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }
}

internal sealed class HostFeedbackSubmissionRegistry : IHostFeedbackSubmissionService
{
    private readonly object _sync = new();
    private IHostFeedbackSubmissionHandler? _handler;

    public bool IsAvailable
    {
        get
        {
            lock (_sync)
                return _handler is not null;
        }
    }

    public IDisposable Register(IHostFeedbackSubmissionHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_sync)
        {
            if (_handler is not null)
                throw new InvalidOperationException("A launcher feedback submission handler is already registered.");
            _handler = handler;
        }
        return new Registration(this, handler);
    }

    public Task<HostFeedbackSubmissionResult> SubmitAsync(
        HostFeedbackDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        IHostFeedbackSubmissionHandler? handler;
        lock (_sync)
            handler = _handler;
        return handler is null
            ? Task.FromException<HostFeedbackSubmissionResult>(
                new NotSupportedException("当前构建未加载 PCL.Plugin，无法在启动器内提交反馈。"))
            : handler.SubmitAsync(draft, cancellationToken);
    }

    private sealed class Registration(
        HostFeedbackSubmissionRegistry owner,
        IHostFeedbackSubmissionHandler handler) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            lock (owner._sync)
            {
                if (ReferenceEquals(owner._handler, handler))
                    owner._handler = null;
            }
            _disposed = true;
        }
    }
}

internal sealed class ImmediateHostWorkQueue : IHostWorkQueue
{
    public static ImmediateHostWorkQueue Instance { get; } = new();
    public void Post(Action action) { ArgumentNullException.ThrowIfNull(action); action(); }
    public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        cancellationToken.ThrowIfCancellationRequested();
        action();
        return Task.CompletedTask;
    }
    public Task<T> InvokeAsync<T>(Func<T> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(action());
    }
}

internal sealed class CapturingHostNotifications : IHostNotifications
{
    public static CapturingHostNotifications Instance { get; } = new();
    private readonly ConcurrentQueue<string> _messages = new();
    public IReadOnlyCollection<string> Messages => _messages.ToArray();
    public void ShowInformation(string message) => _messages.Enqueue("[info] " + (message ?? string.Empty));
    public void ShowWarning(string message) => _messages.Enqueue("[warn] " + (message ?? string.Empty));
    public Task<bool> ConfirmAsync(
        string title,
        string message,
        string primaryButton = "允许",
        string secondaryButton = "拒绝",
        bool isWarn = true,
        CancellationToken cancellationToken = default)
    {
        _messages.Enqueue($"[confirm] {title}: {message}");
        return Task.FromResult(false);
    }
}
