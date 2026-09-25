using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Nexa.UI.Next;

namespace Nexa.UI.Next.Backend.Avalonia;

public sealed partial class AvaloniaUiSceneSurface
{
    private PointerPressedEventArgs? _fileDragPress;
    private XsrUiEntityId _fileDragEntity;
    private Point _fileDragOrigin;
    private bool _fileDragActive;
    private bool _fileDragReleased;
    internal bool IsFileDragActive => _fileDragActive;
    internal Func<PointerPressedEventArgs, IDataTransfer, DragDropEffects, Task<DragDropEffects>> FileDragRunner { get; set; } = DragDrop.DoDragDropAsync;

    private void TrackFileDrag(PointerPressedEventArgs args, Point point)
    {
        _fileDragPress = null;
        if (args.Pointer.Type != PointerType.Mouse || !args.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _fileDragEntity = _shell.Renderer.FileDragTarget(new(point.X, point.Y));
        if (!_fileDragEntity.IsAssigned) return;
        _fileDragOrigin = point;
        _fileDragPress = args;
    }

    private bool MoveFileDrag(PointerEventArgs args, Point point)
    {
        if (_fileDragActive) return true;
        if (_fileDragPress is not { } press) return false;
        if (!args.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { _fileDragPress = null; return false; }
        if (Math.Abs(point.X - _fileDragOrigin.X) < 6 && Math.Abs(point.Y - _fileDragOrigin.Y) < 6) return true;
        var entity = _fileDragEntity;
        _fileDragPress = null;
        XsrUiFileDrag? transfer = _shell.Renderer.BeginFileDrag(entity);
        if (transfer is null) return false;
        _fileDragActive = true;
        _fileDragReleased = false;
        _textSelecting = default;
        press.Pointer.Capture(null);
        CommitScene();
        _ = RunFileDragAsync(press, entity, transfer);
        return true;
    }

    private async Task RunFileDragAsync(PointerPressedEventArgs press, XsrUiEntityId entity, XsrUiFileDrag transfer)
    {
        try
        {
            var owner = TopLevel.GetTopLevel(this);
            if (owner is null) return;
            var data = new DataTransfer();
            foreach (string path in transfer.Paths)
            {
                if (!Path.IsPathFullyQualified(path)) return;
                var folder = await owner.StorageProvider.TryGetFolderFromPathAsync(new Uri(path));
                if (folder is null) return;
                data.Add(DataTransferItem.CreateFile(folder));
            }
            if (_disposed || _fileDragReleased) return;
            XsrUiFileDrag? latest = _shell.Renderer.BeginFileDrag(entity);
            if (latest is null || !latest.Paths.SequenceEqual(transfer.Paths)) return;
            XsrUiFileDragEffects allowed = transfer.Effects & latest.Effects;
            DragDropEffects effects = DragDropEffects.None;
            if (allowed.HasFlag(XsrUiFileDragEffects.Copy)) effects |= DragDropEffects.Copy;
            if (allowed.HasFlag(XsrUiFileDragEffects.Move)) effects |= DragDropEffects.Move;
            if (allowed.HasFlag(XsrUiFileDragEffects.Link)) effects |= DragDropEffects.Link;
            if (effects == DragDropEffects.None) return;
            await FileDragRunner(press, data, effects);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            // A missing directory or unavailable native target cannot become a version click.
            System.Diagnostics.Trace.TraceWarning("Native directory drag failed: {0}", error.GetType().Name);
        }
        finally
        {
            _fileDragActive = false;
            if (!_disposed) { _shell.Renderer.CompleteFileDrag(entity, transfer); CommitScene(); }
        }
    }
}
