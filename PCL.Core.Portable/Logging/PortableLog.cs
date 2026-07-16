// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Text.RegularExpressions;

namespace PCL.Core.Logging;

public enum PortableLogLevel
{
    Error,
    Warn,
    Info,
    Debug,
    RealTime
}

public readonly record struct PortableLogEntry(
    PortableLogLevel Level,
    string Module,
    string Message,
    Exception? Exception = null,
    DateTimeOffset Timestamp = default);

/// <summary>
/// Small logging bridge for portable code that must not depend on the WPF launcher lifecycle.
/// </summary>
public static partial class PortableLog
{
    private static int _maximumLevel = (int)PortableLogLevel.Info;

    public static event Action<PortableLogEntry>? Written;

    /// <summary>
    /// Gets or sets the most verbose level that is emitted. The default is <see cref="PortableLogLevel.Info"/>.
    /// </summary>
    public static PortableLogLevel MaximumLevel
    {
        get => (PortableLogLevel)Volatile.Read(ref _maximumLevel);
        set => Interlocked.Exchange(
            ref _maximumLevel,
            (int)(Enum.IsDefined(value) ? value : PortableLogLevel.Info));
    }

    public static bool IsEnabled(PortableLogLevel level) =>
        Enum.IsDefined(level) && (int)level <= Volatile.Read(ref _maximumLevel);

    /// <summary>
    /// Compatibility alias for old trace call sites. Trace is now the high-volume RealTime level.
    /// </summary>
    public static void Trace(string module, string message)
    {
        RealTime(module, message);
    }

    public static void RealTime(string module, string message)
    {
        Write(new PortableLogEntry(PortableLogLevel.RealTime, module, message));
    }

    public static void Debug(Exception exception, string module, string message)
    {
        Write(new PortableLogEntry(PortableLogLevel.Debug, module, message, exception));
    }

    public static void Debug(string module, string message)
    {
        Write(new PortableLogEntry(PortableLogLevel.Debug, module, message));
    }

    public static void Info(string module, string message)
    {
        Write(new PortableLogEntry(PortableLogLevel.Info, module, message));
    }

    public static void Warn(string module, string message)
    {
        Write(new PortableLogEntry(PortableLogLevel.Warn, module, message));
    }

    public static void Warn(Exception exception, string module, string message)
    {
        Write(new PortableLogEntry(PortableLogLevel.Warn, module, message, exception));
    }

    public static void Error(Exception exception, string module, string message)
    {
        Write(new PortableLogEntry(PortableLogLevel.Error, module, message, exception));
    }

    public static void Error(string module, string message)
    {
        Write(new PortableLogEntry(PortableLogLevel.Error, module, message));
    }

    public static void Write(PortableLogEntry entry)
    {
        if (!IsEnabled(entry.Level))
            return;

        if (entry.Timestamp == default)
            entry = entry with { Timestamp = DateTimeOffset.UtcNow };

        entry = entry with
        {
            Module = string.IsNullOrWhiteSpace(entry.Module) ? "General" : entry.Module.Trim(),
            Message = Redact(entry.Message)
        };

        Action<PortableLogEntry>? handlers = Written;
        if (handlers is null)
            return;

        foreach (Action<PortableLogEntry> handler in handlers.GetInvocationList().Cast<Action<PortableLogEntry>>())
        {
            try
            {
                handler(entry);
            }
            catch (Exception ex)
            {
                // A diagnostic sink must never break the operation being diagnosed.
                System.Diagnostics.Debug.WriteLine($"[PortableLog] Sink failed: {ex}");
            }
        }
    }

    /// <summary>
    /// Removes common credentials from diagnostic text before it reaches any sink.
    /// </summary>
    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        string result = AuthorizationHeaderPattern().Replace(text, "$1<redacted>");
        result = BearerPattern().Replace(result, "$1<redacted>");
        result = SecretAssignmentPattern().Replace(result, "$1$2<redacted>");
        result = SecretArgumentPattern().Replace(result, "$1$2<redacted>");
        result = SensitiveQueryPattern().Replace(result, "$1<redacted>");
        return result;
    }

    [GeneratedRegex("(?i)(\\bAuthorization\\s*:\\s*)(?:(?:Bearer|Basic)\\s+)?[^\\s,;]+", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex AuthorizationHeaderPattern();

    [GeneratedRegex("(?i)(\\bBearer\\s+)[^\\s,;]+", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex BearerPattern();

    [GeneratedRegex("(?i)(\\b(?:access[_-]?token|refresh[_-]?token|password|passwd|api[_-]?key|client[_-]?secret|secret|token)\\b)(\\s*(?:=|:)\\s*)[^\\s,;&]+", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex SecretAssignmentPattern();

    [GeneratedRegex("(?i)(\\b(?:access[_-]?token|refresh[_-]?token|password|passwd|api[_-]?key|client[_-]?secret|secret)\\b)(\\s+)[^\\s,;&]+", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex SecretArgumentPattern();

    [GeneratedRegex("(?i)([?&](?:code|token|access_token|refresh_token|api_key|signature|sig)=)[^&#\\s]+", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex SensitiveQueryPattern();
}
