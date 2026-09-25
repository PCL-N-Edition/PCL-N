using System.Buffers.Binary;
using System.Text;
using Nexa.Services.Minecraft.Launch;

namespace Nexa.Services.Minecraft.Process;

/// <summary>One-time private-pipe bootstrap. Never persist or log the encoded payload.</summary>
public static class JvmHostBootstrap
{
    private const int Magic = 0x4E4A564D;
    private const int Version = 1;
    private const int MaxFrameBytes = 4 * 1024 * 1024;
    private const int MaxStringBytes = 1024 * 1024;
    private const int MaxArguments = 16384;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static async ValueTask WriteAsync(Stream destination, MinecraftLaunchPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.MainClassIndex is not { } boundary || boundary < 0 || boundary >= plan.Arguments.Count)
            throw new ArgumentException("An explicit main-class boundary is required.", nameof(plan));
        if (boundary > MaxArguments || plan.Arguments.Count - boundary - 1 > MaxArguments)
            throw new InvalidDataException("Too many host arguments.");
        string[] jvm = plan.Arguments.Take(boundary).ToArray();
        string[] game = plan.Arguments.Skip(boundary + 1).ToArray();
        string[] fields = [plan.JavaExecutablePath, plan.WorkingDirectory, plan.Arguments[boundary]];
        if (fields.Any(string.IsNullOrWhiteSpace)) throw new InvalidDataException("Missing host bootstrap identity.");
        int size = 16; // magic, version, two vector counts
        foreach (string value in fields.Concat(jvm).Concat(game))
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = Utf8.GetByteCount(value);
            if (count > MaxStringBytes || value.Contains('\0')) throw new InvalidDataException("Invalid host string.");
            if (size > MaxFrameBytes - count - 4) throw new InvalidDataException("Host bootstrap exceeds budget.");
            size += count + 4;
        }
        byte[] frame = new byte[size + 4];
        try
        {
            using MemoryStream buffer = new(frame, writable: true);
            using BinaryWriter writer = new(buffer, Utf8, leaveOpen: true);
            writer.Write(size);
            writer.Write(Magic);
            writer.Write(Version);
            foreach (string field in fields) WriteString(writer, field);
            WriteArguments(writer, jvm);
            WriteArguments(writer, game);
            await destination.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { Array.Clear(frame); }
    }

    public static async ValueTask<JvmHostBootstrapRequest> ReadAsync(Stream source,
        CancellationToken cancellationToken = default)
    {
        byte[] prefix = new byte[4];
        await source.ReadExactlyAsync(prefix, cancellationToken).ConfigureAwait(false);
        int size = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (size < 28 || size > MaxFrameBytes) throw new InvalidDataException("Invalid host bootstrap size.");
        byte[] frame = new byte[size];
        try
        {
            await source.ReadExactlyAsync(frame, cancellationToken).ConfigureAwait(false);
            using MemoryStream buffer = new(frame, writable: false);
            using BinaryReader reader = new(buffer, Utf8);
            if (reader.ReadInt32() != Magic || reader.ReadInt32() != Version)
                throw new InvalidDataException("Unsupported host bootstrap protocol.");
            string java = ReadString(reader), directory = ReadString(reader), main = ReadString(reader);
            if (string.IsNullOrWhiteSpace(java) || string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(main))
                throw new InvalidDataException("Missing host bootstrap identity.");
            string[] jvm = ReadArguments(reader), game = ReadArguments(reader);
            if (buffer.Position != buffer.Length) throw new InvalidDataException("Trailing host bootstrap data.");
            return new(java, directory, main, jvm, game);
        }
        finally { Array.Clear(frame); }
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        byte[] bytes = Utf8.GetBytes(value);
        try { writer.Write(bytes.Length); writer.Write(bytes); }
        finally { Array.Clear(bytes); }
    }

    private static string ReadString(BinaryReader reader)
    {
        int count = reader.ReadInt32();
        if (count < 0 || count > MaxStringBytes || count > reader.BaseStream.Length - reader.BaseStream.Position)
            throw new InvalidDataException("Invalid host string size.");
        byte[] bytes = reader.ReadBytes(count);
        try
        {
            string value = Utf8.GetString(bytes);
            if (value.Contains('\0')) throw new InvalidDataException("Invalid host string.");
            return value;
        }
        finally { Array.Clear(bytes); }
    }

    private static void WriteArguments(BinaryWriter writer, string[] arguments)
    {
        writer.Write(arguments.Length);
        foreach (string argument in arguments) WriteString(writer, argument);
    }

    private static string[] ReadArguments(BinaryReader reader)
    {
        int count = reader.ReadInt32();
        if (count < 0 || count > MaxArguments || count > (reader.BaseStream.Length - reader.BaseStream.Position) / 4)
            throw new InvalidDataException("Invalid host argument count.");
        string[] arguments = new string[count];
        for (int index = 0; index < count; index++) arguments[index] = ReadString(reader);
        return arguments;
    }
}

public sealed record JvmHostBootstrapRequest(string JavaExecutable, string WorkingDirectory,
    string MainClass, IReadOnlyList<string> JvmArguments, IReadOnlyList<string> GameArguments);
