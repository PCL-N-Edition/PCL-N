using System.Text;
using Org.BouncyCastle.Bcpg;
using Org.BouncyCastle.Bcpg.OpenPgp;

namespace PCL.Services.Updates;

/// <summary>
/// Verifies update artifacts against the release signing key. Implementations must throw
/// <see cref="InvalidDataException"/> for any verification failure so the update flow can
/// treat every outcome uniformly as "untrusted".
/// </summary>
public interface IUpdateSignatureVerifier
{
    Task VerifyAsync(Stream content, Stream detachedSignature, CancellationToken cancellationToken = default);
}

/// <summary>
/// Detached ASCII-armored GPG verification over the release public key. The armored public
/// key is supplied by the composition root; the expected fingerprint defaults to the pinned
/// PCL N release key, so only that key can ever authorize an update even if a signature is
/// otherwise well-formed.
/// </summary>
public sealed class UpdateGpgVerifier : IUpdateSignatureVerifier
{
    public const string ReleaseKeyFingerprint = "5701218D69B531E1A7ED35BB6E31F5974A273AEE";

    private readonly string _armoredPublicKey;
    private readonly string _expectedFingerprint;

    public UpdateGpgVerifier(string armoredPublicKey, string? expectedFingerprint = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(armoredPublicKey);
        _armoredPublicKey = armoredPublicKey;
        _expectedFingerprint = expectedFingerprint ?? ReleaseKeyFingerprint;
    }

    public async Task VerifyAsync(
        Stream content,
        Stream detachedSignature,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(detachedSignature);

        // Armored OpenPGP decoding probes and rewinds its input, so buffer the (small)
        // signature instead of passing a forward-only network stream through directly.
        using MemoryStream signatureBuffer = new();
        await detachedSignature.CopyToAsync(signatureBuffer, cancellationToken).ConfigureAwait(false);
        signatureBuffer.Position = 0;
        PgpSignature signature = ReadSignature(signatureBuffer);
        PgpPublicKey publicKey = LoadPublicKey(signature.KeyId);
        string fingerprint = Convert.ToHexString(publicKey.GetFingerprint());
        if (!string.Equals(fingerprint, _expectedFingerprint, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"GPG 公钥指纹不匹配：{fingerprint}。");
        }

        signature.InitVerify(publicKey);
        byte[] buffer = new byte[128 * 1024];
        while (true)
        {
            int read = await content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            signature.Update(buffer, 0, read);
        }

        if (!signature.Verify())
        {
            throw new InvalidDataException("GPG 签名校验失败，更新文件可能已被修改。");
        }
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
        {
            throw new InvalidDataException("更新签名不是有效的 detached GPG 签名。");
        }

        return signatures[0];
    }

    private PgpPublicKey LoadPublicKey(long keyId)
    {
        byte[] armored = Encoding.ASCII.GetBytes(_armoredPublicKey);
        using MemoryStream armoredStream = new(armored);
        using Stream decoded = PgpUtilities.GetDecoderStream(armoredStream);
        PgpPublicKeyRingBundle bundle = new(decoded);
        PgpPublicKey? key = bundle.GetPublicKey(keyId);
        return key ?? throw new InvalidDataException($"更新签名使用了未授权的 GPG 密钥：{keyId:X16}。");
    }
}
