// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using System.Text.Json.Nodes;
using Rivet.Core.App;

namespace Rivet.Core.Settings;

/// <summary>
/// Settings export/import as JSON:
/// <code>{"backupVersion":1,"appVersion":"…","platform":"windows","settingsSchemaVersion":1,"settings":{…}}</code>
/// Only explicitly saved values travel; machine-specific state never does.
/// Import replaces every portable value, keeps this PC's machine state, and
/// the caller then relaunches the app so every service starts clean.
/// </summary>
public static class SettingsBackup
{
    public const int FormatVersion = 1;

    /// <summary>Keys that are machine state even without a definition being loaded (by prefix).</summary>
    private static readonly string[] MachineStatePrefixes = ["update", "settingsWindow", "startupDidNotFinish"];

    private static readonly Dictionary<string, Func<JsonNode?, JsonNode?>> ExportSanitizers = new(StringComparer.Ordinal);

    /// <summary>
    /// Rewrites a value before it is exported (e.g. strips absolute image paths
    /// that only exist on this PC). Return null to leave the key out.
    /// </summary>
    public static void RegisterExportSanitizer(string key, Func<JsonNode?, JsonNode?> sanitize)
    {
        lock (ExportSanitizers)
        {
            ExportSanitizers[key] = sanitize;
        }
    }

    public static bool IsMachineState(string key) =>
        SettingDefinition.Find(key)?.IsMachineState == true
        || MachineStatePrefixes.Any(p => key.StartsWith(p, StringComparison.Ordinal));

    public static string Export(ISettingsStore store)
    {
        var settings = new JsonObject();
        foreach (var (key, value) in store.Snapshot().OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (IsMachineState(key))
            {
                continue;
            }

            Func<JsonNode?, JsonNode?>? sanitize;
            lock (ExportSanitizers)
            {
                ExportSanitizers.TryGetValue(key, out sanitize);
            }

            var exported = sanitize is null ? value?.DeepClone() : sanitize(value?.DeepClone());
            if (sanitize is null || exported is not null)
            {
                settings[key] = exported;
            }
        }

        var root = new JsonObject
        {
            ["backupVersion"] = FormatVersion,
            ["appVersion"] = AppIdentity.VersionString,
            ["platform"] = "windows",
            ["settingsSchemaVersion"] = SettingsStore.CurrentSchemaVersion,
            ["settings"] = settings,
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Reads a backup; returns null when the file is not a valid backup.</summary>
    public static IReadOnlyDictionary<string, JsonNode?>? Parse(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject root)
            {
                return null;
            }

            var version = root["backupVersion"]?.GetValue<int>() ?? 0;
            if (version is < 1 or > FormatVersion || root["settings"] is not JsonObject settings)
            {
                return null;
            }

            var result = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
            foreach (var (key, value) in settings)
            {
                if (!IsMachineState(key) && IsPlausible(key, value))
                {
                    result[key] = value?.DeepClone();
                }
            }

            return result;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>Replaces portable values with the backup's, keeping machine state.</summary>
    public static void Apply(SettingsStore store, IReadOnlyDictionary<string, JsonNode?> backup)
    {
        var merged = store.Snapshot()
            .Where(kv => IsMachineState(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        foreach (var (key, value) in backup)
        {
            merged[key] = value;
        }

        store.ReplaceAll(merged);
        store.Flush();
    }

    /// <summary>Type check against the loaded definition, when there is one.</summary>
    private static bool IsPlausible(string key, JsonNode? value)
    {
        var definition = SettingDefinition.Find(key);
        if (definition is null || value is null)
        {
            return true;
        }

        var decoded = definition.DecodeBoxed(value);
        return decoded is not null && (definition.BoxedDefault is null || decoded.GetType() == definition.BoxedDefault.GetType());
    }
}
