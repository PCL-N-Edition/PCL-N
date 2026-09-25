using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;

namespace Nexa.Services.Accounts;

public interface IProfileDataProtector
{
    byte[] Protect(byte[] plaintext);
    byte[] Unprotect(byte[] ciphertext);
}

/// <summary>Live account storage. No plaintext data is ever written to a temporary file.</summary>
public sealed class ProtectedLaunchProfilePort : ILaunchProfilePort
{
    private static readonly byte[] Magic = "NEXAAC01"u8.ToArray();
    private const int MaxBytes = 8 * 1024 * 1024;
    private static readonly ConcurrentDictionary<string, object> Gates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly string _path;
    private readonly IProfileDataProtector _protector;
    private bool _loaded;
    private bool _blocked;

    public ProtectedLaunchProfilePort(string path, IProfileDataProtector? protector = null)
    {
        _path = Path.GetFullPath(path);
        _protector = protector ?? new PlatformProfileDataProtector();
    }

    public LaunchProfileSet Load()
    {
        lock (Gates.GetOrAdd(_path, static _ => new object()))
        {
            EnsureWritable();
            try
            {
                LaunchProfileSet result = new();
                if (File.Exists(_path))
                {
                    byte[] raw = ReadBounded(_path);
                    byte[]? plain = null;
                    try
                    {
                        bool encrypted = raw.AsSpan().StartsWith(Magic);
                        // An unknown protected version is never retried as legacy JSON.
                        if (!encrypted && raw.AsSpan().StartsWith("NEXA"u8)) throw new IOException("未知的档案加密格式。");
                        plain = encrypted ? _protector.Unprotect(raw[Magic.Length..]) : raw;
                        result = JsonSerializer.Deserialize(plain, LaunchProfileJsonContext.Default.LaunchProfileSet)
                            ?? throw new IOException("档案内容无效。");
                        Validate(result);
                        if (!encrypted) WriteProtected(_path, plain);
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(raw);
                        if (plain is not null) CryptographicOperations.ZeroMemory(plain);
                    }
                }
                ProtectHistoricalCopies();
                _loaded = true;
                return result;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException or JsonException or NotSupportedException)
            {
                _blocked = true;
                throw new IOException("账户安全存储不可用，原档案已保留。请解锁系统密钥库并重启后重试。");
            }
        }
    }

    public void Save(LaunchProfileSet profiles)
    {
        lock (Gates.GetOrAdd(_path, static _ => new object()))
        {
            EnsureWritable();
            if (!_loaded) Load();
            Validate(profiles);
            byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(profiles, LaunchProfileJsonContext.Default.LaunchProfileSet);
            try { WriteProtected(_path, plaintext); }
            catch (Exception e) when (e is CryptographicException or NotSupportedException)
            { throw new IOException("无法保护账户凭据。请解锁系统密钥库后重试。"); }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
        }
    }

    private void EnsureWritable()
    {
        if (_blocked) throw new IOException("原账户档案未能安全读取，已阻止覆盖。请解锁系统密钥库并重启。");
    }

    private static void Validate(LaunchProfileSet profiles)
    {
        if (profiles.SchemaVersion != LaunchProfileSet.CurrentSchemaVersion || profiles.Profiles is null
            || profiles.Profiles.Any(p => p is null || string.IsNullOrWhiteSpace(p.Username) || !Enum.IsDefined(p.Kind)))
            throw new IOException("账户档案格式无效。");
    }

    private static void RejectLinks(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("账户目录不能包含符号链接。");
    }

    private static byte[] ReadBounded(string path)
    {
        RejectLinks(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaxBytes) throw new IOException("账户档案过大。");
        byte[] result = new byte[checked((int)stream.Length)];
        stream.ReadExactly(result);
        if (stream.ReadByte() != -1) { CryptographicOperations.ZeroMemory(result); throw new IOException("账户档案大小已改变。"); }
        return result;
    }

    private void WriteProtected(string path, byte[] plaintext)
    {
        if (plaintext.Length > MaxBytes - 4096) throw new IOException("账户档案过大。");
        byte[] encrypted = _protector.Protect(plaintext);
        if (encrypted.Length > MaxBytes - Magic.Length) throw new IOException("账户档案过大。");
        RejectLinks(path);
        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.WriteThrough };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var output = new FileStream(temporary, options))
            {
                output.Write(Magic);
                output.Write(encrypted);
                output.Flush(true);
            }
            RejectLinks(path);
            File.Move(temporary, path, true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encrypted);
            try { File.Delete(temporary); } catch (IOException) { }
        }
    }

    private void ProtectHistoricalCopies()
    {
        string directory = Path.GetDirectoryName(_path)!;
        if (!Directory.Exists(directory)) return;
        string prefix = "." + Path.GetFileName(_path) + ".";
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (string candidate in Directory.EnumerateFiles(directory))
        {
            string name = Path.GetFileName(candidate);
            bool temporary = name.Length == prefix.Length + 32 + 4 && name.StartsWith(prefix, comparison) && name.EndsWith(".tmp", comparison)
                && Guid.TryParseExact(name[prefix.Length..^4], "N", out _);
            if (!string.Equals(candidate, _path + ".invalid", comparison) && !temporary) continue;
            byte[] raw = ReadBounded(candidate);
            try
            {
                if (!raw.AsSpan().StartsWith(Magic)) WriteProtected(candidate, raw);
                else CryptographicOperations.ZeroMemory(_protector.Unprotect(raw[Magic.Length..]));
            }
            finally { CryptographicOperations.ZeroMemory(raw); }
        }
    }
}
