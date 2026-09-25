using System.Buffers.Binary;
using System.Text;

namespace Nexa.Services.Minecraft.Install;

internal static class OptiFineInstallerMetadata
{
    // JVMS 4.4 / 4.5 / 4.7.2: only field ConstantValue attributes, never code execution.
    public static (string Game, string Build)? Read(byte[] data)
    {
        int offset = 0;
        ReadOnlySpan<byte> Take(int count)
        {
            if (count < 0 || count > data.Length - offset) throw new InvalidDataException("OptiFine 类文件已截断。");
            var result = data.AsSpan(offset, count); offset += count; return result;
        }
        ushort U2() => BinaryPrimitives.ReadUInt16BigEndian(Take(2));
        uint U4() => BinaryPrimitives.ReadUInt32BigEndian(Take(4));
        if (U4() != 0xCAFEBABE) return null;
        _ = Take(4);
        int count = U2();
        string?[] text = new string?[count];
        int[] strings = new int[count];
        for (int index = 1; index < count; index++)
        {
            byte tag = Take(1)[0];
            switch (tag)
            {
                case 1: text[index] = Encoding.UTF8.GetString(Take(U2())); break;
                case 8: strings[index] = U2(); break;
                case 7: case 16: case 19: case 20: _ = Take(2); break;
                case 3: case 4: case 9: case 10: case 11: case 12: case 17: case 18: _ = Take(4); break;
                case 5: case 6: _ = Take(8); if (++index >= count) throw new InvalidDataException("无效的常量池。"); break;
                case 15: _ = Take(3); break;
                default: throw new InvalidDataException("不支持的类文件常量。");
            }
        }
        string? Text(int index) => index > 0 && index < count ? text[index] : null;
        _ = Take(6);
        _ = Take(U2() * 2);
        int fields = U2();
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        for (int index = 0; index < fields; index++)
        {
            ushort flags = U2(); string? name = Text(U2()), type = Text(U2());
            int attributes = U2();
            for (int attribute = 0; attribute < attributes; attribute++)
            {
                string? kind = Text(U2()); uint length = U4();
                if (length > int.MaxValue) throw new InvalidDataException("类文件属性过大。");
                var content = Take((int)length);
                if (kind != "ConstantValue" || name is not ("MC_VERSION" or "OF_EDITION" or "OF_RELEASE")) continue;
                if (length != 2 || (flags & 0x0018) != 0x0018 || type != "Ljava/lang/String;") return null;
                int constant = BinaryPrimitives.ReadUInt16BigEndian(content);
                string? value = constant > 0 && constant < count ? Text(strings[constant]) : null;
                if (string.IsNullOrEmpty(value) || value.Length > 80
                    || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '_')
                    || !values.TryAdd(name, value)) return null;
            }
        }
        if (!values.TryGetValue("MC_VERSION", out string? game)
            || !values.TryGetValue("OF_EDITION", out string? edition)
            || !values.TryGetValue("OF_RELEASE", out string? release)) return null;
        return (game, game + "_" + edition + "_" + release);
    }
}
