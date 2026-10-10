// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rivet.Core.Shortcuts;

namespace Rivet.Core.Modules.RadialMenu;

/// <summary>The legacy single-wheel keys a missing profile list migrates from.</summary>
public sealed record RadialLegacySeeds(string? Shortcut, string? MouseButton, JsonNode? Items);

public sealed record RadialProfilesResult(IReadOnlyList<RadialProfile> Profiles, bool Migrated);

/// <summary>
/// Reads and writes <c>radialMenuProfiles</c> (spec 07 §5.2). Reading is lenient
/// and has no version field: a profile with a wrong field type or an invalid
/// colour or id is dropped alone, an item with an unknown kind is dropped
/// alone, and the cleanup below runs on every read and write. A value that is
/// absent or not an array migrates the legacy keys into one "General" profile;
/// the caller must persist that seed at once (otherwise every read would mint
/// a new random id).
/// </summary>
public static class RadialProfilesCodec
{
    public const int MaxCustomIconBytes = 65_536;

    private static readonly string[] ColorNames =
        ["accent", "blue", "purple", "pink", "red", "orange", "yellow", "green", "mint", "cyan", "indigo", "graphite"];

    private static readonly string[] KindNames =
        ["app", "file", "url", "shortcut", "tool", "quickToggle", "windowLayout", "media", "submenu"];

    public static RadialProfilesResult Decode(JsonNode? stored, RadialLegacySeeds legacy)
    {
        var array = AsArray(stored);
        if (array is null)
        {
            return new RadialProfilesResult(Clean([Migrate(legacy)]), Migrated: true);
        }

        var profiles = new List<RadialProfile>();
        foreach (var node in array)
        {
            if (DecodeProfile(node) is { } profile)
            {
                profiles.Add(profile);
            }
        }

        return new RadialProfilesResult(Clean(profiles), Migrated: false);
    }

    public static JsonArray Encode(IReadOnlyList<RadialProfile> profiles)
    {
        var array = new JsonArray();
        foreach (var profile in Clean(profiles))
        {
            var obj = new JsonObject
            {
                ["id"] = FormatId(profile.Id),
                ["name"] = profile.Name,
                ["color"] = ColorNames[(int)profile.Color],
                ["shortcut"] = profile.Shortcut,
                ["mouseButton"] = profile.MouseButton.ToStorage(),
                ["items"] = EncodeItems(profile.Items),
                ["trackpadTap"] = profile.TrackpadTap,
            };
            if (profile.Preset is { } preset)
            {
                obj["preset"] = RadialPresets.Id(preset);
            }

            array.Add(obj);
        }

        return array;
    }

    /// <summary>
    /// Profiles: duplicate ids dropped, names cut to 60, invalid shortcuts
    /// cleared, a mouse button already used by an earlier profile switched off,
    /// a shortcut already used by an earlier profile cleared; an empty list
    /// becomes one General profile with the default shortcut and starter items.
    /// </summary>
    public static IReadOnlyList<RadialProfile> Clean(IEnumerable<RadialProfile> input)
    {
        var result = new List<RadialProfile>();
        var ids = new HashSet<Guid>();
        var buttons = new HashSet<int>();
        var shortcuts = new HashSet<string>(StringComparer.Ordinal);
        var tapTaken = false;
        foreach (var profile in input)
        {
            if (profile.Id == Guid.Empty || !ids.Add(profile.Id))
            {
                continue;
            }

            var shortcut = CleanShortcut(profile.Shortcut);
            if (shortcut.Length > 0 && !shortcuts.Add(shortcut))
            {
                shortcut = string.Empty;
            }

            var button = profile.MouseButton;
            if (!button.IsOff && !buttons.Add(button.Button))
            {
                button = RadialMouseTrigger.Off;
            }

            var tap = profile.TrackpadTap && !tapTaken;
            tapTaken |= tap;
            result.Add(profile with
            {
                Name = Truncate(profile.Name.Trim(), RadialProfile.MaxNameLength),
                Shortcut = shortcut,
                MouseButton = button,
                TrackpadTap = tap,
                Items = CleanItems(profile.Items, depth: 0),
            });
        }

        if (result.Count == 0)
        {
            result.Add(DefaultProfile());
        }

        return result;
    }

