using System.Security.Cryptography;
using System.Text;
using Nexa.Services.Accounts;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static void AssertThrows<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static ValueTask ProtectedProfilesMigrateWithoutPlaintextCopies()
    {
        string root = CreateTempDirectory();
        try
        {
            string path = Path.Combine(root, "profiles.json");
            var profile = SampleProfile("Alice", "SENSITIVE_ACCESS") with
            { RefreshToken = "SENSITIVE_REFRESH", ProviderAccessToken = "SENSITIVE_PROVIDER", ClientToken = "SENSITIVE_CLIENT" };
            var profiles = new LaunchProfileSet { Profiles = [profile] };
            new LaunchProfileFilePort(path).Save(profiles);
            string historicalName = OperatingSystem.IsWindows() ? "PROFILES.JSON" : "profiles.json";
            File.Copy(path, Path.Combine(root, historicalName + ".invalid"));
            File.Copy(path, Path.Combine(root, $".{historicalName}.{Guid.NewGuid():N}.tmp"));
            var cipher = new TestAccountProtector();
            var port = new ProtectedLaunchProfilePort(path, cipher);
            AssertEqual(profile, port.Load().Profiles[0]);
            AssertEqual(profile, new ProtectedLaunchProfilePort(path, cipher).Load().Profiles[0]);
            foreach (string file in Directory.GetFiles(root))
                AssertFalse(Encoding.UTF8.GetString(File.ReadAllBytes(file)).Contains("SENSITIVE_", StringComparison.Ordinal));
            byte[] encrypted = File.ReadAllBytes(path);
            encrypted[^1] ^= 1;
            File.WriteAllBytes(path, encrypted);
            var broken = new ProtectedLaunchProfilePort(path, cipher);
            AssertThrows<IOException>(() => broken.Load());
            AssertThrows<IOException>(() => broken.Save(new()));
            AssertThrows<IOException>(() => new ProtectedLaunchProfilePort(path, cipher).Save(new()));
            AssertTrue(encrypted.SequenceEqual(File.ReadAllBytes(path)));
            string largePath = Path.Combine(root, "large.json");
            new LaunchProfileFilePort(largePath).Save(new() { Profiles = Enumerable.Range(0, 257).Select(i => SampleProfile("Account" + i)).ToArray() });
            AssertEqual(257, new ProtectedLaunchProfilePort(largePath, cipher).Load().Profiles.Count);
            var accounts = CreateAccountService(broken);
            AssertFalse(accounts.AddProfile(SampleProfile("Replacement")).IsSuccess);
            AssertFalse(accounts.ImportProfiles([SampleProfile("Replacement")]).IsSuccess);
            AssertTrue(encrypted.SequenceEqual(File.ReadAllBytes(path)));
        }
        finally { Directory.Delete(root, true); }
        return ValueTask.CompletedTask;
    }

    private static ValueTask ProtectedProfileFailuresPreserveOriginal()
    {
        string root = CreateTempDirectory();
        try
        {
            string path = Path.Combine(root, "profiles.json");
            new LaunchProfileFilePort(path).Save(new() { Profiles = [SampleProfile("Alice", "SENSITIVE_ACCESS")] });
            byte[] original = File.ReadAllBytes(path);
            var unavailable = new ProtectedLaunchProfilePort(path, new TestAccountProtector { Fail = true });
            AssertThrows<IOException>(() => unavailable.Load());
            AssertThrows<IOException>(() => unavailable.Save(new()));
            AssertTrue(original.SequenceEqual(File.ReadAllBytes(path)));
            AssertEqual(1, Directory.GetFiles(root).Length);
            var cipher = new TestAccountProtector();
            var recovered = new ProtectedLaunchProfilePort(path, cipher);
            AssertEqual("SENSITIVE_ACCESS", recovered.Load().Profiles[0].AccessToken);
            byte[] protectedBytes = File.ReadAllBytes(path);
            cipher.Fail = true;
            AssertThrows<IOException>(() => recovered.Save(new()));
            AssertTrue(protectedBytes.SequenceEqual(File.ReadAllBytes(path)));
            cipher.Fail = false;
            AssertThrows<IOException>(() => new ProtectedLaunchProfilePort(path, new TestAccountProtector()).Load());
            File.WriteAllText(path, "{ malformed SENSITIVE_ACCESS");
            AssertThrows<IOException>(() => new ProtectedLaunchProfilePort(path, cipher).Load());
            AssertEqual(1, Directory.GetFiles(root).Length);
        }
        finally { Directory.Delete(root, true); }
        return ValueTask.CompletedTask;
    }

    private static ValueTask NativeProfileProtectionUsesCurrentUser()
    {
        if (!OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("NEXA_TEST_SYSTEM_KEYRING") != "1")
        {
            Console.WriteLine("SKIP: native keyring test requires an isolated unlocked system keyring.");
            return ValueTask.CompletedTask;
        }
        var protector = new PlatformProfileDataProtector();
        byte[] plain = "SENSITIVE_NATIVE_TEST"u8.ToArray();
        byte[] encrypted = protector.Protect(plain);
        AssertFalse(encrypted.AsSpan().IndexOf(plain) >= 0);
        AssertTrue(plain.SequenceEqual(new PlatformProfileDataProtector().Unprotect(encrypted)));
        encrypted[^1] ^= 1;
        AssertThrows<CryptographicException>(() => protector.Unprotect(encrypted));
        return ValueTask.CompletedTask;
    }

    private sealed class TestAccountProtector : IProfileDataProtector
    {
        private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
        internal bool Fail { get; set; }
        public byte[] Protect(byte[] plaintext)
        {
            if (Fail) throw new CryptographicException();
            byte[] result = new byte[28 + plaintext.Length];
            RandomNumberGenerator.Fill(result.AsSpan(0, 12));
            using var aes = new AesGcm(_key, 16);
            aes.Encrypt(result.AsSpan(0, 12), plaintext, result.AsSpan(28), result.AsSpan(12, 16));
            return result;
        }
        public byte[] Unprotect(byte[] ciphertext)
        {
            if (Fail) throw new CryptographicException();
            byte[] result = new byte[ciphertext.Length - 28];
            using var aes = new AesGcm(_key, 16);
            aes.Decrypt(ciphertext.AsSpan(0, 12), ciphertext.AsSpan(28), ciphertext.AsSpan(12, 16), result);
            return result;
        }
    }
}
