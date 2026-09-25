using System.Diagnostics;
using System.Text;

namespace Nexa.Services.Processes;

/// <summary>Owns an installer subprocess through a pipe lease, including abrupt launcher termination.</summary>
public static class OwnedInstallerProcess
{
    public const string WorkerArgument = "--nexa-install-worker";
    private const int Magic = 0x4e495731;
    private const int MaximumTextBytes = 32768;

    internal static ProcessStartInfo WorkerStartInfo()
    {
        string executable = Environment.ProcessPath ?? throw new IOException("无法定位安装器宿主。");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            string assembly = Environment.GetCommandLineArgs()[0];
            if (!Path.IsPathFullyQualified(assembly) || !assembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                throw new IOException("无法定位托管安装器宿主。");
            start.ArgumentList.Add(assembly);
        }
        start.ArgumentList.Add(WorkerArgument);
        return start;
    }

    internal static async Task WriteRequestAsync(Stream stream, ProcessStartInfo start, CancellationToken token = default)
    {
        if (start.ArgumentList.Count > 128) throw new InvalidDataException("安装器参数过多。");
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true);
        writer.Write(Magic);
        WriteText(writer, start.FileName); WriteText(writer, start.WorkingDirectory);
        writer.Write(start.ArgumentList.Count);
        foreach (string argument in start.ArgumentList) WriteText(writer, argument);
        writer.Flush();
        byte[] header = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header, checked((int)buffer.Length));
        await stream.WriteAsync(header, token).ConfigureAwait(false);
        await stream.WriteAsync(buffer.ToArray(), token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    private static void WriteText(BinaryWriter writer, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > MaximumTextBytes) throw new InvalidDataException("安装器参数过长。");
        writer.Write(bytes.Length); writer.Write(bytes);
    }

    private static string ReadText(BinaryReader reader)
    {
        int length = reader.ReadInt32();
        if (length is < 0 or > MaximumTextBytes) throw new InvalidDataException("Invalid installer argument length.");
        byte[] bytes = reader.ReadBytes(length);
        if (bytes.Length != length) throw new EndOfStreamException();
        return new UTF8Encoding(false, true).GetString(bytes);
    }

    public static async Task<int> RunWorkerAsync()
    {
        try
        {
            using Stream lease = Console.OpenStandardInput();
            byte[] header = new byte[4];
            await lease.ReadExactlyAsync(header).ConfigureAwait(false);
            int frameLength = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(header);
            if (frameLength is < 4 or > 5 * 1024 * 1024) return 2;
            byte[] frame = new byte[frameLength];
            await lease.ReadExactlyAsync(frame).ConfigureAwait(false);
            using var buffer = new MemoryStream(frame, writable: false);
            using var reader = new BinaryReader(buffer, Encoding.UTF8, leaveOpen: true);
            if (reader.ReadInt32() != Magic) return 2;
            string executable = ReadText(reader), directory = ReadText(reader);
            if (!Path.IsPathFullyQualified(executable) || !Path.IsPathFullyQualified(directory)) return 2;
            int count = reader.ReadInt32();
            if (count is < 0 or > 128) return 2;
            var start = new ProcessStartInfo(executable)
            {
                WorkingDirectory = directory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            for (int index = 0; index < count; index++) start.ArgumentList.Add(ReadText(reader));
            if (buffer.Position != buffer.Length) return 2;
            using var process = new Process { StartInfo = start };
            if (!process.Start()) return 2;
            process.StandardInput.Close();
            using var drainLifetime = new CancellationTokenSource();
            var drains = Task.WhenAll(
                process.StandardOutput.BaseStream.CopyToAsync(Console.OpenStandardOutput(), drainLifetime.Token),
                process.StandardError.BaseStream.CopyToAsync(Console.OpenStandardError(), drainLifetime.Token));
            Task exit = process.WaitForExitAsync();
            Task ownerLost = lease.ReadAsync(new byte[1]).AsTask();
            await Task.WhenAny(exit, ownerLost).ConfigureAwait(false);
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await exit.ConfigureAwait(false);
            try { await drains.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (TimeoutException) { await drainLifetime.CancelAsync().ConfigureAwait(false); }
            try { await drains.ConfigureAwait(false); } catch (OperationCanceledException) { }
            return process.ExitCode;
        }
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
        { return 2; }
    }
}
