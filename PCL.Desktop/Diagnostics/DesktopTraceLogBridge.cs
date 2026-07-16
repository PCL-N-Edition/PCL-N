// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Diagnostics;
using System.Text;
using PCL.Core.Logging;

namespace PCL.Desktop.Diagnostics;

/// <summary>
/// Routes Avalonia and framework trace output into the launcher log pipeline.
/// </summary>
internal sealed class DesktopTraceLogBridge : TraceListener
{
    private static readonly object InstallLock = new();
    private static bool _installed;

    [ThreadStatic]
    private static StringBuilder? _lineBuffer;

    [ThreadStatic]
    private static bool _isWriting;

    public static void Install()
    {
        lock (InstallLock)
        {
            if (_installed)
                return;
            Trace.Listeners.Add(new DesktopTraceLogBridge());
            _installed = true;
        }
    }

    public override void Write(string? message)
    {
        if (string.IsNullOrEmpty(message))
            return;
        (_lineBuffer ??= new StringBuilder()).Append(message);
    }

    public override void WriteLine(string? message)
    {
        if (_isWriting)
            return;

        string line;
        if (_lineBuffer is { Length: > 0 } buffer)
        {
            buffer.Append(message);
            line = buffer.ToString();
            buffer.Clear();
        }
        else
        {
            line = message ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(line) || line.StartsWith("[PortableLog]", StringComparison.Ordinal))
            return;

        try
        {
            _isWriting = true;
            PortableLogLevel level = Classify(line);
            PortableLog.Write(new PortableLogEntry(level, "Framework", line));
        }
        finally
        {
            _isWriting = false;
        }
    }

    private static PortableLogLevel Classify(string message)
    {
        if (message.Contains("Fatal", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Error", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Exception", StringComparison.OrdinalIgnoreCase))
        {
            return PortableLogLevel.Error;
        }

        if (message.Contains("Warn", StringComparison.OrdinalIgnoreCase))
            return PortableLogLevel.Warn;
        if (message.Contains("Info", StringComparison.OrdinalIgnoreCase))
            return PortableLogLevel.Info;
        return PortableLogLevel.Debug;
    }
}
