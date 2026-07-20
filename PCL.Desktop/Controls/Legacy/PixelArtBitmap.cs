// Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace PCL.Desktop.Controls.Legacy;

/// <summary>
/// Nearest-neighbor crop/upscale for Minecraft skin faces and other pixel art.
/// Avoids GPU bilinear softening when an 8×8 atlas region is shown at 48–64 CSS pixels.
/// </summary>
internal static class PixelArtBitmap
{
    /// <summary>
    /// Crops <paramref name="source"/> and integer-upscales with nearest-neighbor so the
    /// result is at least <paramref name="minDisplaySize"/> on the short edge.
    /// </summary>
    public static WriteableBitmap? CropAndUpscale(
        Bitmap source,
        int srcX,
        int srcY,
        int srcWidth,
        int srcHeight,
        int minDisplaySize = 48)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (srcWidth <= 0 || srcHeight <= 0 || minDisplaySize <= 0)
            return null;

        PixelSize size = source.PixelSize;
        if (srcX < 0 || srcY < 0 || srcX + srcWidth > size.Width || srcY + srcHeight > size.Height)
            return null;

        int factor = Math.Max(1, (int)Math.Ceiling(minDisplaySize / (double)Math.Min(srcWidth, srcHeight)));
        int dstWidth = srcWidth * factor;
        int dstHeight = srcHeight * factor;

        int srcStride = srcWidth * 4;
        byte[] srcPixels = new byte[srcStride * srcHeight];
        GCHandle handle = GCHandle.Alloc(srcPixels, GCHandleType.Pinned);
        try
        {
            source.CopyPixels(
                new PixelRect(srcX, srcY, srcWidth, srcHeight),
                handle.AddrOfPinnedObject(),
                srcPixels.Length,
                srcStride);
        }
        finally
        {
            handle.Free();
        }

        WriteableBitmap target = new(
            new PixelSize(dstWidth, dstHeight),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Unpremul);

        using (ILockedFramebuffer fb = target.Lock())
        {
            int dstStride = fb.RowBytes;
            byte[] row = new byte[dstWidth * 4];
            for (int y = 0; y < dstHeight; y++)
            {
                int srcRow = (y / factor) * srcStride;
                for (int x = 0; x < dstWidth; x++)
                {
                    int srcCol = (x / factor) * 4;
                    int si = srcRow + srcCol;
                    int di = x * 4;
                    row[di] = srcPixels[si];
                    row[di + 1] = srcPixels[si + 1];
                    row[di + 2] = srcPixels[si + 2];
                    row[di + 3] = srcPixels[si + 3];
                }

                Marshal.Copy(row, 0, IntPtr.Add(fb.Address, y * dstStride), row.Length);
            }
        }

        return target;
    }

    /// <summary>Integer nearest-neighbor upscale of an entire bitmap (no crop).</summary>
    public static WriteableBitmap Upscale(Bitmap source, int minDisplaySize = 48)
    {
        ArgumentNullException.ThrowIfNull(source);
        WriteableBitmap? result = CropAndUpscale(
            source,
            0,
            0,
            source.PixelSize.Width,
            source.PixelSize.Height,
            minDisplaySize);
        return result ?? throw new InvalidOperationException("Pixel art upscale failed.");
    }
}
