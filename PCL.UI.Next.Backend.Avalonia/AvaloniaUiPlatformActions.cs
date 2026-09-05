using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

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

    public Task CopyTextAsync(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        return Dispatcher.UIThread.CheckAccess()
            ? CopyTextOnUiThreadAsync(text)
            : Dispatcher.UIThread.InvokeAsync(() => CopyTextOnUiThreadAsync(text));
    }

    private async Task CopyTextOnUiThreadAsync(string text)
    {
        IClipboard clipboard = _owner?.Clipboard
            ?? throw new InvalidOperationException("The native clipboard is not ready.");
        Exception? failure = null;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                await clipboard.SetTextAsync(text).ConfigureAwait(true);
                try
                {
                    // Windows otherwise may retain only a delayed provider owned by this process.
                    // Flush is an enhancement, not the copy itself: some platform implementations
                    // cannot persist ownership even though SetTextAsync already succeeded.
                    await clipboard.FlushAsync().ConfigureAwait(true);
                }
                catch (Exception)
                {
                    // Keep the successfully written clipboard usable for this application session.
                }
                return;
            }
            catch (Exception error)
            {
                // Clipboard ownership is transiently exclusive on Windows. Keep retrying on the
                // UI dispatcher without blocking input or spawning concurrent writes.
                failure = error;
                if (attempt == 4)
                {
                    break;
                }
                await Task.Delay(40 << attempt).ConfigureAwait(true);
            }
        }

        throw new InvalidOperationException("The native clipboard rejected the text after retries.", failure);
    }

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
