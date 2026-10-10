// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Rivet.Core.Modules.Scratchpad;

/// <summary>One tab of plain text.</summary>
public sealed record ScratchpadPad(Guid Id, string Name, string Text, DateTimeOffset? ModifiedAt)
{
    public bool IsEmpty => Text.Length == 0;
}

/// <summary>
/// The whole notes document: up to 12 tabs in order plus the selected tab.
/// Immutable, so a tab operation can build the next document, write it, and
/// only adopt it when the write succeeded (spec 07 §3.3.6 "write-first").
/// </summary>
public sealed record ScratchpadDocument
{
    public const int MaxPads = 12;
    public const int MaxNameLength = 40;

    public ScratchpadDocument(IReadOnlyList<ScratchpadPad> pads, Guid selectedId)
    {
        Pads = pads;
        SelectedId = selectedId;
    }

    public IReadOnlyList<ScratchpadPad> Pads { get; init; }

    public Guid SelectedId { get; init; }

    public ScratchpadPad Selected => Pads.FirstOrDefault(p => p.Id == SelectedId) ?? Pads[0];

    public int SelectedIndex => Math.Max(0, Pads.ToList().FindIndex(p => p.Id == SelectedId));

    public bool CanAddPad => Pads.Count < MaxPads;

    public static ScratchpadDocument Fresh(string baseName)
    {
        var pad = new ScratchpadPad(Guid.NewGuid(), ScratchpadNames.NextPadName([], baseName), string.Empty, null);
        return new ScratchpadDocument([pad], pad.Id);
    }

    public ScratchpadPad? Find(Guid id) => Pads.FirstOrDefault(p => p.Id == id);

    /// <summary>New text for a tab; modifiedAt follows (cleared when the text becomes empty).</summary>
    public ScratchpadDocument WithText(Guid padId, string text, DateTimeOffset now) =>
        Replace(padId, p => p with { Text = text, ModifiedAt = text.Length == 0 ? null : now });

    public ScratchpadDocument WithSelected(Guid padId) =>
        Find(padId) is null ? this : this with { SelectedId = padId };

    /// <summary>Appends a tab named with the next free default name and selects it. Null at the 12-tab limit.</summary>
    public ScratchpadDocument? WithNewPad(string baseName)
    {
        if (!CanAddPad)
        {
            return null;
        }

        var pad = new ScratchpadPad(Guid.NewGuid(), ScratchpadNames.NextPadName(Pads.Select(p => p.Name), baseName), string.Empty, null);
        return new ScratchpadDocument([.. Pads, pad], pad.Id);
    }

    /// <summary>Renames a tab after cleaning; an empty result leaves the name unchanged (returns this).</summary>
    public ScratchpadDocument WithName(Guid padId, string name)
    {
        var cleaned = ScratchpadNames.CleanName(name);
        return cleaned.Length == 0 ? this : Replace(padId, p => p with { Name = cleaned });
    }

    /// <summary>
    /// Removes a tab. The last tab cannot be closed (null). After closing the
    /// selected tab, the tab now at the same index is selected (or the new last one).
    /// </summary>
    public ScratchpadDocument? WithoutPad(Guid padId)
    {
        var index = Pads.ToList().FindIndex(p => p.Id == padId);
        if (index < 0 || Pads.Count <= 1)
        {
            return null;
        }

        var pads = Pads.Where(p => p.Id != padId).ToList();
        var selected = SelectedId;
        if (selected == padId)
        {
            selected = pads[Math.Min(index, pads.Count - 1)].Id;
        }

        return new ScratchpadDocument(pads, selected);
    }

    /// <summary>Clears texts left unedited longer than the retention period (tab and name are kept).</summary>
    public ScratchpadDocument ApplyRetention(ScratchpadRetention retention, DateTimeOffset now)
    {
        if (retention == ScratchpadRetention.Never || !Pads.Any(p => ScratchpadRetentionRules.ShouldClear(p.ModifiedAt, now, retention)))
        {
            return this;
        }

        var pads = Pads.Select(p => ScratchpadRetentionRules.ShouldClear(p.ModifiedAt, now, retention) ? p with { Text = string.Empty, ModifiedAt = null } : p).ToList();
        return this with { Pads = pads };
    }

