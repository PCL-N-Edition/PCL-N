using System.Text;

namespace PCL.Services.Logging;

/// <summary>
/// One sink that mirrors recorded log entries outside the state ring. Sinks never influence
/// the operation being logged: implementations must catch their own IO failures.
/// </summary>
public interface ILogSink
{
    void Write(LogEntry entry, string formattedLine);
}

/// <summary>
/// Mirrors log entries to the process console. Active only when the process actually has a
/// console output stream (launched from a terminal or with redirected output); detached GUI
/// launches disable the sink instead of paying for writes nobody sees.
/// </summary>
public sealed class ConsoleLogSink : ILogSink
{
    private bool _disabled;

    public void Write(LogEntry entry, string formattedLine)
    {
        if (_disabled)
        {
            return;
        }

        try
        {
            Console.WriteLine(formattedLine);
        }
        catch (Exception)
        {
            // No console (detached GUI launch) or a broken stdout: stop mirroring instead of
            // touching the log path on every entry.
            _disabled = true;
        }
    }
}

/// <summary>
/// Appends log entries to one UTF-8 file, opening lazily and disabling itself when the file
/// cannot be written (locked disk, missing folder); logging must never break the app.
/// </summary>
public sealed class FileLogSink(string filePath) : ILogSink
{
    private readonly object _gate = new();
    private StreamWriter? _writer;
    private bool _disabled;

    public void Write(LogEntry entry, string formattedLine)
    {
        lock (_gate)
        {
            if (_disabled)
            {
                return;
            }

            try
            {
                StreamWriter? writer = _writer;
                if (writer is null)
                {
                    string? directory = Path.GetDirectoryName(filePath);
                    if (!string.IsNullOrEmpty(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    FileStream stream = new FileStream(
                        filePath,
                        FileMode.Append,
                        FileAccess.Write,
                        FileShare.Read);
                    writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                    _writer = writer;
                }

                writer.WriteLine(formattedLine);
                writer.Flush();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _disabled = true;
                try { _writer?.Dispose(); } catch { }
                _writer = null;
            }
        }
    }

    /// <summary>Flushes and closes the file; safe to call more than once.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_writer is not null)
            {
                try { _writer.Dispose(); } catch { }
                _writer = null;
            }

            _disabled = true;
        }
    }
}
