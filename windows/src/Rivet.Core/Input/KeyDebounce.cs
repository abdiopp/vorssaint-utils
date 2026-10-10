// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;
using Rivet.Core.Shortcuts;

namespace Rivet.Core.Input;

/// <summary>
/// Key debounce (spec 07 §3.7.2): swallows a key-down that repeats too fast.
/// Rule 1 drops a second down while the key is still down inside the window;
/// rule 2 drops a down that follows the same key's release inside the window,
/// but only when no other key was accepted in between, so fast "e r e" typing
/// is never touched. Auto-repeat, key-ups, modifiers and lock keys always pass.
/// Windows reports no repeat flag: a down-while-down that arrives at least
/// <c>repeatThreshold</c> after the accepted press counts as auto-repeat.
/// Pure state; not thread-safe (one instance per hook subscription).
/// </summary>
public sealed class KeyDebounceFilter
{
    /// <summary>State older than this is forgotten (the key may have been pressed elsewhere).</summary>
    public const long StaleGapNs = 5 * InputTime.NsPerSecond;

    /// <summary>Key ids: scan code (bit 0x100 = extended) below 0x200, 0x200 + VK when there is no scan code.</summary>
    public const int KeyIdCount = 0x300;

    private readonly KeyState[] _keys = new KeyState[KeyIdCount];
    private int[] _overrideMs = NoOverrides();
    private int _lastAcceptedKey = -1;

    /// <summary>The global window in ms (0 = accept everything).</summary>
    public int GlobalWindowMs { get; set; } = InputSettings.DefaultKeyWindowMs;

    /// <summary>Down-while-down at or after this gap is auto-repeat (derived from the keyboard repeat delay).</summary>
    public long RepeatThresholdNs { get; set; } = 200 * InputTime.NsPerMs;

    /// <summary>Replaces the per-key windows (scan id → ms). Safe to call while another thread filters.</summary>
    public void SetOverrides(IReadOnlyDictionary<int, int> overrides)
    {
        var table = NoOverrides();
        foreach (var (id, ms) in overrides)
        {
            if (id is > 0 and < KeyIdCount)
            {
                table[id] = ms;
            }
        }

        Volatile.Write(ref _overrideMs, table);
    }

    private static int[] NoOverrides()
    {
        var table = new int[KeyIdCount];
        Array.Fill(table, -1);
        return table;
    }

    /// <summary>
    /// The filter's key id for an event, or -1 when the event is never
    /// filtered (modifiers, lock keys, injected Unicode text).
    /// </summary>
    public static int KeyIdFor(int virtualKey, int scanCode, bool extended)
    {
        if (IsNeverFiltered(virtualKey))
        {
            return -1;
        }

        if (scanCode is > 0 and <= 0xFF)
        {
            return scanCode | (extended ? 0x100 : 0);
        }

        return virtualKey is > 0 and <= 0xFF ? 0x200 + virtualKey : -1;
    }

    public static bool IsNeverFiltered(int vk) => vk is
        VirtualKeys.Shift or VirtualKeys.Control or VirtualKeys.Menu or
        VirtualKeys.LShift or VirtualKeys.RShift or VirtualKeys.LControl or VirtualKeys.RControl or
        VirtualKeys.LMenu or VirtualKeys.RMenu or VirtualKeys.LWin or VirtualKeys.RWin or
        VirtualKeys.Capital or 0x90 /* NUMLOCK */ or 0x91 /* SCROLL */ or 0xE7 /* PACKET (Unicode text) */ or
        ChordStrokes.MaskKey;