    private ScratchpadDocument Replace(Guid padId, Func<ScratchpadPad, ScratchpadPad> change)
    {
        var pads = Pads.Select(p => p.Id == padId ? change(p) : p).ToList();
        return this with { Pads = pads };
    }

    /// <summary>Same tabs, names, texts, times and selection.</summary>
    public bool ContentEquals(ScratchpadDocument? other) =>
        other is not null && other.SelectedId == SelectedId && other.Pads.SequenceEqual(Pads);
}

public static class ScratchpadNames
{
    /// <summary>
    /// Next default tab name (spec 07 §6.3): "base 1" when neither "base" nor
    /// "base 1" is used, else the first free "base 2"…"base 12", else
    /// "base &lt;count+1&gt;". An unnumbered "base" counts as slot 1.
    /// </summary>
    public static string NextPadName(IEnumerable<string> existing, string baseName)
    {
        var used = existing.ToHashSet(StringComparer.Ordinal);
        if (!used.Contains(baseName) && !used.Contains($"{baseName} 1"))
        {
            return $"{baseName} 1";
        }

        for (var n = 2; n <= ScratchpadDocument.MaxPads; n++)
        {
            var candidate = $"{baseName} {n}";
            if (!used.Contains(candidate))
            {
                return candidate;
            }
        }

        return $"{baseName} {used.Count + 1}";
    }

    /// <summary>Every whitespace/newline run → one space, trimmed, cut to 40 characters (never inside a surrogate pair).</summary>
    public static string CleanName(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(name.Length);
        var inSpace = false;
        foreach (var c in name)
        {
            if (char.IsWhiteSpace(c))
            {
                inSpace = true;
                continue;
            }

            if (inSpace && builder.Length > 0)
            {
                builder.Append(' ');
            }

            inSpace = false;
            builder.Append(c);
        }

        var cleaned = builder.ToString();
        if (cleaned.Length <= ScratchpadDocument.MaxNameLength)
        {
            return cleaned;
        }

        var cut = ScratchpadDocument.MaxNameLength;
        if (char.IsHighSurrogate(cleaned[cut - 1]))
        {
            cut--;
        }

        return cleaned[..cut].TrimEnd();
    }

    /// <summary>
    /// Suggested export name "&lt;tab name&gt; &lt;yyyy-MM-dd&gt;.&lt;ext&gt;": the tab name cleaned
    /// as for renaming, characters Windows forbids in file names replaced by "-".
    /// </summary>
    public static string ExportFileName(string tabName, DateTime localDate, string extension = "txt")
    {
        var name = CleanName(tabName);
        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            builder.Append(c is '/' or ':' or '\\' or '*' or '?' or '"' or '<' or '>' or '|' || char.IsControl(c) ? '-' : c);
        }

        var safe = builder.ToString().Trim().TrimEnd('.');
        if (safe.Length == 0)
        {
            safe = "Scratchpad";
        }

        return $"{safe} {localDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.{extension}";
    }
}

public static class ScratchpadRetentionRules
{
    public static TimeSpan? MaxIdle(ScratchpadRetention retention) => retention switch
    {
        ScratchpadRetention.Day => TimeSpan.FromSeconds(86_400),
        ScratchpadRetention.Week => TimeSpan.FromSeconds(604_800),
        ScratchpadRetention.Month => TimeSpan.FromSeconds(2_592_000),
        _ => null,
    };

    /// <summary>Clear when a period is chosen and now − modifiedAt is strictly longer. Missing or future times never clear.</summary>
    public static bool ShouldClear(DateTimeOffset? modifiedAt, DateTimeOffset now, ScratchpadRetention retention)
    {
        if (MaxIdle(retention) is not { } limit || modifiedAt is not { } modified || modified > now)
        {
            return false;
        }

        return now - modified > limit;
    }
}

public sealed class ScratchpadDecodeException(string message) : Exception(message);

