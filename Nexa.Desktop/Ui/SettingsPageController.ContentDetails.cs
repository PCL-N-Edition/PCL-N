using System.Text;
using Nexa.Services.Minecraft.Management;
using Nexa.UI.Next;

namespace Nexa.Desktop.Ui;

internal sealed partial class SettingsPageController
{
    private void RegisterContentAction(XsrUiEntityId entity, Action action)
    {
        _managementActions[entity] = action;
        _contentActions.Add(entity);
    }

    private void OpenContentDetail(InstanceContentEntry item)
    {
        _contentReturnOffset = _shell.Tree.GetComponent<XsrUiScroll>(_sections)!.OffsetY;
        _contentDetail = item;
        BuildSections(true);
        _shell.Tree.GetComponent<XsrUiScroll>(_sections)!.OffsetY = 0;
    }

    private void BuildContentDetail(InstanceContentEntry item)
    {
        var header = Stack(_sections, "ManagementDetailToolbar", XsrUiOrientation.Horizontal, 10);
        ManagementButton(header, "返回列表", () =>
        {
            _contentDetail = null;
            _scrollPositions[_selected] = _contentReturnOffset;
            BuildSections(true);
        }, 86);
        var spacer = Element(header, "DetailToolbarSpace", XsrUiSemanticRole.None, null);
        _shell.Tree.GetComponent<XsrUiElement>(spacer)!.Weight = 1;
        if (item.Enabled is { } enabled)
        {
            var toggle = ActionButton(header, "ManagementModToggle." + item.Name, enabled ? "停用" : "启用", ManagementAction, 64);
            RegisterContentAction(toggle, () => ToggleMod(item));
        }
        ManagementButton(header, "移至已移除内容", () => RemoveContent(item), 116);
        var surface = Stack(_sections, "ManagementContentDetail", XsrUiOrientation.Vertical, 0);
        Style(surface, White, Ink, 16);
        var hero = Stack(surface, "ManagementContentDetailBody", XsrUiOrientation.Vertical, 16);
        _shell.Tree.GetComponent<XsrUiElement>(hero)!.Padding = new(24, 24, 24, 24);
        if (_selected == "screenshots")
        {
            ContentImage(hero, item, null, Math.Min(420, _shell.Renderer.Viewport.Height * .5));
            ContentName(hero, item.Name, 19, 32);
            if (item.Icon is { } image) ManagementFactIn(hero, "图像尺寸", $"{image.Width} × {image.Height}");
            else Text(hero, "暂时无法预览此图片，可在文件夹中查看原文件。", 13, Muted, 28);
        }
        else
        {
            var identity = Stack(hero, "ContentDetailIdentity", XsrUiOrientation.Horizontal, 18);
            ContentImage(identity, item, 72, 72);
            var titles = Stack(identity, "ContentDetailTitles", XsrUiOrientation.Vertical, 4);
            _shell.Tree.GetComponent<XsrUiElement>(titles)!.Weight = 1;
            if (_selected == "resourcepacks") ContentName(titles, ResourcePackTitle(item), 21, 34);
            else ContentName(titles, item.DisplayName.Length > 0 ? item.DisplayName : item.Name, 21, 34);
            Text(titles, Pages.First(page => page.Id == _selected).Label + (item.Enabled is { } active ? active ? " · 已启用" : " · 已停用" : ""), 13, Muted, 26);
            if (item.Description.Length > 0)
            {
                ContentName(hero, item.Description, 14, null, maxLines: 0);
            }
        }
        if (_selected == "mods")
        {
            if (item.PackageProblem.Length > 0) ManagementFactIn(hero, "包检测", item.PackageProblem);
            ManagementFactIn(hero, "更新", item.UpdateAvailable == true ? "可更新至 " + item.UpdateVersion
                : item.UpdateAvailable == false ? "未发现更新" : "尚未检测或未识别");
        }
        ManagementFactIn(hero, "文件名", item.Name);
        if (item.Version.Length > 0) ManagementFactIn(hero, _selected == "resourcepacks" ? "资源包格式" : "版本", item.Version);
        ManagementFactIn(hero, "大小", item.Size is { } bytes ? FormatContentSize(bytes) : "文件夹");
        ManagementFactIn(hero, "修改时间", new DateTime(item.ModifiedUtcTicks, DateTimeKind.Utc).ToLocalTime().ToString("yyyy/MM/dd HH:mm", System.Globalization.CultureInfo.CurrentCulture));
        string? directory = _management?.Pages.FirstOrDefault(page => page.Id == _selected)?.Directory;
        if (directory is not null)
        {
            ManagementFactIn(hero, "所在目录", directory);
            ManagementButton(hero, item.IsDirectory ? "打开内容文件夹" : "打开所在文件夹", () => OpenContentDirectory(item.IsDirectory ? System.IO.Path.Combine(directory, item.Name) : directory), 128);
        }
    }

    private void ManagementFactIn(XsrUiEntityId parent, string label, string value)
    {
        var row = Stack(parent, "ContentDetailFact", XsrUiOrientation.Horizontal, 20);
        _shell.Tree.GetComponent<XsrUiElement>(Text(row, label, 12, Muted, 28))!.Width = 90;
        _shell.Tree.GetComponent<XsrUiElement>(Text(row, value, 13, Ink, 28))!.Weight = 1;
    }

