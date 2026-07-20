// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Jvm.NET.Asm;

namespace PCL.Desktop.Features.Launching;

/// <summary>
/// ASM rewrite of <c>com.mojang.authlib.*</c> class files: redirect Mojang endpoints,
/// trust texture domains, and accept profile property signatures after launcher-side checks.
/// Used both for on-disk jar replacement and optional JVMTI transformers.
/// </summary>
internal static class AuthlibClassTransformer
{
    /// <summary>Bump when patch semantics change so on-disk caches invalidate.</summary>
    public const string PatchRevision = "v2";

    public static byte[]? Transform(string className, byte[] originalBytes, AuthlibPatchProfile profile)
    {
        ArgumentNullException.ThrowIfNull(originalBytes);
        ArgumentNullException.ThrowIfNull(profile);

        string normalized = className.Replace('.', '/');
        if (!normalized.Contains("com/mojang/authlib/", StringComparison.OrdinalIgnoreCase) &&
            !normalized.StartsWith("com/mojang/authlib/", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            ClassReader reader = new(originalBytes);
            // Keep original frames; only rewrite constants / selected concrete method bodies.
            ClassWriter writer = new(reader, 0);
            TransformVisitor visitor = new(writer, profile);
            reader.Accept(visitor, ClassReader.EXPAND_FRAMES);
            return visitor.Modified ? writer.ToByteArray() : null;
        }
        catch
        {
            return null;
        }
    }

    private sealed class TransformVisitor(ClassVisitor next, AuthlibPatchProfile profile)
        : ClassVisitor(Opcodes.ASM9, next)
    {
        private int _classAccess;

        public bool Modified { get; private set; }

        public override void Visit(
            int version,
            int access,
            string? name,
            string? signature,
            string? superName,
            string[]? interfaces)
        {
            _classAccess = access;
            base.Visit(version, access, name, signature, superName, interfaces);
        }

        public override FieldVisitor? VisitField(
            int access,
            string? name,
            string? descriptor,
            string? signature,
            object? value)
        {
            object? replaced = value is string text ? Replace(text) : value;
            return base.VisitField(access, name, descriptor, signature, replaced);
        }

        public override MethodVisitor? VisitMethod(
            int access,
            string? name,
            string? descriptor,
            string? signature,
            string[]? exceptions)
        {
            // NEVER emit Code on abstract/native methods (interfaces include validateProperty etc.).
            // That produced: ClassFormatError: Code attribute in native or abstract methods
            // (com/mojang/authlib/yggdrasil/ServicesKeyInfo).
            bool isAbstractOrNative = (access & (Opcodes.ACC_ABSTRACT | Opcodes.ACC_NATIVE)) != 0;
            bool isInterface = (_classAccess & Opcodes.ACC_INTERFACE) != 0;

            if (!isAbstractOrNative &&
                !isInterface &&
                profile.RelaxTextureDomains &&
                IsConcreteTextureAllowMethod(name, descriptor))
            {
                MethodVisitor? generated = Cv?.VisitMethod(access, name, descriptor, signature, exceptions);
                if (generated is not null)
                {
                    generated.VisitCode();
                    generated.VisitInsn(Opcodes.ICONST_1);
                    generated.VisitInsn(Opcodes.IRETURN);
                    generated.VisitMaxs(1, CalculateLocals(access, descriptor));
                    generated.VisitEnd();
                }

                Modified = true;
                return null;
            }

            if (!isAbstractOrNative &&
                !isInterface &&
                profile.TrustSignatures &&
                IsConcreteSignatureTrustMethod(name, descriptor))
            {
                MethodVisitor? generated = Cv?.VisitMethod(access, name, descriptor, signature, exceptions);
                if (generated is not null)
                {
                    generated.VisitCode();
                    generated.VisitInsn(Opcodes.ICONST_1);
                    generated.VisitInsn(Opcodes.IRETURN);
                    generated.VisitMaxs(1, CalculateLocals(access, descriptor));
                    generated.VisitEnd();
                }

                Modified = true;
                return null;
            }

            if (!isAbstractOrNative &&
                !isInterface &&
                profile.TrustSignatures &&
                string.Equals(name, "getPropertySignatureState", StringComparison.Ordinal) &&
                descriptor?.EndsWith(")Lcom/mojang/authlib/SignatureState;", StringComparison.Ordinal) == true)
            {
                MethodVisitor? generated = Cv?.VisitMethod(access, name, descriptor, signature, exceptions);
                if (generated is not null)
                {
                    generated.VisitCode();
                    generated.VisitFieldInsn(
                        Opcodes.GETSTATIC,
                        "com/mojang/authlib/SignatureState",
                        "SIGNED",
                        "Lcom/mojang/authlib/SignatureState;");
                    generated.VisitInsn(Opcodes.ARETURN);
                    generated.VisitMaxs(1, CalculateLocals(access, descriptor));
                    generated.VisitEnd();
                }

                Modified = true;
                return null;
            }

            MethodVisitor? method = base.VisitMethod(access, name, descriptor, signature, exceptions);
            // Abstract/native methods have no Code — do not wrap (avoids accidental Code attrs).
            if (method is null || isAbstractOrNative)
                return method;
            return new StringVisitor(method, this);
        }

        /// <summary>
        /// Only concrete TextureUrlChecker-style helpers. Do not match interface methods
        /// like ServicesKeyInfo.validateProperty.
        /// </summary>
        private static bool IsConcreteTextureAllowMethod(string? name, string? descriptor) =>
            (name is "isAllowedTextureDomain" or "isWhitelistedDomain") &&
            string.Equals(descriptor, "(Ljava/lang/String;)Z", StringComparison.Ordinal);

        /// <summary>
        /// Concrete Property helpers only — never ServicesKeyInfo.validateProperty (interface).
        /// </summary>
        private static bool IsConcreteSignatureTrustMethod(string? name, string? descriptor) =>
            (name is "isSignatureValid" or "isValidSignature") &&
            descriptor?.EndsWith(")Z", StringComparison.Ordinal) == true;

        private static int CalculateLocals(int access, string? descriptor)
        {
            int locals = (access & Opcodes.ACC_STATIC) == 0 ? 1 : 0;
            if (descriptor is null)
                return locals;
            bool inType = false;
            for (int i = 0; i < descriptor.Length && descriptor[i] != ')'; i++)
            {
                char c = descriptor[i];
                if (c == '(')
                {
                    inType = true;
                    continue;
                }

                if (!inType || c == '[')
                    continue;
                if (c == 'L')
                {
                    i = descriptor.IndexOf(';', i);
                    if (i < 0)
                        break;
                }

                locals += c is 'J' or 'D' ? 2 : 1;
            }

            return locals;
        }

        private string Replace(string value)
        {
            string result = value;
            foreach ((string source, string target) in profile.UrlReplacements)
                result = result.Replace(source, target, StringComparison.Ordinal);
            if (!string.Equals(result, value, StringComparison.Ordinal))
                Modified = true;
            return result;
        }

        private sealed class StringVisitor(MethodVisitor next, TransformVisitor owner)
            : MethodVisitor(Opcodes.ASM9, next)
        {
            public override void VisitLdcInsn(object? value)
            {
                base.VisitLdcInsn(value is string text ? owner.Replace(text) : value);
            }

            public override void VisitInvokeDynamicInsn(
                string? name,
                string? descriptor,
                Handle? bootstrapMethodHandle,
                params object?[]? bootstrapMethodArguments)
            {
                if (bootstrapMethodArguments is not null)
                {
                    for (int i = 0; i < bootstrapMethodArguments.Length; i++)
                    {
                        if (bootstrapMethodArguments[i] is string text)
                            bootstrapMethodArguments[i] = owner.Replace(text);
                    }
                }

                base.VisitInvokeDynamicInsn(name, descriptor, bootstrapMethodHandle, bootstrapMethodArguments);
            }
        }
    }
}

/// <summary>URL rewrites + policy flags applied when patching authlib classes.</summary>
internal sealed class AuthlibPatchProfile
{
    public required IReadOnlyDictionary<string, string> UrlReplacements { get; init; }
    public bool TrustSignatures { get; init; } = true;
    public bool RelaxTextureDomains { get; init; } = true;
    public string CacheKey { get; init; } = "default";