/// <summary>
/// <c>Scratchpad.json</c> (spec 07 §5.3): <c>{"pads":[{"id","name","text","modifiedAt"?}],"selectedID"}</c>.
/// Ids are written as uppercase UUIDs and <c>modifiedAt</c> as seconds since
/// 2001-01-01 UTC (the macOS reference date), so the file is the same on both
/// platforms. A missing <c>pads</c> or <c>selectedID</c> fails the whole load.
/// </summary>
public static class ScratchpadCodec
{
    /// <summary>The Apple reference date; Unix = value + 978,307,200.</summary>
    public static readonly DateTimeOffset ReferenceDate = new(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static byte[] Encode(ScratchpadDocument document)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("pads");
            foreach (var pad in document.Pads)
            {
                writer.WriteStartObject();
                writer.WriteString("id", FormatId(pad.Id));
                writer.WriteString("name", pad.Name);
                writer.WriteString("text", pad.Text);
                if (pad.ModifiedAt is { } modified)
                {
                    writer.WriteNumber("modifiedAt", Math.Round((modified - ReferenceDate).TotalSeconds, 3));
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteString("selectedID", FormatId(document.SelectedId));
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    /// <summary>Decodes without cleaning. Throws <see cref="ScratchpadDecodeException"/> for anything malformed.</summary>
    public static ScratchpadDocument Decode(ReadOnlySpan<byte> utf8)
    {
        JsonDocument json;
        try
        {
            json = JsonDocument.Parse(utf8.ToArray());
        }
        catch (JsonException ex)
        {
            throw new ScratchpadDecodeException($"Not JSON: {ex.Message}");
        }

        using (json)
        {
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new ScratchpadDecodeException("Root is not an object.");
            }

            if (!root.TryGetProperty("pads", out var padsElement) || padsElement.ValueKind != JsonValueKind.Array)
            {
                throw new ScratchpadDecodeException("Missing pads.");
            }

            if (!root.TryGetProperty("selectedID", out var selectedElement) || selectedElement.ValueKind != JsonValueKind.String)
            {
                throw new ScratchpadDecodeException("Missing selectedID.");
            }

            var pads = new List<ScratchpadPad>();
            foreach (var element in padsElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    throw new ScratchpadDecodeException("A pad is not an object.");
                }

                var id = element.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String && Guid.TryParse(idElement.GetString(), out var parsed)
                    ? parsed
                    : Guid.Empty;
                var name = ReadString(element, "name");
                var text = ReadString(element, "text");
                DateTimeOffset? modifiedAt = null;
                if (element.TryGetProperty("modifiedAt", out var modifiedElement) && modifiedElement.ValueKind == JsonValueKind.Number
                    && modifiedElement.TryGetDouble(out var seconds) && double.IsFinite(seconds) && Math.Abs(seconds) < 1e11)
                {
                    modifiedAt = ReferenceDate.AddSeconds(seconds);
                }

                pads.Add(new ScratchpadPad(id, name, text, modifiedAt));
            }

            var selected = Guid.TryParse(selectedElement.GetString(), out var selectedId) ? selectedId : Guid.Empty;
            return new ScratchpadDocument(pads, selected);
        }
    }

    /// <summary>
    /// Normalizes a decoded document: first 12 tabs, repeated or missing ids
    /// replaced/dropped, names cleaned (empty → next default name), no time on
    /// empty tabs, a valid selection, and a fresh document for an empty list.
    /// </summary>
    public static ScratchpadDocument Clean(ScratchpadDocument document, string baseName)
    {
        var seen = new HashSet<Guid>();
        var pads = new List<ScratchpadPad>();
        foreach (var pad in document.Pads)
        {
            if (pads.Count >= ScratchpadDocument.MaxPads)
            {
                break;
            }

            var id = pad.Id;
            if (id == Guid.Empty)
            {
                id = Guid.NewGuid();
            }
            else if (!seen.Add(id))
            {
                continue;
            }

            seen.Add(id);
            pads.Add(pad with
            {
                Id = id,
                Name = ScratchpadNames.CleanName(pad.Name),
                ModifiedAt = pad.Text.Length == 0 ? null : pad.ModifiedAt,
            });
        }

        if (pads.Count == 0)
        {
            return ScratchpadDocument.Fresh(baseName);
        }

        for (var i = 0; i < pads.Count; i++)
        {
            if (pads[i].Name.Length == 0)
            {
                var others = pads.Where((_, j) => j != i).Select(p => p.Name).Where(n => n.Length > 0);
                pads[i] = pads[i] with { Name = ScratchpadNames.NextPadName(others, baseName) };
            }
        }

        var selected = pads.Any(p => p.Id == document.SelectedId) ? document.SelectedId : pads[0].Id;
        return new ScratchpadDocument(pads, selected);
    }

    public static string FormatId(Guid id) => id.ToString("D").ToUpperInvariant();

    private static string ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
}
