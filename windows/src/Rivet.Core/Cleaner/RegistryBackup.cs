// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Rivet.Core.Maintenance.Cleaner;

/// <summary>Registry value types as stored (REG_* numbers).</summary>
public enum RegValueKind
{
    None = 0,
    String = 1,
    ExpandString = 2,
    Binary = 3,
    DWord = 4,
    MultiString = 7,
    QWord = 11,
}

/// <summary>One registry value with its raw type number.</summary>
public sealed record RegValue(string Name, int Kind, object? Data);

/// <summary>A registry key and everything below it, captured for a backup.</summary>
public sealed record RegKeySnapshot(string Path, IReadOnlyList<RegValue> Values, IReadOnlyList<RegKeySnapshot> SubKeys);

/// <summary>
/// Writes registry backups in the format regedit imports ("Windows Registry
/// Editor Version 5.00", UTF-16 LE with BOM). Registry items cannot go to the
/// Recycle Bin, so every removal writes one of these first; double-clicking
/// the file (or <c>reg import</c>) restores it.
/// </summary>
public static class RegFile
{
    public const string Header = "Windows Registry Editor Version 5.00";

    /// <summary>Full key paths use the long hive names regedit writes.</summary>
    public static string LongHive(string path)
    {
        var slash = path.IndexOf('\\');
        var hive = slash < 0 ? path : path[..slash];
        var rest = slash < 0 ? string.Empty : path[slash..];
        var full = hive.ToUpperInvariant() switch
        {
            "HKCU" => "HKEY_CURRENT_USER",
            "HKLM" => "HKEY_LOCAL_MACHINE",
            "HKCR" => "HKEY_CLASSES_ROOT",
            "HKU" => "HKEY_USERS",
            _ => hive,
        };
        return full + rest;
    }

    public static string ForKeys(IEnumerable<RegKeySnapshot> keys)
    {
        var builder = new StringBuilder();
        builder.Append(Header).Append("\r\n");
        foreach (var key in keys)
        {
            AppendKey(builder, key);
        }

        return builder.ToString();
    }

    /// <summary>A file restoring single values (Run entries) in their key.</summary>
    public static string ForValues(string keyPath, IEnumerable<RegValue> values) =>
        ForKeys([new RegKeySnapshot(keyPath, values.ToList(), [])]);

    /// <summary>The bytes to write: UTF-16 LE with BOM, as regedit exports.</summary>
    public static byte[] Encode(string text) => [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(text)];

    public static string FormatValue(RegValue value)
    {
        var name = value.Name.Length == 0 ? "@" : Quote(value.Name);
        return name + "=" + FormatData(value);
    }

    private static void AppendKey(StringBuilder builder, RegKeySnapshot key)
    {
        builder.Append("\r\n[").Append(LongHive(key.Path)).Append("]\r\n");
        foreach (var value in key.Values)
        {
            builder.Append(FormatValue(value)).Append("\r\n");
        }

        foreach (var sub in key.SubKeys)
        {
            AppendKey(builder, sub);
        }
    }

    private static string FormatData(RegValue value)
    {
        switch (value.Kind)
        {
            case (int)RegValueKind.String when value.Data is string text && !text.Any(char.IsControl):
                return Quote(text);
            case (int)RegValueKind.String when value.Data is string text:
                return "hex(1):" + Hex(StringBytes(text));
            case (int)RegValueKind.DWord when value.Data is int or uint or long:
                // RegistryKey.GetValue returns DWORDs as signed ints.
                return "dword:" + unchecked((uint)Convert.ToInt64(value.Data, CultureInfo.InvariantCulture)).ToString("x8", CultureInfo.InvariantCulture);
            case (int)RegValueKind.QWord when value.Data is long or ulong or int:
                var q = new byte[8];
                BinaryPrimitives.WriteInt64LittleEndian(q, Convert.ToInt64(value.Data, CultureInfo.InvariantCulture));
                return "hex(b):" + Hex(q);
            case (int)RegValueKind.ExpandString when value.Data is string expand:
                return "hex(2):" + Hex(StringBytes(expand));
            case (int)RegValueKind.MultiString when value.Data is string[] lines:
                var multi = new List<byte>();
                foreach (var line in lines)
                {
                    multi.AddRange(StringBytes(line));
                }

                multi.AddRange([0, 0]);
                return "hex(7):" + Hex([.. multi]);
            case (int)RegValueKind.Binary when value.Data is byte[] bytes:
                return "hex:" + Hex(bytes);
            default:
                var raw = value.Data switch
                {
                    byte[] b => b,
                    string s => StringBytes(s),
                    null => [],
                    _ => Encoding.Unicode.GetBytes(Convert.ToString(value.Data, CultureInfo.InvariantCulture) ?? string.Empty),
                };
                return string.Create(CultureInfo.InvariantCulture, $"hex({value.Kind:x}):") + Hex(raw);
        }
    }

    /// <summary>UTF-16 LE text with its terminating NUL, as REG_SZ/EXPAND_SZ store it.</summary>
    private static byte[] StringBytes(string text) => [.. Encoding.Unicode.GetBytes(text), 0, 0];

    private static string Quote(string text) =>
        "\"" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static string Hex(byte[] bytes) =>
        string.Join(',', bytes.Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
}
