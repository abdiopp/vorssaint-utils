// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Rivet.Core.Diagnostics;

namespace Rivet.Core.Settings;

/// <summary>
/// A named preference with its default and sanitizer. Definitions live in
/// static classes next to the feature that owns them; the stored file only
/// ever contains values the user (or a migration) explicitly wrote.
/// </summary>
public abstract class SettingDefinition
{
    private static readonly ConcurrentDictionary<string, SettingDefinition> Registry = new(StringComparer.Ordinal);

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = null,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    protected SettingDefinition(string key, Type valueType, bool isMachineState)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Setting key must not be empty.", nameof(key));
        }

        Key = key;
        ValueType = valueType;
        IsMachineState = isMachineState;

        var existing = Registry.GetOrAdd(key, this);
        if (!ReferenceEquals(existing, this) && existing.ValueType != valueType)
        {
            Log.Warn("settings", $"Key '{key}' defined twice with different types ({existing.ValueType.Name}, {valueType.Name}).");
        }
    }

    public string Key { get; }

    public Type ValueType { get; }

    /// <summary>Machine-specific state: never exported in a settings backup.</summary>
    public bool IsMachineState { get; }

    public abstract object? BoxedDefault { get; }

    internal abstract object? DecodeBoxed(JsonNode? node);

    internal abstract JsonNode? EncodeBoxed(object? value);

    /// <summary>All definitions whose declaring class has been initialized so far.</summary>
    public static IReadOnlyCollection<SettingDefinition> All => (IReadOnlyCollection<SettingDefinition>)Registry.Values;

    public static SettingDefinition? Find(string key) => Registry.GetValueOrDefault(key);

    public override string ToString() => Key;
}

public sealed class Setting<T> : SettingDefinition
{
    private readonly Func<T, T>? _sanitize;

    public Setting(string key, T defaultValue, Func<T, T>? sanitize = null, bool machineState = false)
        : base(key, typeof(T), machineState)
    {
        _sanitize = sanitize;
        Default = defaultValue;
    }

    public T Default { get; }

    public override object? BoxedDefault => Default;

    public T Sanitize(T value) => _sanitize is null ? value : _sanitize(value);

    /// <summary>Reads a stored value; anything unreadable falls back to the default.</summary>
    public T Decode(JsonNode? node)
    {
        if (node is null)
        {
            return Default;
        }

        try
        {
            var value = node.Deserialize<T>(JsonOptions);
            return value is null ? Default : Sanitize(value);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or NotSupportedException)
        {
            return Default;
        }
    }

    public JsonNode? Encode(T value) => JsonSerializer.SerializeToNode(value, JsonOptions);

    internal override object? DecodeBoxed(JsonNode? node) => Decode(node);

    internal override JsonNode? EncodeBoxed(object? value) => Encode(value is T typed ? typed : Default);
}

/// <summary>Common sanitizers for setting definitions.</summary>
public static class Sanitize
{
    public static Func<int, int> Clamp(int min, int max) => v => Math.Clamp(v, min, max);

    public static Func<double, double> Clamp(double min, double max) =>
        v => double.IsFinite(v) ? Math.Clamp(v, min, max) : min;

    public static Func<T, T> OneOf<T>(T fallback, params T[] allowed) =>
        v => allowed.Contains(v) ? v : fallback;

    public static Func<string, string> OneOfStrings(string fallback, params string[] allowed) =>
        v => allowed.Contains(v, StringComparer.Ordinal) ? v : fallback;

    public static Func<string, string> MaxLength(int length) =>
        v => v.Length <= length ? v : v[..length];
}
