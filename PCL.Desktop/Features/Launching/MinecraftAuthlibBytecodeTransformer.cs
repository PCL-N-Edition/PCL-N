// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Jvm.NET.Abstractions;
using Jvm.NET.Asm;

namespace PCL.Desktop.Features.Launching;

/// <summary>
/// Routes authlib network constants through the session-scoped loopback bridge.
/// Signature results are trusted only after the bridge has independently validated
/// or restricted the texture response.
/// </summary>
internal sealed class MinecraftAuthlibBytecodeTransformer(string bridgeBaseUrl) : IBytecodeTransformer
{
    private readonly IReadOnlyDictionary<string, string> _replacements =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["https://sessionserver.mojang.com"] = bridgeBaseUrl + "/sessionserver",
            ["http://sessionserver.mojang.com"] = bridgeBaseUrl + "/sessionserver",
            ["https://api.mojang.com"] = bridgeBaseUrl + "/api",
            ["https://authserver.mojang.com"] = bridgeBaseUrl + "/authserver",
            ["https://api.minecraftservices.com"] = bridgeBaseUrl + "/minecraftservices",
            ["https://discovery.minecraftservices.com/minecraft/client"] = bridgeBaseUrl + "/minecraft/client",
            ["https://discovery-staging.minecraftservices.com/minecraft/client"] = bridgeBaseUrl + "/minecraft/client"
        };

    public string Name => "PCL-N authlib loopback bridge";

    public byte[]? Transform(string className, byte[] originalBytes)
    {
        string normalized = className.Replace('.', '/');
        if (!normalized.StartsWith("com/mojang/authlib/", StringComparison.Ordinal))
            return null;

        try
        {
            ClassReader reader = new(originalBytes);
            ClassWriter writer = new(ClassWriter.COMPUTE_MAXS);
            TransformVisitor visitor = new(writer, _replacements);
            reader.Accept(visitor, 0);
            return visitor.Modified ? writer.ToByteArray() : null;
        }
        catch
        {
            // A transformation failure must not corrupt the class. The bridge properties
            // still cover modern authlib versions, while the host log will expose launch failure.
            return null;
        }
    }

    private sealed class TransformVisitor(
        ClassVisitor next,
        IReadOnlyDictionary<string, string> replacements) : ClassVisitor(Opcodes.ASM9, next)
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
            if (IsTextureDomainMethod(name, descriptor))
            {
                MethodVisitor? generated = Cv?.VisitMethod(access, name, descriptor, signature, exceptions);
                if (generated is not null)
                {
                    generated.VisitCode();
                    generated.VisitVarInsn(Opcodes.ALOAD, (access & Opcodes.ACC_STATIC) == 0 ? 1 : 0);
                    generated.VisitLdcInsn("127.0.0.1");
                    generated.VisitMethodInsn(
                        Opcodes.INVOKEVIRTUAL,
                        "java/lang/String",
                        "contains",
                        "(Ljava/lang/CharSequence;)Z",
                        isInterface: false);
                    generated.VisitInsn(Opcodes.IRETURN);
                    generated.VisitMaxs(2, CalculateLocals(access, descriptor));
                    generated.VisitEnd();
                }
                Modified = true;
                return null;
            }

            if (IsBooleanTrustMethod(name, descriptor))
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

            if (string.Equals(name, "getPropertySignatureState", StringComparison.Ordinal) &&
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
            (name is "isAllowedTextureDomain" or "isWhitelistedDomain") &&
            string.Equals(descriptor, "(Ljava/lang/String;)Z", StringComparison.Ordinal);

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
            foreach ((string source, string target) in replacements)
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
