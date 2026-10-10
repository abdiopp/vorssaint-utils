// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;

namespace Rivet.Core.Launcher;

public enum EmojiSkinTone
{
    Default,
    Light,
    MediumLight,
    Medium,
    MediumDark,
    Dark,
}

/// <summary>One emoji row: the glyph as listed, its Unicode name and extra search words.</summary>
public sealed record EmojiEntry(string Glyph, string Name, string Aliases)
{
    public string Keywords => Aliases.Length > 0 ? $"{Name} {Aliases} Emoji" : $"{Name} Emoji";
}

/// <summary>
/// Emoji for the Command Bar (spec 06 §6.10): the popular list first, then
/// every other emoji scalar sorted by its Unicode name; names come from the
/// Unicode Character Database (generated table). Skin tones apply only to
/// single-scalar modifier bases, never to the legacy family 👪.
/// </summary>
public static class CommandBarEmoji
{
    private static readonly Dictionary<string, string> AliasTable = new(StringComparer.Ordinal)
    {
        ["😂"] = "haha lol roflmao laugh laughing tears funny",
        ["🤣"] = "haha lol rofl roflmao laugh laughing funny",
        ["😊"] = "happy smile blush",
        ["🥰"] = "love affection hearts",
        ["😘"] = "kiss love",
        ["😎"] = "cool sunglasses",
        ["🤔"] = "think thinking hmm",
        ["🙄"] = "eyeroll whatever",
        ["😭"] = "cry crying sad sob",
        ["🥺"] = "please pleading puppy eyes",
        ["😡"] = "angry mad rage",
        ["🤬"] = "swear cursing angry",
        ["🤷"] = "idk shrug whatever",
        ["💀"] = "dead death dying skeleton halloween",
        ["☠"] = "dead death danger poison pirate",
        ["🙏"] = "appreciate please thanks thank thx you pray prayer high five",
        ["👍"] = "yes good approve like okay",
        ["👎"] = "no bad disapprove dislike",
        ["👌"] = "okay perfect good",
        ["👏"] = "clap applause congrats congratulations",
        ["🙌"] = "hooray celebrate praise",
        ["🫶"] = "love heart hands",
        ["👀"] = "look looking eyes see",
        ["❤"] = "love heart red",
        ["💔"] = "heartbreak broken heart sad",
        ["🔥"] = "fire hot lit trending",
        ["✨"] = "sparkle sparkles magic clean",
        ["🎉"] = "party celebrate celebration congrats congratulations",
        ["✅"] = "check done yes complete success",
        ["❌"] = "cross no wrong error fail",
        ["⚠"] = "warning caution alert",
        ["💡"] = "idea lightbulb tip",
        ["🚀"] = "launch ship rocket fast",
    };

    private static readonly Lazy<IReadOnlyList<EmojiEntry>> AllEntries = new(Load);

    private static readonly Lazy<HashSet<int>> ModifierBaseSet = new(() =>
        EmojiData.ModifierBases.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(h => int.Parse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture))
            .ToHashSet());

    /// <summary>Every emoji, popular first. More than a thousand rows, so they never join a normal search.</summary>
    public static IReadOnlyList<EmojiEntry> All => AllEntries.Value;

    private static IReadOnlyList<EmojiEntry> Load()
    {
        var entries = new List<EmojiEntry>(1500);
        foreach (var block in new[] { EmojiData.Popular, EmojiData.LongTail })
        {
            foreach (var line in block.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var tab = line.IndexOf('\t');
                if (tab <= 0)
                {
                    continue;
                }

                var glyph = line[..tab];
                entries.Add(new EmojiEntry(glyph, line[(tab + 1)..], AliasTable.GetValueOrDefault(StripSelectors(glyph), string.Empty)));
            }
        }

        return entries;
    }

    /// <summary>The glyph without variation selectors (alias and duplicate key).</summary>
    public static string StripSelectors(string glyph) => glyph.Replace("️", string.Empty).Replace("︎", string.Empty);

    /// <summary>Whether a skin tone applies: a single-scalar modifier base other than 👪.</summary>
    public static bool AcceptsSkinTone(string glyph)
    {
        var stripped = StripSelectors(glyph);
        var runes = stripped.EnumerateRunes().ToList();
        return runes.Count == 1 && runes[0].Value != 0x1F46A && ModifierBaseSet.Value.Contains(runes[0].Value);
    }

    /// <summary>The base without variation selectors plus the modifier scalar (U+1F3FB light … U+1F3FF dark).</summary>
    public static string ApplySkinTone(string glyph, EmojiSkinTone tone)
    {
        if (tone == EmojiSkinTone.Default || !AcceptsSkinTone(glyph))
        {
            return glyph;
        }

        var modifier = 0x1F3FB + ((int)tone - 1);
        return StripSelectors(glyph) + char.ConvertFromUtf32(modifier);
    }

    /// <summary>The five tones other than <paramref name="current"/> (row actions offer them as one-off insertions).</summary>
    public static IReadOnlyList<(EmojiSkinTone Tone, string Glyph)> OtherTones(string glyph, EmojiSkinTone current)
    {
        if (!AcceptsSkinTone(glyph))
        {
            return [];
        }

        return Enum.GetValues<EmojiSkinTone>()
            .Where(t => t != current)
            .Select(t => (t, t == EmojiSkinTone.Default ? glyph : ApplySkinTone(glyph, t)))
            .ToList();
    }

    public static EmojiSkinTone ParseTone(string stored) => stored switch
    {
        "light" => EmojiSkinTone.Light,
        "mediumLight" => EmojiSkinTone.MediumLight,
        "medium" => EmojiSkinTone.Medium,
        "mediumDark" => EmojiSkinTone.MediumDark,
        "dark" => EmojiSkinTone.Dark,
        _ => EmojiSkinTone.Default,
    };

    public static string StoreTone(EmojiSkinTone tone) => tone switch
    {
        EmojiSkinTone.Light => "light",
        EmojiSkinTone.MediumLight => "mediumLight",
        EmojiSkinTone.Medium => "medium",
        EmojiSkinTone.MediumDark => "mediumDark",
        EmojiSkinTone.Dark => "dark",
        _ => string.Empty,
    };
}
