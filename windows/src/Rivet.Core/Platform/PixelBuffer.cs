// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Core.Platform;

/// <summary>
/// A plain 32-bit BGRA image (premultiplied alpha, top-down rows). It is the
/// hand-off format between platform capture code and the imaging library,
/// which converts it to and from SkiaSharp bitmaps.
/// </summary>
public sealed class PixelBuffer
{
    public PixelBuffer(int width, int height, byte[]? pixels = null, int stride = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        Width = width;
        Height = height;
        Stride = stride > 0 ? stride : width * 4;
        Pixels = pixels ?? new byte[Stride * height];
        if (Pixels.Length < Stride * height)
        {
            throw new ArgumentException("Pixel array is smaller than stride × height.", nameof(pixels));
        }
    }

    public int Width { get; }

    public int Height { get; }

    public int Stride { get; }

    public byte[] Pixels { get; }

    /// <summary>Display scale the pixels were captured at (1.0 = 96 DPI).</summary>
    public double Scale { get; init; } = 1.0;
}
