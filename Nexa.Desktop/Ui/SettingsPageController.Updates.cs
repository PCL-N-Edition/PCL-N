using Nexa.Services.Updates;
using Nexa.UI.Next;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;

namespace Nexa.Desktop.Ui;

internal sealed partial class SettingsPageController
{
    private static readonly XsrSemanticId CheckUpdate = XsrSemanticId.Parse("ui.settings.update.check");
    private static readonly XsrSemanticId DownloadUpdate = XsrSemanticId.Parse("ui.settings.update.download");
    private static readonly XsrSemanticId PortableUpdate = XsrSemanticId.Parse("ui.settings.update.portable");
    private static readonly XsrSemanticId UpdateNotes = XsrSemanticId.Parse("ui.settings.update.notes");
    private XsrQueryRouter? _updateQueries;
    private NexaUpdateQuery? _updateQuery;
    private Action<Uri>? _openUpdateLink;
    private Task<XsrResult<NexaUpdateStatus>>? _updateReading;
    private NexaUpdateOffer? _updateOffer;
    private string _updateStatus = "通过 Cloudflare 检查当前平台的新版本。";
    private readonly CancellationTokenSource _updateStop = new();

    internal void ConfigureUpdates(XsrQueryRouter queries, NexaUpdateQuery query, Action<Uri> open)
    { _updateQueries = queries; _updateQuery = query; _openUpdateLink = open; }

    private static bool IsUpdateIntent(XsrSemanticId id) => id == CheckUpdate || id == DownloadUpdate || id == PortableUpdate || id == UpdateNotes;
    private void HandleUpdateIntent(XsrSemanticId id)
    {
        if (id == CheckUpdate && _updateReading is null && _updateQuery is not null && _updateQueries?.TryResolve(NexaUpdateContract.Check, out var route) == true)
        {
            _updateStatus = "正在检查更新…";
            _updateReading = _updateQueries.QueryAsync<NexaUpdateQuery, NexaUpdateStatus>(route, _updateQuery, cancellationToken: _updateStop.Token).AsTask();
            WakeOnPlatformCompletion(_updateReading);
            BuildSections();
        }
        else if (_updateOffer is { } offer && id != CheckUpdate)
        {
            string url = id == DownloadUpdate ? offer.InstallerUrl : id == PortableUpdate ? offer.PortableUrl : offer.ReleaseUrl;
            try { _openUpdateLink?.Invoke(new Uri(url)); }
            catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
            { _feedback.Error("无法打开下载页面，请稍后重试。"); }
        }
    }

    private void UpdateReleaseCheck()
    {
        if (_updateReading is not { IsCompleted: true } task) return;
        _updateReading = null;
        _updateOffer = task.IsCompletedSuccessfully && task.Result.IsSuccess ? task.Result.Value!.Offer : null;
        _updateStatus = !task.IsCompletedSuccessfully || !task.Result.IsSuccess ? "暂时无法检查更新，请重试。"
            : _updateOffer is null ? "此通道没有可用的新版本。" : "新版本 " + _updateOffer.Version + " 已可下载。";
        if (_selected == "advanced") BuildSections();
    }

    private void BuildUpdateCard()
    {
        if (_updateQuery is null) return;
        var card = Stack(_sections, "SettingsUpdateCard", XsrUiOrientation.Vertical, 10);
        Style(card, White, Ink, 18);
        _shell.Tree.GetComponent<XsrUiElement>(card)!.Padding = new(20, 18, 20, 18);
        Text(card, "NexaCL " + _updateQuery.CurrentVersion, 19, Ink, 28, 600);
        Text(card, _updateStatus, 13, Muted, 22);
        var actions = Stack(card, "SettingsUpdateActions", XsrUiOrientation.Horizontal, 10);
        var check = ActionButton(actions, "SettingsCheckUpdate", _updateReading is null ? "检查更新" : "正在检查", CheckUpdate, 100);
        _shell.Tree.GetComponent<XsrUiInput>(check)!.Enabled = _updateReading is null;
        if (_updateOffer is not null)
        {
            ActionButton(actions, "SettingsDownloadUpdate", "下载安装包", DownloadUpdate, 112);
            ActionButton(actions, "SettingsPortableUpdate", "便携包", PortableUpdate, 80);
            ActionButton(actions, "SettingsUpdateNotes", "GitHub / 更新日志", UpdateNotes, 144);
        }
    }
}
