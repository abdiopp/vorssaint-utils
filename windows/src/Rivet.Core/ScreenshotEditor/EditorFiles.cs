// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;

namespace Rivet.Core.ScreenshotEditor;

/// <summary>File names for the editor's own outputs (Save As suggestion, drag-out, share, fallback save).</summary>
public static class EditorFiles
{
    /// <summary>
    /// <c>Screenshot yyyy-MM-dd at HH.mm.ss.png</c> with the localized prefix,
    /// invariant digits, a 24-hour clock and dots instead of colons.
    /// </summary>
    public static string DefaultName(DateTime now, string prefix) =>
        Sanitize($"{prefix} {now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} at {now.ToString("HH.mm.ss", CultureInfo.InvariantCulture)}") + ".png";

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Makes a file name stem safe on Windows: <c>/ \ : * ? " &lt; &gt; |</c> and
    /// control characters become dashes, trailing dots and spaces are trimmed
    /// and reserved device names get an underscore.
    /// </summary>
    public static string Sanitize(string stem)
    {
        var builder = new StringBuilder(stem.Length);
        foreach (var c in stem)
        {
            builder.Append(c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' || char.IsControl(c) ? '-' : c);
        }

        var result = builder.ToString().TrimEnd('.', ' ').Trim();
        if (result.Length == 0)
        {
            result = "Screenshot";
        }

        if (ReservedNames.Contains(result))
        {
            result += "_";
        }

        return result.Length > 180 ? result[..180] : result;
    }

    /// <summary>The name itself when free, else "name 2.png", "name 3.png" … up to 9999 (then the original).</summary>
    public static string UniquePath(string folder, string fileName, Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        var path = Path.Combine(folder, fileName);
        if (!exists(path))
        {
            return path;
        }

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var i = 2; i <= 9999; i++)
        {
            var candidate = Path.Combine(folder, $"{stem} {i}{extension}");
            if (!exists(candidate))
            {
                return candidate;
            }
        }

        return path;
    }

    /// <summary>Drag and share temp folders are named exactly <c>ScreenshotDrag-&lt;GUID&gt;</c>.</summary>
    public const string DragFolderPrefix = "ScreenshotDrag-";

    public static bool IsDragFolderName(string name) =>
        name.StartsWith(DragFolderPrefix, StringComparison.Ordinal)
        && Guid.TryParseExact(name[DragFolderPrefix.Length..], "D", out _);

    // ── QR payloads (spec 01 §6.18) ──────────────────────────────────────────

    /// <summary>
    /// Joins decoded codes in reading order: rowKey = (y_mid / H) · 50 (top-left
    /// origin); keys at least 0.5 apart sort by row, otherwise by minX.
    /// Whitespace-only payloads are dropped.
    /// </summary>
    public static string? JoinQrPayloads(IEnumerable<(string Payload, ImgRect Box)> codes, int imageHeight)
    {
        var list = codes.Where(c => !string.IsNullOrWhiteSpace(c.Payload)).ToList();
        list.Sort((a, b) =>
        {
            var ka = a.Box.MidY / Math.Max(1, imageHeight) * 50;
            var kb = b.Box.MidY / Math.Max(1, imageHeight) * 50;
            return Math.Abs(ka - kb) >= 0.5 ? ka.CompareTo(kb) : a.Box.MinX.CompareTo(b.Box.MinX);
        });
        return list.Count == 0 ? null : string.Join('\n', list.Select(c => c.Payload));
    }

    /// <summary>An openable link: trimmed, no whitespace inside, http or https with a host. Other schemes are never opened.</summary>
    public static Uri? OpenableUrl(string? payload)
    {
        var s = payload?.Trim();
        if (string.IsNullOrEmpty(s) || s.Any(char.IsWhiteSpace))
        {
            return null;
        }

        return Uri.TryCreate(s, UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
               && !string.IsNullOrEmpty(uri.Host)
            ? uri
            : null;
    }

    // ── Clipboard image scale (spec 01 §6.23) ───────────────────────────────

    /// <summary>
    /// <c>h = pixelW / pointW, v = pixelH / pointH</c>; when they agree within
    /// 5 % the scale is their mean if it lies in 0.5…4, otherwise 1.
    /// </summary>
    public static double InferScale(double pixelWidth, double pixelHeight, double pointWidth, double pointHeight)
    {
        if (!(pixelWidth > 0 && pixelHeight > 0 && pointWidth > 0 && pointHeight > 0)
            || !double.IsFinite(pixelWidth / pointWidth) || !double.IsFinite(pixelHeight / pointHeight))
        {
            return 1;
        }

        var h = pixelWidth / pointWidth;
        var v = pixelHeight / pointHeight;
        if (Math.Abs(h - v) > 0.05 * Math.Max(h, v))
        {
            return 1;
        }

        var scale = (h + v) / 2;
        return scale is >= 0.5 and <= 4 ? scale : 1;
    }

    /// <summary>Clipboard and file images over 60 megapixels are refused.</summary>
    public const long MaxEditablePixels = 60_000_000;

    /// <summary>"@1.5x" for the info chip (fractional scales shown; nothing at 1x).</summary>
    public static string? ScaleLabel(double scale)
    {
        if (!double.IsFinite(scale) || Math.Abs(scale - 1) < 0.005)
        {
            return null;
        }

        return "@" + Math.Round(scale, 2).ToString("0.##", CultureInfo.InvariantCulture) + "x";
    }
}
