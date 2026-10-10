// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Rivet.Core.Clipboard;
using Rivet.Core.Settings;
using Rivet.Core.Shortcuts;

namespace Rivet.Core.Launcher;

/// <summary>Command Bar settings (spec 06 §4). Keys match the macOS app.</summary>
public static class CommandBarSettings
{
    public const int MaxPins = 30;
    public const int MaxAliasLength = 60;
    public const int MaxRowShortcuts = 64;

    /// <summary>The bar's global shortcut is off by default, as on macOS.</summary>
    public static readonly Setting<bool> ShortcutEnabled = new("commandBarShortcutEnabled", false);

    public static readonly Setting<string> Shortcut = new("commandBarShortcut", string.Empty);

    /// <summary>Alt+Space, taken over from the window menu with the shared hook (PowerToys Run precedent).</summary>
    public static readonly KeyChord DefaultShortcut = new(KeyModifiers.Alt, VirtualKeys.Space);

    public static readonly Setting<bool> CompactMode = new("commandBarCompactMode", false);

    public static readonly Setting<string> DisabledSources = new("commandBarDisabledSources", string.Empty);

    public static readonly Setting<string> Aliases = new("commandBarAliases", string.Empty);

    public static readonly Setting<string> Pins = new("commandBarPins", string.Empty);

    public static readonly Setting<string> Hidden = new("commandBarHidden", string.Empty);

    public static readonly Setting<List<CommandBarLink>> Links = new("commandBarLinks", [], CommandBarLinks.Sanitize);

    public static readonly Setting<string> PositionOffset = new("commandBarPositionOffset", string.Empty);

    public static readonly Setting<string> EmojiSkinTone = new("commandBarEmojiSkinTone", string.Empty,
        Sanitize.OneOfStrings(string.Empty, string.Empty, "light", "mediumLight", "medium", "mediumDark", "dark"));

    /// <summary>Folders the file search looks in (machine-specific, never backed up).</summary>
    public static readonly Setting<string> FileScopes = new("commandBarFileScopes", string.Empty, machineState: true);

    public static readonly Setting<string> FileIgnores = new("commandBarFileIgnores", string.Empty);

    /// <summary>Run counts per row (never queries); machine-specific.</summary>
    public static readonly Setting<string> Usage = new("commandBarUsage", string.Empty, machineState: true);

    /// <summary>Row shortcuts: JSON <c>{stableKey: chord}</c> (spec 06 §3.8.9).</summary>
    public static readonly Setting<string> RowShortcuts = new("commandBarRowShortcuts", string.Empty);
}

/// <summary>Why a row shortcut was refused.</summary>
public enum RowShortcutRefusal
{
    None,
    NeedsModifier,
    TooMany,
}

/// <summary>Pins, names, hidden rows, switched-off sources and the bar position (spec 06 §3.8.9).</summary>
public sealed class CommandBarPreferences(ISettingsStore settings)
{
    public static readonly string[] BuiltInIgnores = ["node_modules", ".git", "DerivedData", "Pods", ".build", "vendor"];

    public ISettingsStore Settings { get; } = settings;

    public List<string> Pins => Lines(Settings.Get(CommandBarSettings.Pins));

    public HashSet<string> Hidden => Lines(Settings.Get(CommandBarSettings.Hidden)).ToHashSet(StringComparer.Ordinal);

    public HashSet<CommandSource> DisabledSources =>
        Settings.Get(CommandBarSettings.DisabledSources)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(CommandSources.Parse)
            .OfType<CommandSource>()
            .Where(s => s != CommandSource.Actions)
            .ToHashSet();

