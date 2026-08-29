using System.Globalization;

namespace PCL.Services.Logging;

/// <summary>
/// Severity of one log entry, ordered from most to least severe so a maximum-level gate can be
/// a single integer comparison, mirroring the legacy level ordering.
/// </summary>
public enum LogLevel
{
    Error = 0,
    Warn = 1,
    Info = 2,
    Debug = 3,

    /// <summary>
    /// The high-volume trace level retained for compatibility with old trace call sites.
    /// </summary>
    RealTime = 4,
}

/// <summary>
/// One immutable log entry. <see cref="Sequence"/> is monotonic per <see cref="LogService"/>
/// and keys the ordered state collection; <see cref="Message"/> and
/// <see cref="ExceptionText"/> are already redacted when stored.
/// </summary>
public readonly record struct LogEntry(
    long Sequence,
    DateTimeOffset Timestamp,
    LogLevel Level,
    string Module,
    string Message,
    string? ExceptionText)
{
    public string ToDisplayText()
    {
        string line = string.Create(
            CultureInfo.InvariantCulture,
            $"[{Timestamp.ToLocalTime():HH:mm:ss.fff}] [{Level}] [{Module}] {Message}");
        return string.IsNullOrWhiteSpace(ExceptionText)
            ? line
            : $"{line}{Environment.NewLine}{ExceptionText}";
    }
}
