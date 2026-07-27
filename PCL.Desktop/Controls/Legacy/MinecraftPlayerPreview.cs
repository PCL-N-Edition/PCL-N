// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace PCL.Desktop.Controls.Legacy;

/// <summary>
/// Lightweight full-body Minecraft texture preview. It composes the canonical
/// skin regions directly with nearest-neighbour sampling and can place a cape
/// behind the model without requiring a browser or a 3D runtime.
/// </summary>
public sealed class MinecraftPlayerPreview : Control
{
    public static readonly StyledProperty<string> SkinAddressProperty =
        AvaloniaProperty.Register<MinecraftPlayerPreview, string>(
            nameof(SkinAddress),
            string.Empty);

    public static readonly StyledProperty<string> CapeAddressProperty =
        AvaloniaProperty.Register<MinecraftPlayerPreview, string>(
            nameof(CapeAddress),
            string.Empty);

    public static readonly StyledProperty<bool> IsSlimProperty =
        AvaloniaProperty.Register<MinecraftPlayerPreview, bool>(nameof(IsSlim));

    private Bitmap? _skin;
    private Bitmap? _cape;
    private int _loadVersion;

    public MinecraftPlayerPreview()
    {
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
        ClipToBounds = true;
        this.GetObservable(SkinAddressProperty).Subscribe(address =>
        {
            _ = ReloadAsync();
        });
        this.GetObservable(CapeAddressProperty).Subscribe(address =>
        {
            _ = ReloadAsync();
        });
        this.GetObservable(IsSlimProperty).Subscribe(_ => InvalidateVisual());
        DetachedFromVisualTree += (_, _) =>
        {
            Interlocked.Increment(ref _loadVersion);
            DisposeBitmaps();
        };
    }

    public string SkinAddress
    {
        get => GetValue(SkinAddressProperty);
        set => SetValue(SkinAddressProperty, value);
    }

    public string CapeAddress
    {
        get => GetValue(CapeAddressProperty);
        set => SetValue(CapeAddressProperty, value);
    }

    public bool IsSlim
    {
        get => GetValue(IsSlimProperty);
        set => SetValue(IsSlimProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Bounds.Width <= 0d || Bounds.Height <= 0d)
            return;

        const double logicalWidth = 16d;
        const double logicalHeight = 32d;
        double unit = Math.Min(Bounds.Width / logicalWidth, Bounds.Height / logicalHeight);
        double originX = (Bounds.Width - logicalWidth * unit) / 2d;
        double originY = (Bounds.Height - logicalHeight * unit) / 2d;

        IBrush shadow = new SolidColorBrush(Color.FromArgb(36, 0, 0, 0));
        context.DrawEllipse(
            shadow,
            null,
            new Point(Bounds.Width / 2d, originY + 31.4d * unit),
            6.2d * unit,
            1.25d * unit);

        if (_cape is not null)
        {
            int capeScale = Math.Max(1, _cape.PixelSize.Width / 64);
            DrawTexturePart(
                context,
                _cape,
                new PixelRect(1 * capeScale, 1 * capeScale, 10 * capeScale, 16 * capeScale),
                LogicalRect(originX, originY, unit, 3.15d, 7.1d, 9.7d, 17.2d));
        }

        if (_skin is null)
        {
            DrawFallback(context, originX, originY, unit);
            return;
        }

        int scale = Math.Max(1, _skin.PixelSize.Width / 64);
        bool modern = _skin.PixelSize.Height >= 64 * scale;
        double armWidth = IsSlim ? 3d : 4d;
        double leftArmX = IsSlim ? 1d : 0d;

        // Base layer: legs, torso, arms, head.
        DrawSkinPart(context, scale, 4, 20, 4, 12, originX, originY, unit, 4, 20, 4, 12);
        DrawSkinPart(
            context,
            scale,
            modern ? 20 : 4,
            modern ? 52 : 20,
            4,
            12,
            originX,
            originY,
            unit,
            8,
            20,
            4,
            12);
        DrawSkinPart(context, scale, 20, 20, 8, 12, originX, originY, unit, 4, 8, 8, 12);
        DrawSkinPart(
            context,
            scale,
            44,
            20,
            IsSlim ? 3 : 4,
            12,
            originX,
            originY,
            unit,
            12,
            8,
            armWidth,
            12);
        DrawSkinPart(
            context,
            scale,
            modern ? 36 : 44,
            modern ? 52 : 20,
            IsSlim ? 3 : 4,
            12,
            originX,
            originY,
            unit,
            leftArmX,
            8,
            armWidth,
            12);
        DrawSkinPart(context, scale, 8, 8, 8, 8, originX, originY, unit, 4, 0, 8, 8);

        if (!modern)
            return;

        // Outer layer. Transparent pixels naturally preserve the base texture.
        DrawSkinPart(context, scale, 4, 36, 4, 12, originX, originY, unit, 4, 20, 4, 12);
        DrawSkinPart(context, scale, 4, 52, 4, 12, originX, originY, unit, 8, 20, 4, 12);
        DrawSkinPart(context, scale, 20, 36, 8, 12, originX, originY, unit, 4, 8, 8, 12);
        DrawSkinPart(
            context,
            scale,
            44,
            36,
            IsSlim ? 3 : 4,
            12,
            originX,
            originY,
            unit,
            12,
            8,
            armWidth,
            12);
        DrawSkinPart(
            context,
            scale,
            52,
            52,
            IsSlim ? 3 : 4,
            12,
            originX,
            originY,
            unit,
            leftArmX,
            8,
            armWidth,
            12);
        DrawSkinPart(context, scale, 40, 8, 8, 8, originX, originY, unit, 4, 0, 8, 8);
    }