    /// <summary>Feeds one key event; returns true when it must be swallowed.</summary>
    public bool OnKey(int keyId, bool down, long t)
    {
        if (keyId is < 0 or >= KeyIdCount)
        {
            return false;
        }

        ref var s = ref _keys[keyId];
        if (s.HasEvent && (t - s.LastEvent > StaleGapNs || t < s.LastEvent))
        {
            s = default;
            if (_lastAcceptedKey == keyId)
            {
                _lastAcceptedKey = -1;
            }
        }

        s.HasEvent = true;
        s.LastEvent = t;

        if (!down)
        {
            if (s.IsDown)
            {
                s.IsDown = false;
                s.HasRelease = true;
                s.LastRelease = t;
            }

            return false;
        }

        if (s.IsDown && s.HasPress && t - s.LastPress >= RepeatThresholdNs)
        {
            // Auto-repeat: never filtered and never refreshes the press time.
            return false;
        }

        var overrides = Volatile.Read(ref _overrideMs);
        var windowMs = overrides[keyId] >= 0 ? overrides[keyId] : GlobalWindowMs;
        var window = windowMs * InputTime.NsPerMs;
        if (window > 0)
        {
            if (s.IsDown && s.HasPress && t - s.LastPress < window)
            {
                return true;
            }

            if (s.HasRelease && _lastAcceptedKey == keyId && t - s.LastRelease < window)
            {
                // A suppressed down leaves the key "up": its key-up passes as an orphan.
                return true;
            }
        }

        s.IsDown = true;
        s.HasPress = true;
        s.LastPress = t;
        _lastAcceptedKey = keyId;
        return false;
    }

    public void Reset()
    {
        Array.Clear(_keys);
        _lastAcceptedKey = -1;
    }

    private struct KeyState
    {
        public bool IsDown;
        public bool HasPress;
        public bool HasRelease;
        public bool HasEvent;
        public long LastPress;
        public long LastRelease;
        public long LastEvent;
    }
}

/// <summary>
/// Per-key windows. Windows stores physical keys by scan code:
/// <c>"sc1E:100,scE04D:0"</c> (sorted). macOS backups store macOS key codes
/// (<c>"37:100,40:0"</c>); those are converted with the spec's table on read
/// and unknown codes are dropped. Values outside 0–500 become 5; malformed
/// parts are skipped; a later duplicate overwrites an earlier one.
/// </summary>
public static class KeyDebounceOverrides
{
    public const int DefaultMs = InputSettings.DefaultKeyWindowMs;

    /// <summary>Decodes the stored string into scan id → ms (a fresh, editable copy).</summary>
    public static SortedDictionary<int, int> Decode(string? stored)
    {
        var result = new SortedDictionary<int, int>();
        foreach (var (code, ms, isScan) in Parts(stored))
        {
            if (isScan)
            {
                result[code] = ms;
            }
            else if (MacKeyCodes.ToScanId(code) is { } scan)
            {
                result[scan] = ms;
            }
        }

        return result;
    }

    /// <summary>The macOS-format decoder, kept for the spec's test vectors: macOS key code → ms.</summary>
    public static IReadOnlyDictionary<int, int> DecodeMacKeyCodes(string? stored)
    {
        var result = new SortedDictionary<int, int>();
        foreach (var (code, ms, isScan) in Parts(stored))
        {
            if (!isScan)
            {
                result[code] = ms;
            }
        }

        return result;
    }