    public Dictionary<string, string> Aliases
    {
        get
        {
            var json = Settings.Get(CommandBarSettings.Aliases);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }

            try
            {
                var map = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];
                return map.Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
                    .ToDictionary(kv => kv.Key, kv => kv.Value.Trim(), StringComparer.Ordinal);
            }
            catch (JsonException)
            {
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }
        }
    }

    public bool IsSourceEnabled(CommandSource source) => source == CommandSource.Actions || !DisabledSources.Contains(source);

    public void SetSourceEnabled(CommandSource source, bool enabled)
    {
        var set = DisabledSources;
        if (enabled ? set.Remove(source) : source != CommandSource.Actions && set.Add(source))
        {
            Settings.Set(CommandBarSettings.DisabledSources, string.Join(',', set.Select(CommandSources.StorageId).OrderBy(s => s, StringComparer.Ordinal)));
        }
    }

    /// <summary>Toggles a pin; at most 30, the oldest dropped. Returns whether the row is now pinned.</summary>
    public bool TogglePin(string stableKey)
    {
        var pins = Pins;
        bool pinned;
        if (pins.Remove(stableKey))
        {
            pinned = false;
        }
        else
        {
            pins.Add(stableKey);
            while (pins.Count > CommandBarSettings.MaxPins)
            {
                pins.RemoveAt(0);
            }

            pinned = true;
        }

        Settings.Set(CommandBarSettings.Pins, string.Join('\n', pins));
        return pinned;
    }

    public void Unpin(string stableKey)
    {
        var pins = Pins;
        if (pins.Remove(stableKey))
        {
            Settings.Set(CommandBarSettings.Pins, string.Join('\n', pins));
        }
    }

    public void SetHidden(string stableKey, bool hidden)
    {
        var set = Hidden;
        if (hidden ? set.Add(stableKey) : set.Remove(stableKey))
        {
            Settings.Set(CommandBarSettings.Hidden, string.Join('\n', set.OrderBy(s => s, StringComparer.Ordinal)));
        }
    }

    /// <summary>
    /// Saves an alias (≤ 60 characters; empty clears). Returns the stable key
    /// of another row already answering to one of its words, or null on success.
    /// </summary>
    public string? SetAlias(string stableKey, string alias)
    {
        var trimmed = alias.Trim();
        if (trimmed.Length > CommandBarSettings.MaxAliasLength)
        {
            trimmed = trimmed[..CommandBarSettings.MaxAliasLength].Trim();
        }

        var aliases = Aliases;
        if (trimmed.Length == 0)
        {
            aliases.Remove(stableKey);
        }
        else
        {
            var words = TextFold.ForCommand(trimmed).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
            foreach (var (key, other) in aliases)
            {
                if (key != stableKey && TextFold.ForCommand(other).Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(words.Contains))
                {
                    return key;
                }
            }

            aliases[stableKey] = trimmed;
        }

        Settings.Set(CommandBarSettings.Aliases, aliases.Count == 0 ? string.Empty : JsonSerializer.Serialize(aliases));
        return null;
    }

    /// <summary>Row shortcuts by stable key (unreadable entries dropped).</summary>
    public Dictionary<string, KeyChord> RowShortcuts
    {
        get
        {
            var result = new Dictionary<string, KeyChord>(StringComparer.Ordinal);
            var json = Settings.Get(CommandBarSettings.RowShortcuts);
            if (string.IsNullOrWhiteSpace(json))
            {
                return result;
            }

            try
            {
                foreach (var (key, value) in JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [])
                {
                    if (KeyChord.TryParse(value, out var chord) && !chord.IsEmpty)
                    {
                        result[key] = chord;
                    }
                }
            }
            catch (JsonException)
            {
            }

            return result;
        }
    }

    /// <summary>
    /// Binds a chord to a row. It must include a modifier (a bare key would be
    /// taken from every app); a chord another row uses moves here; at most 64.
    /// </summary>
    public RowShortcutRefusal SetRowShortcut(string stableKey, KeyChord chord)
    {
        if ((chord.Modifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Win)) == 0)
        {
            return RowShortcutRefusal.NeedsModifier;
        }

        var map = RowShortcuts;
        foreach (var other in map.Where(kv => kv.Value == chord && kv.Key != stableKey).Select(kv => kv.Key).ToList())
        {
            map.Remove(other);
        }

        if (!map.ContainsKey(stableKey) && map.Count >= CommandBarSettings.MaxRowShortcuts)
        {
            return RowShortcutRefusal.TooMany;
        }

        map[stableKey] = chord;
        SaveRowShortcuts(map);
        return RowShortcutRefusal.None;
    }

    public void RemoveRowShortcut(string stableKey)
    {
        var map = RowShortcuts;
        if (map.Remove(stableKey))
        {
            SaveRowShortcuts(map);
        }
    }

    private void SaveRowShortcuts(Dictionary<string, KeyChord> map) =>
        Settings.Set(CommandBarSettings.RowShortcuts, map.Count == 0
            ? string.Empty
            : JsonSerializer.Serialize(map.ToDictionary(kv => kv.Key, kv => kv.Value.ToStorageString(), StringComparer.Ordinal)));

    public List<string> FileScopes => Lines(Settings.Get(CommandBarSettings.FileScopes));

    public void SetFileScopes(IEnumerable<string> scopes) =>
        Settings.Set(CommandBarSettings.FileScopes, string.Join('\n', scopes.Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)));

    public List<string> FileIgnores => Lines(Settings.Get(CommandBarSettings.FileIgnores));

    public void SetFileIgnores(IEnumerable<string> ignores) =>
        Settings.Set(CommandBarSettings.FileIgnores, string.Join('\n', ignores.Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)));

    /// <summary>The drag offset from the default spot ("dx,dy" in DIPs; positive dy = moved up).</summary>
    public (int Dx, int Dy) PositionOffset
    {
        get
        {
            var parts = Settings.Get(CommandBarSettings.PositionOffset).Split(',');
            return parts.Length == 2 && int.TryParse(parts[0], out var dx) && int.TryParse(parts[1], out var dy) ? (dx, dy) : (0, 0);
        }

        set => Settings.Set(CommandBarSettings.PositionOffset, value == (0, 0) ? string.Empty : $"{value.Dx},{value.Dy}");
    }

    public EmojiSkinTone SkinTone
    {
        get => CommandBarEmoji.ParseTone(Settings.Get(CommandBarSettings.EmojiSkinTone));
        set => Settings.Set(CommandBarSettings.EmojiSkinTone, CommandBarEmoji.StoreTone(value));
    }

    public CommandBarUsage LoadUsage() => CommandBarUsage.Deserialize(Settings.Get(CommandBarSettings.Usage));

    public void SaveUsage(CommandBarUsage usage) =>
        Settings.Set(CommandBarSettings.Usage, usage.Records.Count == 0 ? string.Empty : usage.Serialize());

    /// <summary>Whether a file or folder name is never worth showing (built-in and user ignores).</summary>
    public static bool IsIgnored(string name, IReadOnlyCollection<string> userIgnores)
    {
        if (name.StartsWith('.') || BuiltInIgnores.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var pattern in userIgnores)
        {
            if (pattern.StartsWith("*.", StringComparison.Ordinal) && name.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (pattern.StartsWith('.') && name.EndsWith(pattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(pattern, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static List<string> Lines(string text) =>
        text.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToList();
}
