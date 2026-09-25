using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Nexa.Services.Accounts;

/// <summary>Current-user OS protection; unavailable backends never become a plaintext store.</summary>
internal sealed partial class PlatformProfileDataProtector : IProfileDataProtector
{
    private Guid? _keyId;
    public byte[] Protect(byte[] plaintext)
    {
        if (OperatingSystem.IsWindows()) return [1, .. Dpapi(plaintext, protect: true)];
        Guid id = _keyId ?? Guid.NewGuid();
        byte[] key = _keyId is null ? RandomNumberGenerator.GetBytes(32) : ReadKey(id);
        try
        {
            if (_keyId is null) { StoreKey(id, key); _keyId = id; }
            byte[] envelope = new byte[1 + 16 + 12 + 16 + plaintext.Length];
            envelope[0] = 2;
            id.TryWriteBytes(envelope.AsSpan(1, 16));
            RandomNumberGenerator.Fill(envelope.AsSpan(17, 12));
            using var cipher = new AesGcm(key, 16);
            cipher.Encrypt(envelope.AsSpan(17, 12), plaintext, envelope.AsSpan(45), envelope.AsSpan(29, 16), envelope.AsSpan(0, 17));
            return envelope;
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    public byte[] Unprotect(byte[] ciphertext)
    {
        if (ciphertext.Length > 1 && ciphertext[0] == 1 && OperatingSystem.IsWindows()) return Dpapi(ciphertext[1..], protect: false);
        if (ciphertext.Length < 45 || ciphertext[0] != 2 || OperatingSystem.IsWindows()) throw new CryptographicException("Unsupported profile protection.");
        byte[] key = ReadKey(new Guid(ciphertext.AsSpan(1, 16)));
        byte[] plaintext = new byte[ciphertext.Length - 45];
        try
        {
            using var cipher = new AesGcm(key, 16);
            cipher.Decrypt(ciphertext.AsSpan(17, 12), ciphertext.AsSpan(45), ciphertext.AsSpan(29, 16), plaintext, ciphertext.AsSpan(0, 17));
            _keyId = new Guid(ciphertext.AsSpan(1, 16));
            return plaintext;
        }
        catch { CryptographicOperations.ZeroMemory(plaintext); throw; }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private static unsafe byte[] Dpapi(byte[] data, bool protect)
    {
        fixed (byte* bytes = data)
        {
            Blob input = new() { Length = data.Length, Data = (nint)bytes };
            Blob output = default;
            bool success = protect
                ? CryptProtectData(ref input, 0, 0, 0, 0, 1, out output) != 0
                : CryptUnprotectData(ref input, 0, 0, 0, 0, 1, out output) != 0;
            if (!success) throw new CryptographicException("Windows account protection is unavailable.");
            try
            {
                if (output.Length < 0 || output.Length > 8 * 1024 * 1024) throw new CryptographicException("Invalid protected data length.");
                return new ReadOnlySpan<byte>((void*)output.Data, output.Length).ToArray();
            }
            finally
            {
                if (!protect && output.Data != 0 && output.Length > 0) CryptographicOperations.ZeroMemory(new Span<byte>((void*)output.Data, output.Length));
                LocalFree(output.Data);
            }
        }
    }

    private static void StoreKey(Guid id, byte[] key)
    {
        if (OperatingSystem.IsMacOS()) MacStore(id, key);
        else if (OperatingSystem.IsLinux()) RunSecretTool(id, Convert.ToBase64String(key));
        else throw new IOException("当前平台没有可用的账户安全存储。");
    }

    private static byte[] ReadKey(Guid id)
    {
        byte[] key;
        if (OperatingSystem.IsMacOS()) key = MacRead(id);
        else if (OperatingSystem.IsLinux())
        {
            try { key = Convert.FromBase64String(RunSecretTool(id, null).Trim()); }
            catch (FormatException) { throw new IOException("系统密钥库未返回有效账户密钥。"); }
        }
        else throw new IOException("当前平台没有可用的账户安全存储。");
        if (key.Length == 32) return key;
        CryptographicOperations.ZeroMemory(key);
        throw new IOException("系统密钥库中的账户密钥无效。");
    }

    private static string RunSecretTool(Guid id, string? secret)
    {
        if (!File.Exists("/usr/bin/secret-tool")) throw new IOException("请安装系统的 secret-tool（Debian/Ubuntu：libsecret-tools；Fedora：libsecret），并解锁系统密钥库。");
        var start = new ProcessStartInfo("/usr/bin/secret-tool")
        { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add(secret is null ? "lookup" : "store");
        if (secret is not null) start.ArgumentList.Add("--label=NexaCL account encryption");
        start.ArgumentList.Add("application"); start.ArgumentList.Add("NexaCL");
        start.ArgumentList.Add("key-id"); start.ArgumentList.Add(id.ToString("N"));
        try
        {
            using var process = Process.Start(start) ?? throw new IOException("无法访问系统密钥库。");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try { return ExchangeAsync(process, secret, deadline.Token).GetAwaiter().GetResult(); }
            catch (Exception e) when (e is OperationCanceledException or IOException)
            {
                try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
                throw new IOException("系统密钥库不可用或已锁定。");
            }
        }
        catch (System.ComponentModel.Win32Exception) { throw new IOException("无法启动系统密钥库客户端。"); }
    }

    private static async Task<string> ExchangeAsync(Process process, string? secret, CancellationToken token)
    {
        // Both pipes are bounded and drained concurrently; neither is logged or shown in UI.
        Task<string> stdout = ReadSmallAsync(process.StandardOutput, token);
        Task<string> stderr = ReadSmallAsync(process.StandardError, token);
        if (secret is not null) await process.StandardInput.WriteAsync(secret.AsMemory(), token).ConfigureAwait(false);
        process.StandardInput.Close();
        await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(token)).ConfigureAwait(false);
        if (process.ExitCode != 0) throw new IOException("系统密钥库操作失败。");
        return await stdout.ConfigureAwait(false);
    }

    private static async Task<string> ReadSmallAsync(StreamReader reader, CancellationToken token)
    {
        char[] buffer = new char[1025];
        int count = 0;
        while (count < buffer.Length)
        {
            int read = await reader.ReadAsync(buffer.AsMemory(count), token).ConfigureAwait(false);
            if (read == 0) return new string(buffer, 0, count);
            count += read;
        }
        throw new IOException("系统密钥库响应过大。");
    }

    private static readonly byte[] Service = "NexaCL.accounts"u8.ToArray();
    private static unsafe void MacStore(Guid id, byte[] key)
    {
        byte[] account = Encoding.UTF8.GetBytes(id.ToString("N"));
        fixed (byte* service = Service, name = account, value = key)
            if (SecKeychainAddGenericPassword(0, (uint)Service.Length, service, (uint)account.Length, name, (uint)key.Length, value, 0) != 0)
                throw new IOException("无法将账户密钥保存到系统钥匙串。");
    }

    private static unsafe byte[] MacRead(Guid id)
    {
        byte[] account = Encoding.UTF8.GetBytes(id.ToString("N"));
        fixed (byte* service = Service, name = account)
        {
            if (SecKeychainFindGenericPassword(0, (uint)Service.Length, service, (uint)account.Length, name, out uint length, out nint data, 0) != 0)
                throw new IOException("无法读取账户密钥，请解锁系统钥匙串。");
            try
            {
                if (length != 32) throw new IOException("系统钥匙串中的账户密钥无效。");
                return new ReadOnlySpan<byte>((void*)data, 32).ToArray();
            }
            finally { _ = SecKeychainItemFreeContent(0, data); }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Blob { public int Length; public nint Data; }
    [LibraryImport("crypt32.dll", SetLastError = true)]
    private static partial int CryptProtectData(ref Blob input, nint description, nint entropy, nint reserved, nint prompt, uint flags, out Blob output);
    [LibraryImport("crypt32.dll", SetLastError = true)]
    private static partial int CryptUnprotectData(ref Blob input, nint description, nint entropy, nint reserved, nint prompt, uint flags, out Blob output);
    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint data);
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    [LibraryImport(Security)]
    private static unsafe partial int SecKeychainAddGenericPassword(nint keychain, uint serviceLength, byte* service, uint accountLength, byte* account, uint passwordLength, byte* password, nint item);
    [LibraryImport(Security)]
    private static unsafe partial int SecKeychainFindGenericPassword(nint keychain, uint serviceLength, byte* service, uint accountLength, byte* account, out uint passwordLength, out nint password, nint item);
    [LibraryImport(Security)]
    private static partial int SecKeychainItemFreeContent(nint attributes, nint data);
}
