using System.Net;
using System.Text.Json;
using Nexa.Xsr;

namespace Nexa.Services.Updates;

public sealed record NexaUpdateQuery(string CurrentVersion, string RuntimeIdentifier, string Channel);
public sealed record NexaUpdateOffer(string Version, string InstallerUrl, string PortableUrl, string ReleaseUrl);
public sealed record NexaUpdateStatus(NexaUpdateOffer? Offer);
public static class NexaUpdateContract
{
    public static readonly XsrSemanticId Check = XsrSemanticId.Parse("nexa.update.check");
}

/// <summary>Only discovers Nexa 2 releases; the legacy patch and distribution contracts are separate.</summary>
public sealed class NexaUpdateService(HttpClient http, Rollouts.RolloutService? rollouts = null)
{
    public Action<string, string>? Record { get; set; }
    private const string Origin = "https://api.pcln.top";
    public async Task<XsrResult<NexaUpdateStatus>> CheckAsync(NexaUpdateQuery query, CancellationToken token = default)
    {
        var result = await CheckCoreAsync(query, token).ConfigureAwait(false);
        Record?.Invoke("update.checked", result.IsSuccess ? "ok" : "failed");
        return result;
    }
    private async Task<XsrResult<NexaUpdateStatus>> CheckCoreAsync(NexaUpdateQuery query, CancellationToken token = default)
    {
        try
        {
            if (query.Channel is not ("stable" or "beta" or "alpha")
                || query.RuntimeIdentifier is not ("win-x64" or "win-arm64" or "linux-x64" or "linux-arm64" or "osx-x64" or "osx-arm64")
                || !UpdateVersion.TryParse(query.CurrentVersion, out var current) || current.Major != 2)
                throw new InvalidDataException("此构建不支持在线更新检查。");
            using var response = await http.GetAsync($"{Origin}/v2/updates/latest?channel={query.Channel}&rid={query.RuntimeIdentifier}{(rollouts is null ? "" : "&rollout=1")}", HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NoContent) return XsrResult.Success(new NexaUpdateStatus(null));
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            byte[] chunk = new byte[8192];
            int read;
            while ((read = await input.ReadAsync(chunk, token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > 128 * 1024) throw new InvalidDataException("更新信息超过大小限制。");
                buffer.Write(chunk, 0, read);
            }
            using var document = JsonDocument.Parse(buffer.ToArray());
            var root = document.RootElement;
            string version = root.GetProperty("version").GetString() ?? "";
            if (root.GetProperty("schemaVersion").GetInt32() != 2 || root.GetProperty("product").GetString() != "nexacl"
                || root.GetProperty("rid").GetString() != query.RuntimeIdentifier || root.GetProperty("channel").GetString() != query.Channel
                || !UpdateVersion.TryParse(version, out var candidate) || candidate.Major != 2 || candidate.ToString() != version
                || (query.Channel == "stable" && candidate.Stage != UpdateVersionStage.Stable)
                || (query.Channel == "beta" && candidate.Stage == UpdateVersionStage.Alpha))
                throw new InvalidDataException("更新信息与当前产品、通道或平台不匹配。");
            if (candidate.CompareTo(current) <= 0) return XsrResult.Success(new NexaUpdateStatus(null));
            if (rollouts is not null && !root.TryGetProperty("rollout", out _))
                throw new InvalidDataException("更新灰度策略缺失。");
            if (root.TryGetProperty("rollout", out var rule) && rule.ValueKind != JsonValueKind.Null)
            {
                if (rule.GetProperty("kind").GetString() != "update" || rule.GetProperty("target").GetString() != version)
                    throw new InvalidDataException("更新灰度规则与版本不匹配。");
                if (rollouts is null || !rollouts.Includes(rule, current.Stage == UpdateVersionStage.Ci ? "ci" : query.Channel, query.RuntimeIdentifier))
                    return XsrResult.Success(new NexaUpdateStatus(null));
            }
            string installer = Asset(root.GetProperty("installer"), version, query.RuntimeIdentifier);
            string portable = Asset(root.GetProperty("portable"), version, query.RuntimeIdentifier);
            string release = root.GetProperty("githubUrl").GetString() ?? "";
            string prefix = "https://github.com/PCL-N-Edition/PCL-N/releases/tag/";
            if (release != prefix + version && release != prefix + "v" + version) throw new InvalidDataException("更新说明地址无效。");
            return XsrResult.Success(new NexaUpdateStatus(new(version, installer, portable, release)));
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException or UnauthorizedAccessException or OperationCanceledException)
        {
            return XsrResult.Failure<NexaUpdateStatus>(new(XsrErrorKind.Unavailable, XsrSemanticId.Parse("nexa.update.unavailable"), "暂时无法检查更新，请稍后重试。"));
        }
    }

    private static string Asset(JsonElement asset, string version, string rid)
    {
        string name = asset.GetProperty("name").GetString() ?? "", url = asset.GetProperty("url").GetString() ?? "";
        if (!name.StartsWith($"Nexa-{version}-{rid}.", StringComparison.Ordinal) || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-'))
            || asset.GetProperty("size").GetInt64() <= 0
            || (url != $"{Origin}/v2/updates/releases/{version}/{name}" && url != $"{Origin}/v2/updates/releases/v{version}/{name}"))
            throw new InvalidDataException("更新安装包地址无效。");
        return url;
    }
}
