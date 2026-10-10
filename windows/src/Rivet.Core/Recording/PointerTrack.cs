// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Text;

namespace Rivet.Core.Recording;

/// <summary>A pointer position at a time; x/y are normalized to the captured region (top-left origin, may leave 0…1).</summary>
public readonly record struct PointerSample(float Time, float X, float Y, ushort ShapeIndex, bool Visible);

/// <summary>A mouse button press (down) or release (up).</summary>
public readonly record struct PointerClick(float Time, bool IsDown);

/// <summary>A cursor image: PNG bytes and the hot spot in bitmap pixels.</summary>
public sealed record PointerShape(float HotX, float HotY, float Width, float Height, byte[] Png);

/// <summary>
/// The recorded pointer (the video never contains the cursor; the editor
/// redraws it). Binary, little-endian, magic "VRPT", version 4: the macOS v3
/// layout, with cursor bitmaps in physical pixels of the recorded monitor
/// (so they map 1:1 onto video pixels) and <see cref="DisplayScale"/> = that
/// monitor's DPI scale.
/// <code>
/// 0  magic "VRPT"          4  u16 version (4)     6  u16 reserved
/// 8  f32 systemScale       12 f32 displayScale    16 u32 sampleCount
/// 20 u32 clickCount        24 u32 shapeCount
/// 28 samples 16 B each: f32 time, f32 x, f32 y, u16 shapeIndex, u16 visible
///    clicks   8 B each: f32 time, u32 isDown
///    shapes:  f32 hotX, f32 hotY, f32 width, f32 height, u32 pngLength, PNG bytes
/// </code>
/// Decoding is defensive: wrong magic/version → empty; counts are capped by
/// the bytes present; non-finite records are skipped; a bad shape index falls
/// back to shape 0.
/// </summary>
public sealed class PointerTrack
{
    public const ushort CurrentVersion = 4;
    public const string FileName = "pointer.bin";
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("VRPT");

    public float SystemScale { get; init; } = 1;

    public float DisplayScale { get; init; } = 1;

    public IReadOnlyList<PointerSample> Samples { get; init; } = [];

    public IReadOnlyList<PointerClick> Clicks { get; init; } = [];

    public IReadOnlyList<PointerShape> Shapes { get; init; } = [];

    public bool IsEmpty => Samples.Count == 0;

    public static PointerTrack Empty { get; } = new();

