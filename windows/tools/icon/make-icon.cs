// SPDX-License-Identifier: GPL-3.0-or-later
// Renders the working-codename app icon (a rivet head on a gradient tile) into
// src/Rivet.App/Assets/app.ico and app-icon.png. Replace with the real logo
// once the app has a name.   Run:  dotnet run tools/icon/make-icon.cs
#:package SkiaSharp
using SkiaSharp;

var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory.Contains("artifacts") ? Directory.GetCurrentDirectory() : Directory.GetCurrentDirectory()));
var assets = Path.Combine(root, "src", "Rivet.App", "Assets");
Directory.CreateDirectory(assets);

byte[] Render(int size)
{
    using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Premul));
    var c = surface.Canvas;
    c.Clear(SKColors.Transparent);
    var s = size / 256f;
    var tile = new SKRoundRect(new SKRect(8 * s, 8 * s, 248 * s, 248 * s), 56 * s);
    using (var fill = new SKPaint { IsAntialias = true })
    {
        fill.Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(size, size),
            [new SKColor(0x25, 0x63, 0xEB), new SKColor(0x06, 0xB6, 0xD4)], SKShaderTileMode.Clamp);
        c.DrawRoundRect(tile, fill);
    }

    using (var ring = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(1.5f, 22 * s), Color = SKColors.White })
    {
        c.DrawCircle(128 * s, 128 * s, 70 * s, ring);
    }

    using (var dot = new SKPaint { IsAntialias = true, Color = SKColors.White })
    {
        c.DrawCircle(128 * s, 128 * s, 30 * s, dot);
    }

    using var image = surface.Snapshot();
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    return data.ToArray();
}

int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
var pngs = sizes.Select(Render).ToArray();
using (var ico = new BinaryWriter(File.Create(Path.Combine(assets, "app.ico"))))
{
    ico.Write((ushort)0); ico.Write((ushort)1); ico.Write((ushort)sizes.Length);
    var offset = 6 + (16 * sizes.Length);
    for (var i = 0; i < sizes.Length; i++)
    {
        ico.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
        ico.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
        ico.Write((byte)0); ico.Write((byte)0);
        ico.Write((ushort)1); ico.Write((ushort)32);
        ico.Write(pngs[i].Length); ico.Write(offset);
        offset += pngs[i].Length;
    }

    foreach (var png in pngs)
    {
        ico.Write(png);
    }
}

File.WriteAllBytes(Path.Combine(assets, "app-icon.png"), pngs[^1]);
Console.WriteLine($"Wrote {Path.Combine(assets, "app.ico")} and app-icon.png");
