// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Org.BouncyCastle.Bcpg.OpenPgp;

namespace PCL.Application.Updates;

internal interface ILauncherGpgVerifier
{
    Task VerifyAsync(Stream content, Stream detachedSignature, CancellationToken cancellationToken);
}

/// <summary>Verifies PCL N release artifacts against the public key pinned in the launcher.</summary>
internal sealed class LauncherGpgVerifier : ILauncherGpgVerifier
{
    internal const string ExpectedFingerprint = "5701218D69B531E1A7ED35BB6E31F5974A273AEE";
    private const string PublicKeyResourceName = "PCL.Application.Updates.PclNReleasePublicKey.asc";

    public static LauncherGpgVerifier Instance { get; } = new();

    public async Task VerifyAsync(
        Stream content,
        Stream detachedSignature,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(detachedSignature);

        PgpSignature signature = ReadSignature(detachedSignature);
        PgpPublicKey publicKey = LoadPinnedPublicKey(signature.KeyId);
        string fingerprint = Convert.ToHexString(publicKey.GetFingerprint());
        if (!string.Equals(fingerprint, ExpectedFingerprint, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"GPG 公钥指纹不匹配：{fingerprint}。");

        signature.InitVerify(publicKey);
        byte[] buffer = new byte[1024 * 128];
        while (true)
        {
            int read = await content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            signature.Update(buffer, 0, read);
        }

        if (!signature.Verify())
            throw new InvalidDataException("GPG 签名校验失败，更新文件可能已被修改。");
    }

    private static PgpSignature ReadSignature(Stream detachedSignature)
    {
        using Stream decoded = PgpUtilities.GetDecoderStream(detachedSignature);
        PgpObjectFactory factory = new(decoded);
        PgpObject? value = factory.NextPgpObject();
        if (value is PgpCompressedData compressed)
        {
            using Stream compressedData = compressed.GetDataStream();
            value = new PgpObjectFactory(compressedData).NextPgpObject();
        }
        if (value is not PgpSignatureList signatures || signatures.Count == 0)
            throw new InvalidDataException("更新签名不是有效的 detached GPG 签名。");
        return signatures[0];
    }

    private static PgpPublicKey LoadPinnedPublicKey(long keyId)
    {
        using Stream resource = typeof(LauncherGpgVerifier).Assembly.GetManifestResourceStream(PublicKeyResourceName)
            ?? throw new InvalidOperationException("启动器缺少内置的更新签名公钥。");
        using Stream decoded = PgpUtilities.GetDecoderStream(resource);
        PgpPublicKeyRingBundle bundle = new(decoded);
        PgpPublicKey? key = bundle.GetPublicKey(keyId);
        return key ?? throw new InvalidDataException($"更新签名使用了未授权的 GPG 密钥：{keyId:X16}。");
    }
}
