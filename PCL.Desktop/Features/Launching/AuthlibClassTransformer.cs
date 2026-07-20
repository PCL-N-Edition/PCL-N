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
    public static byte[]? Transform(string className, byte[] originalBytes, AuthlibPatchProfile profile)
    {
        ArgumentNullException.ThrowIfNull(originalBytes);
        ArgumentNullException.ThrowIfNull(profile);

        string normalized = className.Replace('.', '/');
        if (!normalized.StartsWith("com/mojang/authlib/", StringComparison.Ordinal) &&
            !normalized.StartsWith("com/mojang/authlib/", StringComparison.OrdinalIgnoreCase))
        {
            // Zip entries use path separators without package dots.
            if (!normalized.Contains("com/mojang/authlib/", StringComparison.Ordinal))
                return null;
        }

        try
        {
            ClassReader reader = new(originalBytes);
            ClassWriter writer = new(ClassWriter.COMPUTE_MAXS);
            TransformVisitor visitor = new(writer, profile);
            reader.Accept(visitor, 0);
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
        public bool Modified { get; private set; }

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
            if (profile.RelaxTextureDomains && IsTextureDomainMethod(name, descriptor))
            {
                MethodVisitor? generated = Cv?.VisitMethod(access, name, descriptor, signature, exceptions);
                if (generated is not null)
                {
                    // Always return true — launcher / bridge already constrained sources.
                    generated.VisitCode();
                    generated.VisitInsn(Opcodes.ICONST_1);
                    generated.VisitInsn(Opcodes.IRETURN);
                    generated.VisitMaxs(1, CalculateLocals(access, descriptor));
                    generated.VisitEnd();
                }

                Modified = true;
                return null;
            }

            if (profile.TrustSignatures && IsBooleanTrustMethod(name, descriptor))
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

            if (profile.TrustSignatures &&
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
            return method is null ? null : new StringVisitor(method, this);
        }

        private static bool IsBooleanTrustMethod(string? name, string? descriptor)
        {
            if (descriptor?.EndsWith(")Z", StringComparison.Ordinal) != true)
                return false;
            return name is "isSignatureValid" or "verifyProperty" or "validateProperty" or "isValidSignature";
        }

        private static bool IsTextureDomainMethod(string? name, string? descriptor) =>
            (name is "isAllowedTextureDomain" or "isWhitelistedDomain" or "isDomainOnList") &&
            descriptor is not null &&
            descriptor.Contains("Ljava/lang/String;", StringComparison.Ordinal) &&
            descriptor.EndsWith(")Z", StringComparison.Ordinal);

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
        // Strip trailing /authserver if present — we rebuild path segments.
        if (root.EndsWith("/authserver", StringComparison.OrdinalIgnoreCase))
            root = root[..^"/authserver".Length].TrimEnd('/');

        Dictionary<string, string> map = new(StringComparer.Ordinal)
        {
            ["https://sessionserver.mojang.com"] = root + "/sessionserver",
            ["http://sessionserver.mojang.com"] = root + "/sessionserver",
            ["https://sessionserver-staging.mojang.com"] = root + "/sessionserver",
            ["https://authserver.mojang.com"] = root + "/authserver",
            ["http://authserver.mojang.com"] = root + "/authserver",
            ["https://api.mojang.com"] = root + "/api",
            ["http://api.mojang.com"] = root + "/api",
            // Services / discovery often stubbed by session bridge; still map for offline third-party.
            ["https://api.minecraftservices.com"] = root + "/minecraftservices",
            ["https://api-staging.minecraftservices.com"] = root + "/minecraftservices",
            ["https://discovery.minecraftservices.com/minecraft/client"] = root + "/minecraft/client",
            ["https://discovery-staging.minecraftservices.com/minecraft/client"] = root + "/minecraft/client",
            ["https://yggdrasil-auth-session-staging.mojang.zone"] = root + "/sessionserver"
        };

        return new AuthlibPatchProfile
        {
            UrlReplacements = map,
            TrustSignatures = true,
            RelaxTextureDomains = true,
            CacheKey = "ygg:" + root.ToLowerInvariant()
        };
    }

    public static AuthlibPatchProfile ForLoopbackBridge(string bridgeBaseUrl)
    {
        string baseUrl = bridgeBaseUrl.Trim().TrimEnd('/');
        Dictionary<string, string> map = new(StringComparer.Ordinal)
        {
            ["https://sessionserver.mojang.com"] = baseUrl + "/sessionserver",
            ["http://sessionserver.mojang.com"] = baseUrl + "/sessionserver",
            ["https://sessionserver-staging.mojang.com"] = baseUrl + "/sessionserver",
            ["https://api.mojang.com"] = baseUrl + "/api",
            ["https://authserver.mojang.com"] = baseUrl + "/authserver",
            ["https://api.minecraftservices.com"] = baseUrl + "/minecraftservices",
            ["https://api-staging.minecraftservices.com"] = baseUrl + "/minecraftservices",
            ["https://discovery.minecraftservices.com/minecraft/client"] = baseUrl + "/minecraft/client",
            ["https://discovery-staging.minecraftservices.com/minecraft/client"] = baseUrl + "/minecraft/client",
            ["https://yggdrasil-auth-session-staging.mojang.zone"] = baseUrl + "/sessionserver"
        };

        return new AuthlibPatchProfile
        {
            UrlReplacements = map,
            TrustSignatures = true,
            RelaxTextureDomains = true,
            CacheKey = "bridge:" + baseUrl.ToLowerInvariant()
        };
    }
}