    /// <summary>One level of items: ≤ 12, unique ids, trimmed names and payloads, invalid items dropped, submenus only at depth 0.</summary>
    public static IReadOnlyList<RadialItem> CleanItems(IEnumerable<RadialItem> items, int depth)
    {
        var result = new List<RadialItem>();
        var ids = new HashSet<Guid>();
        foreach (var item in items)
        {
            if (result.Count >= RadialGeometry.MaxItems)
            {
                break;
            }

            var id = item.Id == Guid.Empty ? Guid.NewGuid() : item.Id;
            if (!ids.Add(id))
            {
                continue;
            }

            if (item.Kind == RadialItemKind.Submenu && depth > 0)
            {
                // Deeper submenus are dropped (root + one level).
                continue;
            }

            var payload = item.Payload.Trim();
            if (!IsValidPayload(item.Kind, ref payload))
            {
                continue;
            }

            var icon = IsUsableIcon(item.CustomIconData) ? item.CustomIconData : null;
            var children = item.Kind == RadialItemKind.Submenu ? CleanItems(item.Children, depth + 1) : [];
            result.Add(item with
            {
                Id = id,
                Name = Truncate(item.Name.Trim(), RadialProfile.MaxNameLength),
                SymbolName = item.SymbolName.Trim(),
                Payload = payload,
                CustomIconData = icon,
                Children = children,
            });
        }

        return result;
    }

    public static RadialProfile DefaultProfile() => new()
    {
        Name = string.Empty,
        Color = RadialColor.Accent,
        Shortcut = RadialMenuSettings.DefaultShortcut.ToStorageString(),
        Items = RadialPresets.StarterItems(),
        Preset = RadialPreset.General,
    };

    /// <summary>Whether an item has a target it can run, normalizing the payload where needed.</summary>
    public static bool IsValidPayload(RadialItemKind kind, ref string payload)
    {
        switch (kind)
        {
            case RadialItemKind.App:
            case RadialItemKind.File:
            case RadialItemKind.Tool:
                return payload.Length > 0;
            case RadialItemKind.Url:
                var normalized = RadialLinks.Normalize(payload);
                if (normalized is null)
                {
                    return false;
                }

                payload = normalized;
                return true;
            case RadialItemKind.Shortcut:
                var shortcut = CleanShortcut(payload);
                payload = shortcut;
                return shortcut.Length > 0;
            case RadialItemKind.QuickToggle:
                return RadialQuickToggleIds.All.Contains(payload);
            case RadialItemKind.WindowLayout:
                return WindowLayoutActions.IsKnown(payload);
            case RadialItemKind.Media:
                return RadialMediaIds.All.Contains(payload);
            case RadialItemKind.Submenu:
                payload = string.Empty;
                return true;
            default:
                return false;
        }
    }

    /// <summary>A shortcut storage string Windows can register (macOS storage is translated), or empty.</summary>
    public static string CleanShortcut(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        if (KeyChord.TryParse(value, out var chord) && chord.IsValidGlobalShortcut)
        {
            return chord.ToStorageString();
        }

        return MacShortcutTranslator.TryTranslate(value, out var translated) && translated.IsValidGlobalShortcut
            ? translated.ToStorageString()
            : string.Empty;
    }

