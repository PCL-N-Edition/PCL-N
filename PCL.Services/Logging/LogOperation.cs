using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace PCL.Services.Logging;

/// <summary>
/// Explicit, low-volume breadcrumbs for one operation. The owner supplies safe identifiers,
/// never request objects, credentials or localized UI messages. No ambient/global context is used.
/// </summary>
public sealed class LogOperation : IDisposable
{
    private readonly LogService _log;
    private readonly object _gate = new();
    private readonly string _module;
    private readonly string _name;
    private readonly LogLevel _level;
    private readonly string? _context;
    private string? _stageContext;
    private string _source;
    private readonly long _startedAt = Stopwatch.GetTimestamp();
    private string _stage = "begin";
    private bool _finished;

    internal LogOperation(LogService log, string module, string name, string? context, string source, LogLevel level)
    {
        _log = log;
        _module = module;
        _name = name;
        _level = level;
        _context = context;
        _source = source;
        Id = Guid.NewGuid().ToString("N");
        Write(_level, "started");
    }

    public string Id { get; }

    public void Stage(string stage, string? context = null,
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        lock (_gate)
        {
            if (_finished) return;
            _stage = stage;
            _stageContext = context;
            _source = $"{Path.GetFileName(file)}:{line}";
            Write(_level, "entered");
        }
    }

    public void Complete(string? context = null) => Finish(_level, $"completed {context}");

    public void Reject(string code) => Finish(LogLevel.Warn, $"rejected code={code}");

    public void Cancel() => Finish(_level, "cancelled");

    public void Fail(Exception exception) => Finish(LogLevel.Error, "failed", ExceptionDiagnostics.Describe(exception));

    public void Dispose() => Finish(LogLevel.Warn, "ended without a terminal outcome");

    private void Finish(LogLevel level, string outcome, string? exceptionText = null)
    {
        lock (_gate)
        {
            if (_finished) return;
            _finished = true;
            Write(level, outcome, exceptionText);
        }
    }

    private void Write(LogLevel level, string detail, string? exceptionText = null) =>
        _log.Write(level, _module, string.Create(CultureInfo.InvariantCulture,
            $"{_name} op={Id} stage={_stage} {detail} {_context} {_stageContext} source={_source} elapsed_ms={Stopwatch.GetElapsedTime(_startedAt).TotalMilliseconds:F1}"), exceptionText);
}

/// <summary>
/// Locale-independent exception facts. Raw messages, Data and HTTP bodies can contain secrets
/// or translated UI text; diagnostic records deliberately keep only type/status and stack.
/// </summary>
public static class ExceptionDiagnostics
{
    public static string Describe(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        List<string> lines = [];
        for (Exception? current = exception; current is not null && lines.Count < 16; current = current.InnerException)
        {
            string status = current is HttpRequestException { StatusCode: { } code }
                ? $" http_status={(int)code}" : string.Empty;
            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"exception={current.GetType().FullName} hresult=0x{current.HResult:X8}{status}"));
            if (!string.IsNullOrWhiteSpace(current.StackTrace)) lines.Add(current.StackTrace);
        }
        return string.Join(Environment.NewLine, lines);
    }
}
