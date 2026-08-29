using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Bcpg;
using Org.BouncyCastle.Bcpg.OpenPgp;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Security;
using PCL.Services.Updates;

namespace PCL.Services.Tests;

// XSR-511: signature and delta codecs — the managed VCDIFF decoder against hand-built
// windows, and detached GPG verification against generated test keys.
internal static partial class Program
{
    internal static void VcdiffDecodesAddCopyAndRunInstructions()
    {
        // Window: ADD "Xc", COPY 5 bytes from source offset 0 (SELF address 0), RUN '!' x3.
        byte[] source = "Hello World"u8.ToArray();
        byte[] data = "Xc!"u8.ToArray();
        byte[] inst = [0x03, 0x13, 0x05, 0x00, 0x03];
        byte[] addr = [0x00];
        byte[] target = "XcHello!!!"u8.ToArray();
        byte[] delta = BuildDelta(source.Length, target.Length, data, inst, addr, withSource: true);

        AssertTrue(UpdateVcdiff.TryDecode(delta, source, out byte[] decoded));
        AssertTrue(decoded.SequenceEqual(target));

        // Empty source with an overlapping HERE-mode COPY: "ab" then COPY 4 from address 0.
        byte[] overlapData = "ab"u8.ToArray();
        byte[] overlapInst = [0x03, 0x23, 0x04];
        byte[] overlapAddr = [0x02];
        byte[] overlapDelta = BuildDelta(0, 6, overlapData, overlapInst, overlapAddr, withSource: false);
        AssertTrue(UpdateVcdiff.TryDecode(overlapDelta, [], out byte[] overlap));
        AssertTrue(overlap.SequenceEqual("ababab"u8.ToArray()));
    }

    internal static void VcdiffRejectsUnsupportedAndCorruptDeltas()
    {
        AssertFalse(UpdateVcdiff.TryDecode([0xD6, 0xC3, 0xC4], [], out _));
        AssertFalse(UpdateVcdiff.TryDecode([0x00, 0x01, 0x02, 0x03], [], out _));

        // Secondary compression flag is refused.
        byte[] compressed = [0xD6, 0xC3, 0xC4, 0x01, 0x00];
        AssertFalse(UpdateVcdiff.TryDecode(compressed, [], out _));

        // Custom code table flag is refused.
        byte[] customTable = [0xD6, 0xC3, 0xC4, 0x02, 0x00];
        AssertFalse(UpdateVcdiff.TryDecode(customTable, [], out _));

        // Truncated varint in the window header.
        byte[] truncated = [0xD6, 0xC3, 0xC4, 0x00, 0x81];
        AssertFalse(UpdateVcdiff.TryDecode(truncated, [], out _));

        // Section lengths exceeding the window payload.
        byte[] overflow = [0xD6, 0xC3, 0xC4, 0x00, 0x00, 0x0A, 0x00, 0x7F, 0x01, 0x01, 0x01, 0x61];
        AssertFalse(UpdateVcdiff.TryDecode(overflow, [], out _));

        AssertEqual("vcdiff-rfc3284", UpdateVcdiff.Algorithm);
    }

    internal static async ValueTask GpgVerifierAcceptsGenuineDetachedSignature()
    {
        (string armoredKey, string fingerprint, Func<byte[], string> Sign) = GenerateSigningKey();

        UpdateGpgVerifier verifier = new(armoredKey, fingerprint);
        byte[] payload = DeterministicBytes(200_000, 0x66);
        using MemoryStream content = new(payload);
        using MemoryStream signature = new(Encoding.ASCII.GetBytes(Sign(payload)));
        await verifier.VerifyAsync(content, signature);
    }

    internal static async ValueTask GpgVerifierRejectsTamperedForeignAndUnpinnedKeys()
    {
        (string armoredKey, string fingerprint, Func<byte[], string> Sign) = GenerateSigningKey();

        // A tampered payload fails the signature check.
        UpdateGpgVerifier verifier = new(armoredKey, fingerprint);
        byte[] payload = DeterministicBytes(5_000, 0x77);
        byte[] tampered = [.. payload];
        tampered[^1] ^= 0xFF;
        using MemoryStream tamperedContent = new(tampered);
        using MemoryStream signature = new(Encoding.ASCII.GetBytes(Sign(payload)));
        bool tamperedRejected = false;
        try
        {
            await verifier.VerifyAsync(tamperedContent, signature);
        }
        catch (InvalidDataException failure)
        {
            tamperedRejected = failure.Message.Contains("校验失败", StringComparison.Ordinal);
        }

        AssertTrue(tamperedRejected);

        // A well-formed signature from an unknown key is refused as unauthorized.
        (string otherArmored, string otherFingerprint, Func<byte[], string> _) = GenerateSigningKey();
        UpdateGpgVerifier strict = new(otherArmored, otherFingerprint);
        using MemoryStream foreignContent = new(payload);
        using MemoryStream foreignSignature = new(Encoding.ASCII.GetBytes(Sign(payload)));
        bool foreignRejected = false;
        try
        {
            await strict.VerifyAsync(foreignContent, foreignSignature);
        }
        catch (InvalidDataException failure)
        {
            foreignRejected = failure.Message.Contains("未授权", StringComparison.Ordinal);
        }

        AssertTrue(foreignRejected);

        // The pinned release fingerprint refuses any other key even when it signs correctly.
        UpdateGpgVerifier pinned = new(armoredKey);
        using MemoryStream pinnedContent = new(payload);
        using MemoryStream pinnedSignature = new(Encoding.ASCII.GetBytes(Sign(payload)));
        bool pinRejected = false;
        try
        {
            await pinned.VerifyAsync(pinnedContent, pinnedSignature);
        }
        catch (InvalidDataException failure)
        {
            pinRejected = failure.Message.Contains("指纹不匹配", StringComparison.Ordinal);
        }

        AssertTrue(pinRejected);

        // Garbage signatures are rejected as invalid rather than crashing.
        UpdateGpgVerifier garbageVerifier = new(armoredKey, fingerprint);
        using MemoryStream garbageContent = new(payload);
        using MemoryStream garbage = new("hello world"u8.ToArray());
        bool garbageRejected = false;
        try
        {
            await garbageVerifier.VerifyAsync(garbageContent, garbage);
        }
        catch (InvalidDataException)
        {
            garbageRejected = true;
        }

        AssertTrue(garbageRejected);
    }

