// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;

namespace Rivet.Core.Sound;

/// <summary>
/// Heuristics that turn endpoint facts into the classes the features use:
/// "is this headphones" (headphone guard, spec §6.8) and the priority tier of
/// a new device (spec §3.14). Windows reports the form factor and the device
/// enumerator; the name heuristic is the fallback.
/// </summary>
public static class DeviceClassifier
{
    private static readonly string[] HeadphoneWords =
    [
        "headphone", "headphones", "headset", "earphone", "earphones", "earbud", "earbuds",
        "airpod", "airpods", "earpod", "earpods", "galaxy buds", "pixel buds", "beats",
        "bose qc", "sony wh", "sony wf", "jabra", "soundcore",
    ];

    /// <summary>Enumerators of onboard audio buses (HD Audio codecs, Intel Smart Sound, AMD ACP).</summary>
    private static readonly string[] BuiltInEnumerators = ["HDAUDIO", "INTELAUDIO", "ACP", "SST"];

    private static readonly string[] BluetoothEnumerators = ["BTHENUM", "BTHLEDEVICE", "BTHHFENUM", "BTHLE"];

    /// <summary>Software devices: root-enumerated drivers (virtual cables, mixers, streaming tools).</summary>
    private static readonly string[] VirtualEnumerators = ["ROOT", "SW", "SWD"];

    private static readonly string[] VirtualNameHints =
    [
        "virtual", "vb-audio", "vb audio", "voicemeeter", "cable input", "cable output",
        "steam streaming", "nvidia broadcast", "obs", "loopback",
    ];

    /// <summary>
    /// Case- and diacritic-folded, lowercase, every run of characters outside
    /// [a-z0-9] replaced by one space (spec §6.8).
    /// </summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var decomposed = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        var lastWasSpace = false;
        foreach (var raw in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(raw) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            var c = char.ToLowerInvariant(raw);
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                builder.Append(c);
                lastWasSpace = false;
            }
            else if (!lastWasSpace)
            {
                builder.Append(' ');
                lastWasSpace = true;
            }
        }

        return builder.ToString();
    }

    /// <summary>The name heuristic of spec §6.8 over name, id and the data-source/description text.</summary>
    public static bool NameLooksLikeHeadphones(string? name, string? id, string? description)
    {
        var haystack = Normalize($"{name} {id} {description}");
        return HeadphoneWords.Any(word => haystack.Contains(word, StringComparison.Ordinal));
    }

    public static bool IsBluetoothEnumerator(string? enumerator) =>
        enumerator is not null && BluetoothEnumerators.Contains(enumerator.Trim().ToUpperInvariant());

    /// <summary>
    /// Headphones for the guard: a Headphones or Headset form factor, else the
    /// name heuristic. A Bluetooth device whose form factor Windows does not
    /// know counts as headphones (Bluetooth speakers report "Speakers").
    /// </summary>
    public static bool IsHeadphones(AudioFormFactor formFactor, bool bluetooth, string? name, string? id, string? description)
    {
        if (formFactor is AudioFormFactor.Headphones or AudioFormFactor.Headset)
        {
            return true;
        }

        if (NameLooksLikeHeadphones(name, id, description))
        {
            return true;
        }

        return bluetooth && formFactor == AudioFormFactor.Unknown;
    }

    /// <summary>
    /// Tier of a new device: onboard bus → built-in; software/root enumerated
    /// or a known virtual driver name → virtual; anything else (USB,
    /// Bluetooth, display audio) → hardware. Heuristic; reordering corrects it.
    /// </summary>
    public static AudioDeviceTier TierFor(string? enumerator, string? name)
    {
        var upper = enumerator?.Trim().ToUpperInvariant() ?? string.Empty;
        if (BuiltInEnumerators.Contains(upper))
        {
            return AudioDeviceTier.BuiltIn;
        }

        var normalizedName = " " + Normalize(name) + " ";
        if (VirtualEnumerators.Contains(upper) || VirtualNameHints.Any(h => normalizedName.Contains(" " + Normalize(h) + " ", StringComparison.Ordinal)))
        {
            return AudioDeviceTier.Virtual;
        }

        return AudioDeviceTier.Hardware;
    }

    public static AudioFormFactor ParseFormFactor(uint value) =>
        value <= (uint)AudioFormFactor.Unknown ? (AudioFormFactor)value : AudioFormFactor.Unknown;

    /// <summary>
    /// The bus enumerator inside a device instance or interface path, as an
    /// endpoint's property store reports its parent device:
    /// <c>{1}.HDAUDIO\FUNC_01&amp;…</c> → HDAUDIO, <c>{2}.\\?\usb#vid_…</c> → USB,
    /// <c>{1}.ROOT\MEDIA\0000</c> → ROOT. Null when the text has no such part.
    /// </summary>
    public static string? EnumeratorFromInstancePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var text = path.Trim();
        if (text.StartsWith('{') && text.IndexOf("}.", StringComparison.Ordinal) is var close and > 0)
        {
            text = text[(close + 2)..];
        }

        if (text.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            text = text[4..];
        }

        var end = text.IndexOfAny(['\\', '#']);
        var enumerator = end > 0 ? text[..end] : null;
        return enumerator is { Length: > 0 } && enumerator.All(c => char.IsLetterOrDigit(c) || c is '_' or '-')
            ? enumerator.ToUpperInvariant()
            : null;
    }
}