    public static AuthlibPatchProfile ForYggdrasilServer(string authServerRoot)
    {
        string root = authServerRoot.Trim().TrimEnd('/');
        if (root.EndsWith("/authserver", StringComparison.OrdinalIgnoreCase))
            root = root[..^"/authserver".Length].TrimEnd('/');

        Dictionary<string, string> map = CreateMojangUrlMap(root + "/sessionserver", root + "/authserver", root + "/api", root + "/minecraftservices", root + "/minecraft/client");
        return new AuthlibPatchProfile
        {
            UrlReplacements = map,
            TrustSignatures = true,
            RelaxTextureDomains = true,
            CacheKey = AuthlibClassTransformer.PatchRevision + "|ygg:" + root.ToLowerInvariant()
        };
    }

    public static AuthlibPatchProfile ForLoopbackBridge(string bridgeBaseUrl)
    {
        string baseUrl = bridgeBaseUrl.Trim().TrimEnd('/');
        Dictionary<string, string> map = CreateMojangUrlMap(
            baseUrl + "/sessionserver",
            baseUrl + "/authserver",
            baseUrl + "/api",
            baseUrl + "/minecraftservices",
            baseUrl + "/minecraft/client");

        return new AuthlibPatchProfile
        {
            UrlReplacements = map,
            TrustSignatures = true,
            RelaxTextureDomains = true,
            CacheKey = AuthlibClassTransformer.PatchRevision + "|bridge:" + baseUrl.ToLowerInvariant()
        };
    }

    private static Dictionary<string, string> CreateMojangUrlMap(
        string sessionserver,
        string authserver,
        string api,
        string services,
        string discovery) =>
        new(StringComparer.Ordinal)
        {
            ["https://sessionserver.mojang.com"] = sessionserver,
            ["http://sessionserver.mojang.com"] = sessionserver,
            ["https://sessionserver-staging.mojang.com"] = sessionserver,
            ["https://authserver.mojang.com"] = authserver,
            ["http://authserver.mojang.com"] = authserver,
            ["https://api.mojang.com"] = api,
            ["http://api.mojang.com"] = api,
            ["https://api.minecraftservices.com"] = services,
            ["https://api-staging.minecraftservices.com"] = services,
            ["https://discovery.minecraftservices.com/minecraft/client"] = discovery,
            ["https://discovery-staging.minecraftservices.com/minecraft/client"] = discovery,
            ["https://yggdrasil-auth-session-staging.mojang.zone"] = sessionserver
        };
}