    private void DrawSkinPart(
        DrawingContext context,
        int scale,
        int sourceX,
        int sourceY,
        int sourceWidth,
        int sourceHeight,
        double originX,
        double originY,
        double unit,
        double x,
        double y,
        double width,
        double height)
    {
        if (_skin is null)
            return;

        PixelRect source = new(
            sourceX * scale,
            sourceY * scale,
            sourceWidth * scale,
            sourceHeight * scale);
        if (source.Right > _skin.PixelSize.Width || source.Bottom > _skin.PixelSize.Height)
            return;

        DrawTexturePart(
            context,
            _skin,
            source,
            LogicalRect(originX, originY, unit, x, y, width, height));
    }

    private static Rect LogicalRect(
        double originX,
        double originY,
        double unit,
        double x,
        double y,
        double width,
        double height) =>
        new(
            originX + x * unit,
            originY + y * unit,
            width * unit,
            height * unit);

    private static void DrawTexturePart(
        DrawingContext context,
        Bitmap bitmap,
        PixelRect source,
        Rect destination)
    {
        context.DrawImage(
            bitmap,
            new Rect(source.X, source.Y, source.Width, source.Height),
            destination);
    }

    private static void DrawFallback(
        DrawingContext context,
        double originX,
        double originY,
        double unit)
    {
        IBrush baseBrush = new SolidColorBrush(Color.FromRgb(111, 124, 145));
        IBrush lightBrush = new SolidColorBrush(Color.FromRgb(141, 155, 177));
        context.FillRectangle(lightBrush, LogicalRect(originX, originY, unit, 4, 0, 8, 8));
        context.FillRectangle(baseBrush, LogicalRect(originX, originY, unit, 4, 8, 8, 12));
        context.FillRectangle(baseBrush, LogicalRect(originX, originY, unit, 0, 8, 4, 12));
        context.FillRectangle(baseBrush, LogicalRect(originX, originY, unit, 12, 8, 4, 12));
        context.FillRectangle(baseBrush, LogicalRect(originX, originY, unit, 4, 20, 4, 12));
        context.FillRectangle(baseBrush, LogicalRect(originX, originY, unit, 8, 20, 4, 12));
    }

    private async Task ReloadAsync()
    {
        int version = Interlocked.Increment(ref _loadVersion);
        string skinAddress = SkinAddress.Trim();
        string capeAddress = CapeAddress.Trim();
        Task<byte[]?> skinTask = string.IsNullOrWhiteSpace(skinAddress)
            ? Task.FromResult<byte[]?>(null)
            : MySkin.LoadSkinBytesAsync(skinAddress);
        Task<byte[]?> capeTask = string.IsNullOrWhiteSpace(capeAddress)
            ? Task.FromResult<byte[]?>(null)
            : MySkin.LoadSkinBytesAsync(capeAddress);

        byte[]?[] bytes;
        try
        {
            bytes = await Task.WhenAll(skinTask, capeTask).ConfigureAwait(false);
        }
        catch
        {
            bytes = [null, null];
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (version != _loadVersion)
                return;

            Bitmap? nextSkin = CreateBitmap(bytes[0]);
            Bitmap? nextCape = CreateBitmap(bytes[1]);
            DisposeBitmaps();
            _skin = nextSkin;
            _cape = nextCape;
            InvalidateVisual();
        });
    }

    private static Bitmap? CreateBitmap(byte[]? bytes)
    {
        if (bytes is null || bytes.Length < 64)
            return null;

        try
        {
            using MemoryStream stream = new(bytes, writable: false);
            Bitmap bitmap = new(stream);
            if (bitmap.PixelSize.Width < 64 || bitmap.PixelSize.Height < 32)
            {
                bitmap.Dispose();
                return null;
            }

            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private void DisposeBitmaps()
    {
        _skin?.Dispose();
        _cape?.Dispose();
        _skin = null;
        _cape = null;
    }
}
