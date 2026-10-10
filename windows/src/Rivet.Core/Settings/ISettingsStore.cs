// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json.Nodes;

namespace Rivet.Core.Settings;

public sealed class SettingChangedEventArgs(string key) : EventArgs
{
    public string Key { get; } = key;
}

public interface ISettingsStore
{
    /// <summary>The effective value: the saved one if present, else the default.</summary>
    T Get<T>(Setting<T> setting);

    /// <summary>Saves a value (sanitized). Writing the current saved value again is a no-op.</summary>
    void Set<T>(Setting<T> setting, T value);

    /// <summary>Whether a value was explicitly saved (as opposed to coming from the default).</summary>
    bool IsSaved(string key);

    /// <summary>Forgets the saved value so the default applies again.</summary>
    void Reset(string key);

    /// <summary>Raw saved JSON value, or null when not saved.</summary>
    JsonNode? GetRaw(string key);

    /// <summary>Writes a raw JSON value, used by backup import and migrations.</summary>
    void SetRaw(string key, JsonNode? value);

    /// <summary>A copy of every saved value.</summary>
    IReadOnlyDictionary<string, JsonNode?> Snapshot();

    /// <summary>Raised after any saved value changes (on the writing thread).</summary>
    event EventHandler<SettingChangedEventArgs>? Changed;

    /// <summary>Calls <paramref name="callback"/> whenever one of <paramref name="keys"/> changes.</summary>
    IDisposable Observe(IEnumerable<string> keys, Action callback);

    /// <summary>Writes pending changes to disk now.</summary>
    void Flush();
}

public static class SettingsStoreExtensions
{
    public static IDisposable Observe(this ISettingsStore store, string key, Action callback) =>
        store.Observe([key], callback);

    public static IDisposable Observe(this ISettingsStore store, Action callback, params SettingDefinition[] settings) =>
        store.Observe(settings.Select(s => s.Key), callback);

    public static bool IsSaved(this ISettingsStore store, SettingDefinition setting) => store.IsSaved(setting.Key);
}
