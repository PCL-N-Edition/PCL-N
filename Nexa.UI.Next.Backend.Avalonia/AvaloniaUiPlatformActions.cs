using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace Nexa.UI.Next.Backend.Avalonia;

public enum AvaloniaUiInputKind { Keyboard, Mouse, Touch, Controller }

/// <summary>Explicit user-triggered OS effects. Does not know accounts or product layout.</summary>
public sealed class AvaloniaUiPlatformActions
{
    private TopLevel? _owner;
    internal void Attach(TopLevel owner)
    {
        _owner = owner;
        owner.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        owner.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    public event Action<AvaloniaUiInputKind>? InputObserved;

    public static TimeSpan DoubleClickInterval => TimeSpan.FromMilliseconds(OperatingSystem.IsWindows() ? GetDoubleClickTime() : 500);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    public void OpenDirectory(string directory)
    {
        if (_owner is null) throw new InvalidOperationException("The native window is not ready.");
        string fullPath = Path.GetFullPath(directory);
        if (!Directory.Exists(fullPath)) throw new DirectoryNotFoundException(fullPath);
        var start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo(fullPath) { UseShellExecute = true }
            : new ProcessStartInfo(OperatingSystem.IsMacOS() ? "/usr/bin/open" : "xdg-open") { UseShellExecute = false };
        if (!OperatingSystem.IsWindows()) start.ArgumentList.Add(fullPath);
        Process.Start(start)?.Dispose();
    }

    /// <summary>Called by a platform gamepad bridge after it has translated native input.</summary>
    public void ReportControllerInput() => InputObserved?.Invoke(AvaloniaUiInputKind.Controller);

    private void OnPointerPressed(object? sender, PointerPressedEventArgs args) => InputObserved?.Invoke(
        args.Pointer.Type is PointerType.Touch or PointerType.Pen
            ? AvaloniaUiInputKind.Touch
            : AvaloniaUiInputKind.Mouse);

    private void OnKeyDown(object? sender, KeyEventArgs args) => InputObserved?.Invoke(AvaloniaUiInputKind.Keyboard);

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

    public async Task<string?> PickDirectoryAsync()
    {
        if (_owner?.StorageProvider is not { } storage) throw new InvalidOperationException("The native folder picker is not ready.");
        IReadOnlyList<IStorageFolder> folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        { Title = "选择游戏目录", AllowMultiple = false });
        using IStorageFolder? folder = folders.Count > 0 ? folders[0] : null;
        return folder?.TryGetLocalPath();
    }

    public Task<string?> PickJavaFileAsync() => Dispatcher.UIThread.CheckAccess()
        ? PickJavaOnUiThreadAsync() : Dispatcher.UIThread.InvokeAsync(PickJavaOnUiThreadAsync);

    private async Task<string?> PickJavaOnUiThreadAsync()
    {
        if (_owner?.StorageProvider is not { } storage) throw new InvalidOperationException("The native file picker is not ready.");
        IReadOnlyList<IStorageFile> files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择 Java 可执行文件",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Java") { Patterns = OperatingSystem.IsWindows() ? ["java.exe", "javaw.exe"] : ["java"] }],
        });
        using IStorageFile? file = files.Count > 0 ? files[0] : null;
        return file?.TryGetLocalPath();
    }
}
