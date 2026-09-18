using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Nexa.Services.Minecraft.Install;

public sealed partial class ForgeInstallService
{
    internal static string OptiFineUrl(string game, string build)
    {
        string prefix = game + "_";
        if (!build.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidDataException("OptiFine 版本与 Minecraft 不匹配。");
        string[] parts = build[prefix.Length..].Split('_');
        if (parts.Length < 3) throw new InvalidDataException("OptiFine 版本标识无效。");
        return $"https://bmclapi2.bangbang93.com/optifine/{Uri.EscapeDataString(game)}/{Uri.EscapeDataString(string.Join('_', parts[..2]))}/{Uri.EscapeDataString(string.Join('_', parts[2..]))}";
    }

    private async Task<string?> CleanroomDigestAsync(string build, CancellationToken token)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/CleanroomMC/Cleanroom/releases/tags/" + Uri.EscapeDataString(build));
        message.Headers.UserAgent.ParseAdd("Nexa/2.0");
        using var response = await http.SendAsync(message, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var release = JsonNode.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false))!.AsObject();
        var asset = release["assets"]!.AsArray().OfType<JsonObject>().Single(a => a["name"]?.ToString() == $"cleanroom-{build}-installer.jar");
        string? digest = asset["digest"]?.ToString();
        if (digest is null) return null; // Older GitHub assets predate provider digests; HTTPS still applies.
        if (!digest.StartsWith("sha256:", StringComparison.Ordinal) || digest.Length != 71 || !digest[7..].All(char.IsAsciiHexDigit))
            throw new InvalidDataException("Cleanroom 发布校验信息无效。");
        return digest[7..];
    }

    private async Task<JsonObject> InstallOptiFineAsync(MinecraftLoaderInstallRequest request, string stage,
        string installer, IProgress<string>? progress, CancellationToken token)
    {
        // A source launcher calls the installer API with an explicit directory. Its GUI main
        // ignores command-line roots and may show Swing dialogs, even after a successful write.
        string helper = Path.Combine(stage, "NexaOptiFineInstall.java");
        await File.WriteAllTextAsync(helper, """
            import java.io.File;
            public class NexaOptiFineInstall {
                public static void main(String[] args) throws Exception {
                    Class.forName("optifine.Installer").getMethod("doInstall", File.class).invoke(null, new File(args[0]));
                }
            }
            """, token).ConfigureAwait(false);
        int major = Math.Max(17, request.VanillaJson["javaVersion"]?["majorVersion"]?.GetValue<int>() ?? 8);
        var javaRequest = request with { VanillaJson = new JsonObject { ["javaVersion"] = new JsonObject { ["majorVersion"] = major } } };
        string java = await (resolveJava ?? ResolveJavaAsync)(javaRequest, token).ConfigureAwait(false);
        var start = new ProcessStartInfo(java) { WorkingDirectory = stage, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { "-Djava.awt.headless=true", "-cp", installer, helper, stage }) start.ArgumentList.Add(arg);
        progress?.Report("正在安装 OptiFine");
        await (runProcess ?? RunAsync)(start, token).ConfigureAwait(false);
        var generated = Directory.GetFiles(Path.Combine(stage, "versions"), "*.json", SearchOption.AllDirectories)
            .Where(path => Path.GetFileNameWithoutExtension(path) != request.Game).ToArray();
        if (generated.Length != 1) throw new InvalidDataException("OptiFine 未生成唯一的版本清单。");
        return await Nexa.Services.Minecraft.Launch.MinecraftVersionJsonReader.ReadAsync(generated[0], token).ConfigureAwait(false);
    }
}