    /// <summary>A PNG under 64 KiB (the decoder check happens when the icon is drawn).</summary>
    public static bool IsUsableIcon(string? base64)
    {
        if (string.IsNullOrEmpty(base64))
        {
            return false;
        }

        try
        {
            var bytes = Convert.FromBase64String(base64);
            return bytes.Length is > 8 and <= MaxCustomIconBytes
                   && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static RadialProfile Migrate(RadialLegacySeeds legacy)
    {
        var shortcut = string.IsNullOrWhiteSpace(legacy.Shortcut)
            ? RadialMenuSettings.DefaultShortcut.ToStorageString()
            : CleanShortcut(legacy.Shortcut);
        IReadOnlyList<RadialItem> items = legacy.Items is JsonArray array
            ? DecodeItems(array)
            : RadialPresets.StarterItems();
        return new RadialProfile
        {
            Name = string.Empty,
            Color = RadialColor.Accent,
            Shortcut = shortcut,
            MouseButton = RadialMouseTrigger.Parse(legacy.MouseButton),
            Items = items,
            Preset = RadialPreset.General,
        };
    }

    /// <summary>A JSON array, or a string holding base64 (macOS Data) or JSON text of one.</summary>
    private static JsonArray? AsArray(JsonNode? stored)
    {
        if (stored is JsonArray array)
        {
            return array;
        }

        if (stored is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0)
        {
            foreach (var candidate in Candidates(text))
            {
                try
                {
                    if (JsonNode.Parse(candidate) is JsonArray parsed)
                    {
                        return parsed;
                    }
                }
                catch (JsonException)
                {
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> Candidates(string text)
    {
        yield return text;
        string? decoded = null;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(text));
        }
        catch (FormatException)
        {
        }

        if (decoded is not null)
        {
            yield return decoded;
        }
    }

    private static RadialProfile? DecodeProfile(JsonNode? node)
    {
        if (node is not JsonObject obj)
        {
            return null;
        }

        var id = Guid.NewGuid();
        if (obj.TryGetPropertyValue("id", out var idNode) && idNode is not null)
        {
            if (!TryString(idNode, out var idText) || !Guid.TryParse(idText, out id))
            {
                return null;
            }
        }

        if (!OptionalString(obj, "name", out var name) || !OptionalString(obj, "shortcut", out var shortcut) || !OptionalString(obj, "mouseButton", out var mouse))
        {
            return null;
        }

        var color = RadialColor.Accent;
        if (obj.TryGetPropertyValue("color", out var colorNode) && colorNode is not null)
        {
            if (!TryString(colorNode, out var colorText) || Array.IndexOf(ColorNames, colorText) is var index && index < 0)
            {
                return null;
            }

            color = (RadialColor)Array.IndexOf(ColorNames, colorText);
        }

        var tap = false;
        if (obj.TryGetPropertyValue("trackpadTap", out var tapNode) && tapNode is not null)
        {
            if (tapNode is not JsonValue tapValue || !tapValue.TryGetValue(out tap))
            {
                return null;
            }
        }

        IReadOnlyList<RadialItem> items = [];
        if (obj.TryGetPropertyValue("items", out var itemsNode) && itemsNode is not null)
        {
            if (itemsNode is not JsonArray itemsArray)
            {
                return null;
            }

            items = DecodeItems(itemsArray);
        }

        RadialPreset? preset = null;
        if (obj.TryGetPropertyValue("preset", out var presetNode) && presetNode is not null && TryString(presetNode, out var presetText))
        {
            preset = RadialPresets.Parse(presetText);
        }

        return new RadialProfile
        {
            Id = id,
            Name = name ?? string.Empty,
            Color = color,
            Shortcut = shortcut ?? string.Empty,
            MouseButton = RadialMouseTrigger.Parse(mouse),
            Items = items,
            Preset = preset,
            TrackpadTap = tap,
        };
    }

    private static List<RadialItem> DecodeItems(JsonArray array)
    {
        var items = new List<RadialItem>();
        foreach (var node in array)
        {
            if (DecodeItem(node) is { } item)
            {
                items.Add(item);
            }
        }

        return items;
    }

    private static RadialItem? DecodeItem(JsonNode? node)
    {
        if (node is not JsonObject obj || !obj.TryGetPropertyValue("kind", out var kindNode) || kindNode is null || !TryString(kindNode, out var kindText))
        {
            return null;
        }

        var kindIndex = Array.IndexOf(KindNames, kindText);
        if (kindIndex < 0)
        {
            return null;
        }

        var id = Guid.NewGuid();
        if (obj.TryGetPropertyValue("id", out var idNode) && idNode is not null && TryString(idNode, out var idText) && Guid.TryParse(idText, out var parsed))
        {
            id = parsed;
        }

        OptionalString(obj, "name", out var name);
        OptionalString(obj, "symbolName", out var symbol);
        OptionalString(obj, "payload", out var payload);
        OptionalString(obj, "customIconData", out var icon);
        var children = obj.TryGetPropertyValue("children", out var childrenNode) && childrenNode is JsonArray childArray ? DecodeItems(childArray) : [];
        return new RadialItem
        {
            Id = id,
            Kind = (RadialItemKind)kindIndex,
            Name = name ?? string.Empty,
            SymbolName = symbol ?? string.Empty,
            Payload = payload ?? string.Empty,
            CustomIconData = string.IsNullOrEmpty(icon) ? null : icon,
            Children = children,
        };
    }

    private static JsonArray EncodeItems(IReadOnlyList<RadialItem> items)
    {
        var array = new JsonArray();
        foreach (var item in items)
        {
            var obj = new JsonObject
            {
                ["id"] = FormatId(item.Id),
                ["kind"] = KindNames[(int)item.Kind],
                ["name"] = item.Name,
                ["symbolName"] = item.SymbolName,
                ["payload"] = item.Payload,
            };
            if (item.CustomIconData is { } icon)
            {
                obj["customIconData"] = icon;
            }

            if (item.Kind == RadialItemKind.Submenu)
            {
                obj["children"] = EncodeItems(item.Children);
            }

            array.Add(obj);
        }

        return array;
    }

    private static bool TryString(JsonNode node, out string value)
    {
        value = string.Empty;
        return node is JsonValue v && v.TryGetValue(out value!);
    }

    /// <summary>Absent → null (ok); present but not a string → false.</summary>
    private static bool OptionalString(JsonObject obj, string key, out string? value)
    {
        value = null;
        if (!obj.TryGetPropertyValue(key, out var node) || node is null)
        {
            return true;
        }

        if (TryString(node, out var text))
        {
            value = text;
            return true;
        }

        return false;
    }

    private static string FormatId(Guid id) => id.ToString("D").ToUpperInvariant();

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];
}

/// <summary>
/// Turns a macOS shortcut storage string (<c>control+option+command:49</c>, macOS
/// virtual key codes) into a Windows chord, so wheels from a macOS backup keep
/// their shortcuts: control → Ctrl, option → Alt, shift → Shift, command → Win.
/// </summary>
public static class MacShortcutTranslator
{
    private static readonly Dictionary<int, int> KeyCodes = BuildKeyCodes();

    public static bool TryTranslate(string value, out KeyChord chord)
    {
        chord = KeyChord.None;
        var colon = value.LastIndexOf(':');
        if (colon < 0 || !int.TryParse(value.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var code))
        {
            return false;
        }

        var modifiers = KeyModifiers.None;
        foreach (var token in value[..colon].Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            modifiers |= token switch
            {
                "control" => KeyModifiers.Control,
                "option" => KeyModifiers.Alt,
                "shift" => KeyModifiers.Shift,
                "command" => KeyModifiers.Win,
                _ => (KeyModifiers)(-1),
            };
            if (modifiers < 0)
            {
                return false;
            }
        }

        if (!KeyCodes.TryGetValue(code, out var vk))
        {
            return false;
        }

        chord = new KeyChord(modifiers, vk);
        return true;
    }

    private static Dictionary<int, int> BuildKeyCodes()
    {
        var map = new Dictionary<int, int>();
        // ANSI letters by macOS key code.
        int[] letterCodes = [0, 11, 8, 2, 14, 3, 5, 4, 34, 38, 40, 37, 46, 45, 31, 35, 12, 15, 1, 17, 32, 9, 13, 7, 16, 6];
        for (var i = 0; i < 26; i++)
        {
            map[letterCodes[i]] = VirtualKeys.A + i;
        }

        int[] digitCodes = [29, 18, 19, 20, 21, 23, 22, 26, 28, 25];
        for (var d = 0; d < 10; d++)
        {
            map[digitCodes[d]] = VirtualKeys.D0 + d;
        }

        int[] fCodes = [122, 120, 99, 118, 96, 97, 98, 100, 101, 109, 103, 111, 105, 107, 113, 106, 64, 79, 80, 90];
        for (var f = 0; f < fCodes.Length; f++)
        {
            map[fCodes[f]] = VirtualKeys.F1 + f;
        }

        map[49] = VirtualKeys.Space;
        map[36] = VirtualKeys.Return;
        map[76] = VirtualKeys.Return;
        map[48] = VirtualKeys.Tab;
        map[51] = VirtualKeys.Back;
        map[117] = VirtualKeys.Delete;
        map[53] = VirtualKeys.Escape;
        map[123] = VirtualKeys.Left;
        map[124] = VirtualKeys.Right;
        map[125] = VirtualKeys.Down;
        map[126] = VirtualKeys.Up;
        map[115] = VirtualKeys.Home;
        map[119] = VirtualKeys.End;
        map[116] = VirtualKeys.Prior;
        map[121] = VirtualKeys.Next;
        map[27] = VirtualKeys.OemMinus;
        map[24] = VirtualKeys.OemPlus;
        map[33] = VirtualKeys.OemOpenBrackets;
        map[30] = VirtualKeys.OemCloseBrackets;
        map[41] = VirtualKeys.OemSemicolon;
        map[39] = VirtualKeys.OemQuotes;
        map[43] = VirtualKeys.OemComma;
        map[47] = VirtualKeys.OemPeriod;
        map[44] = VirtualKeys.OemQuestion;
        map[42] = VirtualKeys.OemPipe;
        map[50] = VirtualKeys.OemTilde;
        return map;
    }
}