    public byte[] Encode()
    {
        using var stream = new MemoryStream();
        Span<byte> buffer = stackalloc byte[28];
        Magic.CopyTo(buffer);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[4..], CurrentVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[6..], 0);
        BinaryPrimitives.WriteSingleLittleEndian(buffer[8..], SystemScale);
        BinaryPrimitives.WriteSingleLittleEndian(buffer[12..], DisplayScale);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[16..], (uint)Samples.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[20..], (uint)Clicks.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[24..], (uint)Shapes.Count);
        stream.Write(buffer);

        Span<byte> record = stackalloc byte[16];
        foreach (var s in Samples)
        {
            BinaryPrimitives.WriteSingleLittleEndian(record, s.Time);
            BinaryPrimitives.WriteSingleLittleEndian(record[4..], s.X);
            BinaryPrimitives.WriteSingleLittleEndian(record[8..], s.Y);
            BinaryPrimitives.WriteUInt16LittleEndian(record[12..], s.ShapeIndex);
            BinaryPrimitives.WriteUInt16LittleEndian(record[14..], (ushort)(s.Visible ? 1 : 0));
            stream.Write(record);
        }

        foreach (var c in Clicks)
        {
            BinaryPrimitives.WriteSingleLittleEndian(record, c.Time);
            BinaryPrimitives.WriteUInt32LittleEndian(record[4..], c.IsDown ? 1u : 0u);
            stream.Write(record[..8]);
        }

        Span<byte> shapeHeader = stackalloc byte[20];
        foreach (var shape in Shapes)
        {
            BinaryPrimitives.WriteSingleLittleEndian(shapeHeader, shape.HotX);
            BinaryPrimitives.WriteSingleLittleEndian(shapeHeader[4..], shape.HotY);
            BinaryPrimitives.WriteSingleLittleEndian(shapeHeader[8..], shape.Width);
            BinaryPrimitives.WriteSingleLittleEndian(shapeHeader[12..], shape.Height);
            BinaryPrimitives.WriteUInt32LittleEndian(shapeHeader[16..], (uint)shape.Png.Length);
            stream.Write(shapeHeader);
            stream.Write(shape.Png);
        }

        return stream.ToArray();
    }

    public static PointerTrack Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < 28 || !data[..4].SequenceEqual(Magic))
        {
            return Empty;
        }

        var version = BinaryPrimitives.ReadUInt16LittleEndian(data[4..]);
        if (version is not (3 or CurrentVersion))
        {
            return Empty;
        }

        var systemScale = BinaryPrimitives.ReadSingleLittleEndian(data[8..]);
        var displayScale = BinaryPrimitives.ReadSingleLittleEndian(data[12..]);
        var sampleCount = BinaryPrimitives.ReadUInt32LittleEndian(data[16..]);
        var clickCount = BinaryPrimitives.ReadUInt32LittleEndian(data[20..]);
        var shapeCount = BinaryPrimitives.ReadUInt32LittleEndian(data[24..]);
        var offset = 28;

        var samples = new List<PointerSample>();
        var available = (long)(data.Length - offset) / 16;
        for (long i = 0; i < Math.Min(sampleCount, available); i++, offset += 16)
        {
            var t = BinaryPrimitives.ReadSingleLittleEndian(data[offset..]);
            var x = BinaryPrimitives.ReadSingleLittleEndian(data[(offset + 4)..]);
            var y = BinaryPrimitives.ReadSingleLittleEndian(data[(offset + 8)..]);
            var shape = BinaryPrimitives.ReadUInt16LittleEndian(data[(offset + 12)..]);
            var visible = BinaryPrimitives.ReadUInt16LittleEndian(data[(offset + 14)..]) != 0;
            if (float.IsFinite(t) && float.IsFinite(x) && float.IsFinite(y))
            {
                samples.Add(new PointerSample(t, x, y, shape, visible));
            }
        }

        var clicks = new List<PointerClick>();
        available = (long)(data.Length - offset) / 8;
        for (long i = 0; i < Math.Min(clickCount, available); i++, offset += 8)
        {
            var t = BinaryPrimitives.ReadSingleLittleEndian(data[offset..]);
            var down = BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + 4)..]) != 0;
            if (float.IsFinite(t))
            {
                clicks.Add(new PointerClick(t, down));
            }
        }

        var shapes = new List<PointerShape>();
        for (long i = 0; i < shapeCount && offset + 20 <= data.Length; i++)
        {
            var hotX = BinaryPrimitives.ReadSingleLittleEndian(data[offset..]);
            var hotY = BinaryPrimitives.ReadSingleLittleEndian(data[(offset + 4)..]);
            var width = BinaryPrimitives.ReadSingleLittleEndian(data[(offset + 8)..]);
            var height = BinaryPrimitives.ReadSingleLittleEndian(data[(offset + 12)..]);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + 16)..]);
            offset += 20;
            if (length > data.Length - offset)
            {
                break;
            }

            var png = data.Slice(offset, (int)length).ToArray();
            offset += (int)length;
            if (length > 0 && float.IsFinite(hotX) && float.IsFinite(hotY) && width > 0 && height > 0)
            {
                shapes.Add(new PointerShape(hotX, hotY, width, height, png));
            }
        }

        if (shapes.Count > 0)
        {
            for (var i = 0; i < samples.Count; i++)
            {
                if (samples[i].ShapeIndex >= shapes.Count)
                {
                    samples[i] = samples[i] with { ShapeIndex = 0 };
                }
            }
        }

        return new PointerTrack
        {
            SystemScale = float.IsFinite(systemScale) && systemScale >= 1 ? systemScale : 1,
            DisplayScale = float.IsFinite(displayScale) && displayScale > 0 ? displayScale : 1,
            Samples = samples,
            Clicks = clicks,
            Shapes = shapes,
        };
    }

    public static PointerTrack Read(string path)
    {
        try
        {
            return File.Exists(path) ? Decode(File.ReadAllBytes(path)) : Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Empty;
        }
    }

    public void Write(string path) => File.WriteAllBytes(path, Encode());
}

/// <summary><c>typing.json</c>: <c>{"times":[…]}</c>, source seconds of non-repeat key-downs (never the keys).</summary>
public sealed record TypingTrack(IReadOnlyList<double> Times)
{
    public const string FileName = "typing.json";

    public static TypingTrack Read(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new TypingTrack([]);
            }

            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            return new TypingTrack(doc.RootElement.GetProperty("times").EnumerateArray()
                .Select(e => e.GetDouble()).Where(double.IsFinite).ToList());
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or KeyNotFoundException or InvalidOperationException or UnauthorizedAccessException)
        {
            return new TypingTrack([]);
        }
    }

    public void Write(string path) =>
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new { times = Times }));
}

/// <summary>Where takes live and how their folders are named.</summary>
public static class TakeFolders
{
    public const string Prefix = "Take-";

    public static string Root(App.AppPaths paths) => paths.LocalFolder("Recordings");

    public static string Create(App.AppPaths paths)
    {
        var folder = Path.Combine(Root(paths), Prefix + Guid.NewGuid().ToString("D").ToUpperInvariant());
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>Folders named <c>Take-&lt;UUID&gt;</c>; anything else in the root is ignored.</summary>
    public static IEnumerable<string> List(App.AppPaths paths) =>
        Directory.EnumerateDirectories(Root(paths), Prefix + "*")
            .Where(d => Guid.TryParse(Path.GetFileName(d)[Prefix.Length..], out _));
}
