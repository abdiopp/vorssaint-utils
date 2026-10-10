// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rivet.Core.Diagnostics;
using Rivet.Core.Util;

namespace Rivet.Core.Settings;

/// <summary>
/// JSON-file settings store. Only explicitly saved values are written; the
/// file looks like <c>{"schemaVersion":1,"values":{...}}</c>. Writes are
/// debounced and atomic (temp file, then replace). The file is treated as
/// user-editable: every read goes through the setting's sanitizer.
/// </summary>
public sealed class SettingsStore : ISettingsStore, IDisposable
{
    public const int CurrentSchemaVersion = 1;

    private readonly object _gate = new();
    private readonly Dictionary<string, JsonNode?> _values = new(StringComparer.Ordinal);
    private readonly Dictionary<string, object?> _decoded = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Observer>> _observers = new(StringComparer.Ordinal);
    private readonly string? _path;
    private readonly Debouncer? _saveDebouncer;
    private bool _dirty;

    private SettingsStore(string? path)
    {
        _path = path;
        if (path is not null)
        {
            _saveDebouncer = new Debouncer(TimeSpan.FromMilliseconds(400), Flush);
        }
    }

    public event EventHandler<SettingChangedEventArgs>? Changed;

    public int SchemaVersion { get; private set; } = CurrentSchemaVersion;

    /// <summary>A store that never touches disk (tests, previews).</summary>
    public static SettingsStore InMemory() => new(null);

    /// <summary>Loads the store from <paramref name="path"/>; a corrupt file is set aside and replaced.</summary>
    public static SettingsStore Load(string path)
    {
        var store = new SettingsStore(path);
        if (!File.Exists(path))
        {
            return store;
        }

        try
        {
            var root = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8)) as JsonObject
                ?? throw new JsonException("Root is not an object.");
            store.SchemaVersion = root["schemaVersion"]?.GetValue<int>() ?? CurrentSchemaVersion;
            if (root["values"] is JsonObject values)
            {
                foreach (var (key, value) in values)
                {
                    store._values[key] = value?.DeepClone();
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            var backup = $"{path}.corrupt-{DateTime.Now:yyyyMMddHHmmss}";
            Log.Error("settings", $"Settings file is unreadable; moved to {backup}.", ex);
            try
            {
                File.Move(path, backup, overwrite: true);
            }
            catch (IOException)
            {
            }
        }

        return store;
    }

    public T Get<T>(Setting<T> setting)
    {
        lock (_gate)
        {
            if (_decoded.TryGetValue(setting.Key, out var cached) && cached is T typed)
            {
                return typed;
            }

            var value = _values.TryGetValue(setting.Key, out var node) ? setting.Decode(node) : setting.Default;
            _decoded[setting.Key] = value;
            return value;
        }
    }

    public void Set<T>(Setting<T> setting, T value)
    {
        var sanitized = setting.Sanitize(value);
        var node = setting.Encode(sanitized);
        lock (_gate)
        {
            if (_values.TryGetValue(setting.Key, out var existing) && JsonNode.DeepEquals(existing, node))
            {
                return;
            }

            _values[setting.Key] = node;
            _decoded[setting.Key] = sanitized;
            _dirty = true;
        }

        OnChanged(setting.Key);
    }

    public bool IsSaved(string key)
    {
        lock (_gate)
        {
            return _values.ContainsKey(key);
        }
    }

    public void Reset(string key)
    {
        lock (_gate)
        {
            if (!_values.Remove(key))
            {
                return;
            }

            _decoded.Remove(key);
            _dirty = true;
        }

        OnChanged(key);
    }

    public JsonNode? GetRaw(string key)
    {
        lock (_gate)
        {
            return _values.TryGetValue(key, out var node) ? node?.DeepClone() : null;
        }
    }

    public void SetRaw(string key, JsonNode? value)
    {
        lock (_gate)
        {
            if (_values.TryGetValue(key, out var existing) && JsonNode.DeepEquals(existing, value))
            {
                return;
            }

            _values[key] = value?.DeepClone();
            _decoded.Remove(key);
            _dirty = true;
        }

        OnChanged(key);
    }

    public IReadOnlyDictionary<string, JsonNode?> Snapshot()
    {
        lock (_gate)
        {
            return _values.ToDictionary(kv => kv.Key, kv => kv.Value?.DeepClone(), StringComparer.Ordinal);
        }
    }

    /// <summary>Replaces every saved value at once (backup import). Raises one change per affected key.</summary>
    public void ReplaceAll(IReadOnlyDictionary<string, JsonNode?> values)
    {
        HashSet<string> affected;
        lock (_gate)
        {
            affected = new HashSet<string>(_values.Keys, StringComparer.Ordinal);
            affected.UnionWith(values.Keys);
            _values.Clear();
            _decoded.Clear();
            foreach (var (key, value) in values)
            {
                _values[key] = value?.DeepClone();
            }

            _dirty = true;
        }

        foreach (var key in affected)
        {
            OnChanged(key);
        }
    }

    public IDisposable Observe(IEnumerable<string> keys, Action callback)
    {
        var observer = new Observer(this, keys.Distinct(StringComparer.Ordinal).ToArray(), callback);
        lock (_gate)
        {
            foreach (var key in observer.Keys)
            {
                if (!_observers.TryGetValue(key, out var list))
                {
                    _observers[key] = list = [];
                }

                list.Add(observer);
            }
        }

        return observer;
    }

    public void Flush()
    {
        if (_path is null)
        {
            return;
        }

        string json;
        lock (_gate)
        {
            if (!_dirty)
            {
                return;
            }

            var values = new JsonObject();
            foreach (var (key, value) in _values.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                values[key] = value?.DeepClone();
            }

            var root = new JsonObject
            {
                ["schemaVersion"] = SchemaVersion,
                ["values"] = values,
            };
            json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            _dirty = false;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, json, new UTF8Encoding(false));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("settings", "Could not write the settings file.", ex);
            lock (_gate)
            {
                _dirty = true;
            }
        }
    }

    /// <summary>Records the schema version after migrations ran.</summary>
    public void SetSchemaVersion(int version)
    {
        lock (_gate)
        {
            if (SchemaVersion == version)
            {
                return;
            }

            SchemaVersion = version;
            _dirty = true;
        }

        _saveDebouncer?.Trigger();
    }

    private void OnChanged(string key)
    {
        _saveDebouncer?.Trigger();

        Observer[] observers;
        lock (_gate)
        {
            observers = _observers.TryGetValue(key, out var list) ? list.ToArray() : [];
        }

        foreach (var observer in observers)
        {
            try
            {
                observer.Callback();
            }
            catch (Exception ex)
            {
                Log.Error("settings", $"Observer of '{key}' failed.", ex);
            }
        }

        Changed?.Invoke(this, new SettingChangedEventArgs(key));
    }

    public void Dispose()
    {
        _saveDebouncer?.Dispose();
        Flush();
    }

    private sealed class Observer(SettingsStore store, string[] keys, Action callback) : IDisposable
    {
        public string[] Keys { get; } = keys;

        public Action Callback { get; } = callback;

        public void Dispose()
        {
            lock (store._gate)
            {
                foreach (var key in Keys)
                {
                    if (store._observers.TryGetValue(key, out var list))
                    {
                        list.Remove(this);
                    }
                }
            }
        }
    }
}