    public static string Encode(IReadOnlyDictionary<int, int> overrides)
    {
        var builder = new StringBuilder();
        foreach (var (id, ms) in overrides.Where(kv => kv.Key is > 0 and < 0x200).OrderBy(kv => kv.Key))
        {
            if (builder.Length > 0)
            {
                builder.Append(',');
            }

            builder.Append(EncodeScanId(id)).Append(':').Append(SanitizeMs(ms).ToString(CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    public static string EncodeScanId(int id) =>
        (id & 0x100) != 0 ? $"scE0{id & 0xFF:X2}" : $"sc{id & 0xFF:X2}";

    public static int SanitizeMs(int ms) => ms is >= 0 and <= 500 ? ms : DefaultMs;

    private static IEnumerable<(int Code, int Ms, bool IsScan)> Parts(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            yield break;
        }

        foreach (var part in stored.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = part.IndexOf(':');
            if (colon <= 0 || colon == part.Length - 1)
            {
                continue;
            }

            var codeText = part[..colon];
            if (!int.TryParse(part.AsSpan(colon + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var ms))
            {
                continue;
            }

            ms = SanitizeMs(ms);
            if (codeText.StartsWith("sc", StringComparison.OrdinalIgnoreCase))
            {
                var hex = codeText[2..];
                var extended = hex.StartsWith("E0", StringComparison.OrdinalIgnoreCase) && hex.Length == 4;
                if (extended)
                {
                    hex = hex[2..];
                }

                if (int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var scan) && scan is > 0 and <= 0xFF)
                {
                    yield return (scan | (extended ? 0x100 : 0), ms, true);
                }
            }
            else if (int.TryParse(codeText, NumberStyles.None, CultureInfo.InvariantCulture, out var mac) && mac is >= 0 and < 512)
            {
                yield return (mac, ms, false);
            }
        }
    }
}

/// <summary>macOS virtual key codes → Windows set-1 scan codes (spec 07 §6.7), for importing macOS backups.</summary>
public static class MacKeyCodes
{
    private static readonly Dictionary<int, int> ToScan = new()
    {
        // Letters
        [0] = 0x1E, [11] = 0x30, [8] = 0x2E, [2] = 0x20, [14] = 0x12, [3] = 0x21, [5] = 0x22, [4] = 0x23,
        [34] = 0x17, [38] = 0x24, [40] = 0x25, [37] = 0x26, [46] = 0x32, [45] = 0x31, [31] = 0x18, [35] = 0x19,
        [12] = 0x10, [15] = 0x13, [1] = 0x1F, [17] = 0x14, [32] = 0x16, [9] = 0x2F, [13] = 0x11, [7] = 0x2D,
        [16] = 0x15, [6] = 0x2C,

        // Digits
        [29] = 0x0B, [18] = 0x02, [19] = 0x03, [20] = 0x04, [21] = 0x05, [23] = 0x06, [22] = 0x07, [26] = 0x08,
        [28] = 0x09, [25] = 0x0A,

        // Keys
        [49] = 0x39, [36] = 0x1C, [48] = 0x0F, [51] = 0x0E, [53] = 0x01,

        // Punctuation
        [43] = 0x33, [47] = 0x34, [44] = 0x35, [41] = 0x27, [39] = 0x28, [27] = 0x0C, [24] = 0x0D, [33] = 0x1A,
        [30] = 0x1B, [42] = 0x2B, [50] = 0x29,
    };

    public static int? ToScanId(int macKeyCode) => ToScan.TryGetValue(macKeyCode, out var scan) ? scan : null;
}

/// <summary>The 52 keys the per-key picker offers (spec 07 §3.7.2), by US scan code with US virtual keys.</summary>
public static class DebounceKeyCatalog
{
    /// <summary>(scan id, US virtual key).</summary>
    public static IReadOnlyList<(int ScanId, int UsVirtualKey)> Keys { get; } = Build();

    public static int? UsVirtualKey(int scanId)
    {
        foreach (var (scan, vk) in Keys)
        {
            if (scan == scanId)
            {
                return vk;
            }
        }

        return null;
    }

    private static List<(int, int)> Build()
    {
        var keys = new List<(int, int)>();
        const string qwerty = "QWERTYUIOP";
        const string asdf = "ASDFGHJKL";
        const string zxcv = "ZXCVBNM";
        for (var i = 0; i < qwerty.Length; i++) keys.Add((0x10 + i, qwerty[i]));
        for (var i = 0; i < asdf.Length; i++) keys.Add((0x1E + i, asdf[i]));
        for (var i = 0; i < zxcv.Length; i++) keys.Add((0x2C + i, zxcv[i]));
        for (var d = 1; d <= 9; d++) keys.Add((0x01 + d, VirtualKeys.D0 + d));
        keys.Add((0x0B, VirtualKeys.D0));
        keys.Add((0x39, VirtualKeys.Space));
        keys.Add((0x1C, VirtualKeys.Return));
        keys.Add((0x0F, VirtualKeys.Tab));
        keys.Add((0x0E, VirtualKeys.Back));
        keys.Add((0x01, VirtualKeys.Escape));
        keys.Add((0x33, VirtualKeys.OemComma));
        keys.Add((0x34, VirtualKeys.OemPeriod));
        keys.Add((0x35, VirtualKeys.OemQuestion));
        keys.Add((0x27, VirtualKeys.OemSemicolon));
        keys.Add((0x28, VirtualKeys.OemQuotes));
        keys.Add((0x0C, VirtualKeys.OemMinus));
        keys.Add((0x0D, VirtualKeys.OemPlus));
        keys.Add((0x1A, VirtualKeys.OemOpenBrackets));
        keys.Add((0x1B, VirtualKeys.OemCloseBrackets));
        keys.Add((0x2B, VirtualKeys.OemPipe));
        keys.Add((0x29, VirtualKeys.OemTilde));
        return keys;
    }
}
