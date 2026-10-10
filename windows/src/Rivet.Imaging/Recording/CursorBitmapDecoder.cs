// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Platform;

namespace Rivet.Imaging.Recording;

/// <summary>
/// The raw bitmaps of a Windows cursor, as <c>GetIconInfoEx</c> + <c>GetDIBits</c>
/// return them converted to 32 bits per pixel, top-down.
/// </summary>
public sealed record CursorBitmapSource
{
    public required int Width { get; init; }

    /// <summary>Height of the cursor picture (not of the doubled monochrome mask).</summary>
    public required int Height { get; init; }

    /// <summary>The colour bitmap (BGRA), or null for a monochrome cursor.</summary>
    public byte[]? Color { get; init; }

    /// <summary>
    /// The mask as 32-bit pixels (white = bit set). Colour cursors: the AND
    /// mask, <see cref="Height"/> rows. Monochrome cursors: AND mask rows, then
    /// XOR mask rows (2 × <see cref="Height"/>).
    /// </summary>
    public required byte[] Mask { get; init; }
}

/// <summary>
/// Turns cursor bitmaps into premultiplied BGRA, the form the pointer track
/// stores. Windows cursors come in three kinds:
/// <list type="bullet">
/// <item>32-bit colour with alpha: used as is;</item>
/// <item>colour without alpha: opaque where the AND mask is clear,
/// transparent where it is set over black;</item>
/// <item>monochrome (AND + XOR masks): black, white, transparent, or
/// <em>inverted screen</em>.</item>
/// </list>
/// Inverting pixels (the classic I-beam, some crosshairs) cannot be stored as
/// colour plus alpha, so they become a black glyph with a 1-pixel white
/// outline, readable on any background (spec 02 §3.12).
/// </summary>
public static class CursorBitmapDecoder
{
    public const int MaxSide = 1024;

    public static PixelBuffer? Decode(CursorBitmapSource source)
    {
        var w = source.Width;
        var h = source.Height;
        if (w <= 0 || h <= 0 || w > MaxSide || h > MaxSide)
        {
            return null;
        }

        var pixels = w * h;
        var monochrome = source.Color is null;
        var maskRows = monochrome ? h * 2 : h;
        if (source.Mask.Length < w * maskRows * 4 || (source.Color is { } color && color.Length < pixels * 4))
        {
            return null;
        }

        var output = new byte[pixels * 4];
        var ink = new bool[pixels];
        var anyInk = false;

        if (source.Color is { } bgra)
        {
            var hasAlpha = false;
            for (var i = 3; i < pixels * 4; i += 4)
            {
                if (bgra[i] != 0)
                {
                    hasAlpha = true;
                    break;
                }
            }

            for (var i = 0; i < pixels; i++)
            {
                var o = i * 4;
                if (hasAlpha)
                {
                    var a = bgra[o + 3];
                    output[o] = Premultiply(bgra[o], a);
                    output[o + 1] = Premultiply(bgra[o + 1], a);
                    output[o + 2] = Premultiply(bgra[o + 2], a);
                    output[o + 3] = a;
                    continue;
                }

                var and = IsSet(source.Mask, i);
                var black = bgra[o] == 0 && bgra[o + 1] == 0 && bgra[o + 2] == 0;
                if (!and)
                {
                    output[o] = bgra[o];
                    output[o + 1] = bgra[o + 1];
                    output[o + 2] = bgra[o + 2];
                    output[o + 3] = 255;
                }
                else if (!black)
                {
                    ink[i] = anyInk = true;
                }
            }
        }
        else
        {
            for (var i = 0; i < pixels; i++)
            {
                var and = IsSet(source.Mask, i);
                var xor = IsSet(source.Mask, pixels + i);
                var o = i * 4;
                switch (and, xor)
                {
                    case (false, false):
                        output[o + 3] = 255; // black
                        break;
                    case (false, true):
                        output[o] = output[o + 1] = output[o + 2] = output[o + 3] = 255; // white
                        break;
                    case (true, true):
                        ink[i] = anyInk = true; // inverts the screen
                        break;
                }
            }
        }

        if (anyInk)
        {
            Outline(output, ink, w, h);
        }

        return new PixelBuffer(w, h, output);
    }

    /// <summary>Ink pixels become opaque black; transparent pixels touching ink become opaque white.</summary>
    private static void Outline(byte[] output, bool[] ink, int w, int h)
    {
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var i = (y * w) + x;
                var o = i * 4;
                if (ink[i])
                {
                    output[o] = output[o + 1] = output[o + 2] = 0;
                    output[o + 3] = 255;
                    continue;
                }

                if (output[o + 3] != 0 || !TouchesInk(ink, w, h, x, y))
                {
                    continue;
                }

                output[o] = output[o + 1] = output[o + 2] = output[o + 3] = 255;
            }
        }
    }

    private static bool TouchesInk(bool[] ink, int w, int h, int x, int y)
    {
        for (var dy = -1; dy <= 1; dy++)
        {
            var ny = y + dy;
            if (ny < 0 || ny >= h)
            {
                continue;
            }

            for (var dx = -1; dx <= 1; dx++)
            {
                var nx = x + dx;
                if ((dx != 0 || dy != 0) && nx >= 0 && nx < w && ink[(ny * w) + nx])
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsSet(byte[] mask, int pixel) => mask[(pixel * 4) + 2] > 127;

    private static byte Premultiply(byte value, byte alpha) => (byte)(((value * alpha) + 127) / 255);
}
