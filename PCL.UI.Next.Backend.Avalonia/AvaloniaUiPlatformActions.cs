using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;

namespace PCL.UI.Next.Backend.Avalonia;

/// <summary>Explicit user-triggered OS effects. Does not know accounts or product layout.</summary>
public sealed class AvaloniaUiPlatformActions
{
    private TopLevel? _owner;
    internal void Attach(TopLevel owner) => _owner = owner;

    public void OpenHttpsUri(Uri uri)
    {
        if (_owner is null) throw new InvalidOperationException("The native window is not ready.");
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0)
            throw new ArgumentException("Only HTTPS browser links are supported.", nameof(uri));
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })?.Dispose();
    }

    public Task CopyTextAsync(string text) => _owner?.Clipboard?.SetTextAsync(text)
        ?? throw new InvalidOperationException("The native clipboard is not ready.");

    public async Task<string?> PickJsonFileAsync()
    {
        if (_owner?.StorageProvider is not { } storage) throw new InvalidOperationException("The native file picker is not ready.");
        IReadOnlyList<IStorageFile> files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择要导入的档案文件",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }],
        });
        using IStorageFile? file = files.Count > 0 ? files[0] : null;
        return file?.TryGetLocalPath();
    }
}