    private static byte[] BuildDelta(
        long sourceLength,
        int targetLength,
        byte[] data,
        byte[] inst,
        byte[] addr,
        bool withSource)
    {
        // Window layout: winIndicator, [sourceSize, sourcePos], deltaEncodingLength, then the
        // delta encoding (targetLen, indicator, section lengths, data, inst, addr).
        List<byte> encoding =
        [
            .. Varint(targetLength),
            0x00, // delta indicator: no interleaving, no secondary compression
            .. Varint(data.Length),
            .. Varint(inst.Length),
            .. Varint(addr.Length),
            .. data,
            .. inst,
            .. addr,
        ];

        List<byte> window =
        [
            withSource ? (byte)0x01 : (byte)0x00,
        ];
        if (withSource)
        {
            window.AddRange(Varint(sourceLength));
            window.AddRange(Varint(0));
        }

        window.AddRange(Varint(encoding.Count));
        window.AddRange(encoding);

        List<byte> delta = [0xD6, 0xC3, 0xC4, 0x00, .. window];
        return [.. delta];
    }

    private static List<byte> Varint(long value)
    {
        List<byte> bytes = [];
        do
        {
            byte current = (byte)(value & 0x7F);
            value >>= 7;
            if (value != 0)
            {
                current |= 0x80;
            }

            bytes.Add(current);
        }
        while (value != 0);

        return bytes;
    }

    private static (string ArmoredKey, string Fingerprint, Func<byte[], string> Sign) GenerateSigningKey()
    {
        RsaKeyPairGenerator generator = new();
        generator.Init(new Org.BouncyCastle.Crypto.Parameters.RsaKeyGenerationParameters(
            Org.BouncyCastle.Math.BigInteger.ValueOf(0x10001),
            new SecureRandom(),
            1024,
            12));
        PgpKeyPair keyPair = new(
            PublicKeyAlgorithmTag.RsaGeneral,
            generator.GenerateKeyPair(),
            DateTime.UtcNow);
        PgpKeyRingGenerator ringGenerator = new(
            PgpSignature.DefaultCertification,
            keyPair,
            "update-test@pcln.example",
            SymmetricKeyAlgorithmTag.Aes256,
            Array.Empty<char>(),
            false,
            new PgpSignatureSubpacketGenerator().Generate(),
            new PgpSignatureSubpacketGenerator().Generate(),
            new SecureRandom());
        PgpSecretKeyRing secretRing = ringGenerator.GenerateSecretKeyRing();
        PgpPublicKeyRing publicRing = ringGenerator.GeneratePublicKeyRing();

        MemoryStream keyBuffer = new();
        using (ArmoredOutputStream armor = new(keyBuffer))
        {
            publicRing.Encode(armor);
        }

        string armoredKey = Encoding.ASCII.GetString(keyBuffer.ToArray());
        string fingerprint = Convert.ToHexString(publicRing.GetPublicKey().GetFingerprint());

        PgpPrivateKey privateKey = secretRing.GetSecretKey().ExtractPrivateKey([]);
        string Sign(byte[] payload)
        {
            MemoryStream signatureBuffer = new();
            using (ArmoredOutputStream armor = new(signatureBuffer))
            {
                PgpSignatureGenerator signatureGenerator = new(secretRing.GetSecretKey().PublicKey.Algorithm, HashAlgorithmTag.Sha256);
                signatureGenerator.InitSign(PgpSignature.BinaryDocument, privateKey);
                signatureGenerator.Update(payload, 0, payload.Length);
                signatureGenerator.Generate().Encode(armor);
            }

            return Encoding.ASCII.GetString(signatureBuffer.ToArray());
        }

        return (armoredKey, fingerprint, Sign);
    }
}
