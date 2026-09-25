using System.IO.Compression;
using System.Text;
using Nexa.Services.Minecraft.Install;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async ValueTask LocalOptiFineUsesFieldConstants()
    {
        string root = CreateTempDirectory();
        try
        {
            byte[] Config(string game, string release)
            {
                using var output = new MemoryStream();
                void U2(int value) { output.WriteByte((byte)(value >> 8)); output.WriteByte((byte)value); }
                void U4(int value) { U2(value >> 16); U2(value); }
                output.Write([0xca, 0xfe, 0xba, 0xbe]); U2(0); U2(52); U2(12);
                foreach (string value in new[] { "MC_VERSION", "OF_EDITION", "OF_RELEASE", "Ljava/lang/String;",
                    "ConstantValue", game, "HD_U", release })
                { output.WriteByte(1); byte[] bytes = Encoding.UTF8.GetBytes(value); U2(bytes.Length); output.Write(bytes); }
                foreach (int text in new[] { 6, 7, 8 }) { output.WriteByte(8); U2(text); }
                U2(1); U2(0); U2(0); U2(0); U2(3);
                for (int index = 1; index <= 3; index++)
                { U2(0x19); U2(index); U2(4); U2(1); U2(5); U4(2); U2(index + 8); }
                U2(0); U2(0); return output.ToArray();
            }
            foreach (string classPath in new[] { "Config.class", "net/optifine/Config.class" })
            {
                string jar = Path.Combine(root, Guid.NewGuid().ToString("N") + ".jar");
                using (var archive = ZipFile.Open(jar, ZipArchiveMode.Create))
                {
                    using (var config = archive.CreateEntry(classPath).Open())
                        config.Write(Config("1.12.2", "G6_pre1"));
                    archive.CreateEntry("optifine/Installer.class");
                }
                var result = await MinecraftLocalJarService.InspectAsync(jar);
                AssertEqual(InstallLoader.OptiFine, result.Loader!.Value);
                AssertEqual("1.12.2", result.Game!);
                AssertEqual("1.12.2_HD_U_G6_pre1", result.Build!);
            }
            AssertTrue(OptiFineInstallerMetadata.Read(Config("../escape", "G5")) is null);
            byte[] truncated = Config("1.12.2", "G5")[..20];
            try { OptiFineInstallerMetadata.Read(truncated); throw new InvalidOperationException("Truncated class accepted."); }
            catch (InvalidDataException) { }
            string renamed = Path.Combine(root, "OptiFine_1.12.2_HD_U_G5.jar");
            using (var archive = ZipFile.Open(renamed, ZipArchiveMode.Create)) archive.CreateEntry("unrelated.class");
            AssertTrue((await MinecraftLocalJarService.InspectAsync(renamed)).Loader is null);
        }
        finally { Directory.Delete(root, true); }
    }
}