    private static string FormatContentSize(long bytes) => bytes >= 1024 * 1024
        ? $"{bytes / (1024d * 1024):N1} MB" : $"{bytes / 1024d:N1} KB";

    private void ContentImage(XsrUiEntityId parent, InstanceContentEntry item, double? width, double height)
    {
        string placeholder = _selected switch { "mods" => "lucide/blocks", "resourcepacks" => "nexa/content-package", "shaderpacks" => "nexa/content-shader", "saves" => "nexa/content-world", _ => "nexa/content-image" };
        var entity = Element(parent, "ManagementContentIcon", XsrUiSemanticRole.None, null, width, height);
        Style(entity, new(242, 245, 249), Muted, 10);
        var image = new XsrUiImage(placeholder);
        if (item.Icon is { } png)
            image.Raster = new(png, [new(new(0, 0, png.Width, png.Height), new(0, 0, 1, 1))]) { FitToBounds = true };
        _shell.Tree.SetComponent(entity, image);
    }

    private void BuildScreenshotCard(XsrUiEntityId parent, InstanceContentEntry item)
    {
        var card = Stack(parent, "ManagementScreenshot." + item.Name, XsrUiOrientation.Vertical, 8);
        var layout = _shell.Tree.GetComponent<XsrUiElement>(card)!;
        layout.Weight = 1; layout.Height = 198;
        Style(card, White, Ink, 14);
        _shell.Tree.SetComponent(card, new XsrUiSemantic(XsrUiSemanticRole.Button, "查看截图 " + item.Name));
        _shell.Tree.SetComponent(card, new XsrUiInput { Clickable = true, Focusable = true });
        _shell.Tree.SetComponent(card, new XsrUiCommandBinding(ManagementAction));
        RegisterContentAction(card, () => OpenContentDetail(item));
        var body = Stack(card, "ManagementScreenshotBody", XsrUiOrientation.Vertical, 8);
        _shell.Tree.GetComponent<XsrUiElement>(body)!.Padding = new(12, 12, 12, 12);
        ContentImage(body, item, null, 140);
        Text(body, item.Name, 12, Ink, 26);
    }

    private static string ResourcePackTitle(InstanceContentEntry item) =>
        !item.IsDirectory && item.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? item.Name[..^4] : item.Name;

    private static bool IsContentFormatCode(char code) => "0123456789abcdefklmnor".Contains(char.ToLowerInvariant(code));

    private static string StripContentFormatting(string value)
    {
        if (!value.Contains('§')) return value;
        var text = new StringBuilder();
        for (int index = 0; index < value.Length; index++)
        {
            if (value[index] == '§' && index + 1 < value.Length && IsContentFormatCode(value[index + 1])) { index++; continue; }
            text.Append(value[index]);
        }
        return text.ToString();
    }

    // Legacy Minecraft formatting is presentation only; raw control codes never become labels.
    private void ContentName(XsrUiEntityId parent, string value, double size, double? height, int maxLines = 1, XsrUiColor? foreground = null)
    {
        XsrUiColor color = foreground ?? Ink;
        bool bold = false, italic = false, underline = false, strike = false;
        var plain = new StringBuilder();
        List<XsrUiTextRun> runs = [];
        int start = 0;
        void Flush()
        {
            if (plain.Length > start) runs.Add(new(start, plain.Length - start, color, bold, italic, underline, strike));
            start = plain.Length;
        }
        for (int index = 0; index < value.Length && index < 4096; index++)
        {
            char character = value[index];
            if (character != '§' || index + 1 >= value.Length || !IsContentFormatCode(value[index + 1])) { plain.Append(character); continue; }
            Flush();
            char code = char.ToLowerInvariant(value[++index]);
            int digit = "0123456789abcdef".IndexOf(code);
            if (digit >= 0)
            {
                int bright = (digit & 8) != 0 ? 85 : 0;
                color = new((byte)(((digit & 4) != 0 ? 170 : 0) + bright), (byte)(((digit & 2) != 0 ? 170 : 0) + bright), (byte)(((digit & 1) != 0 ? 170 : 0) + bright));
                if (digit == 6) color = new(255, 170, 0);
                bold = italic = underline = strike = false;
            }
            else if (code == 'l') bold = true;
            else if (code == 'o') italic = true;
            else if (code == 'n') underline = true;
            else if (code == 'm') strike = true;
            else if (code == 'r') { color = foreground ?? Ink; bold = italic = underline = strike = false; }
        }
        Flush();
        var entity = Text(parent, plain.ToString(), size, foreground ?? Ink, height ?? 0, maxLines == 1 ? 550 : 400);
        _shell.Tree.GetComponent<XsrUiElement>(entity)!.Height = height;
        var text = _shell.Tree.GetComponent<XsrUiText>(entity)!;
        text.Runs = runs.AsReadOnly();
        text.MaxLines = maxLines;
        _shell.Tree.GetComponent<XsrUiVisualStyle>(entity)!.WrapText = maxLines != 1;
    }
}
