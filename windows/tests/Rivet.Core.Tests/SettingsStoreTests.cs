// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json.Nodes;
using Rivet.Core.Settings;
using Xunit;

namespace Rivet.Core.Tests;

public enum TestMode
{
    First,
    Second,
}

public class SettingsStoreTests
{
    private static readonly Setting<bool> Flag = new("test.flag", false);
    private static readonly Setting<int> Clamped = new("test.clamped", 5, Sanitize.Clamp(1, 10));
    private static readonly Setting<TestMode> Mode = new("test.mode", TestMode.First);
    private static readonly Setting<List<string>> Items = new("test.items", []);

    [Fact]
    public void Defaults_are_not_saved_until_written()
    {
        var store = SettingsStore.InMemory();
        Assert.False(store.Get(Flag));
        Assert.False(store.IsSaved(Flag.Key));
        store.Set(Flag, false);
        Assert.True(store.IsSaved(Flag.Key));
    }

    [Fact]
    public void Values_are_sanitized_on_write_and_read()
    {
        var store = SettingsStore.InMemory();
        store.Set(Clamped, 50);
        Assert.Equal(10, store.Get(Clamped));
        store.SetRaw(Clamped.Key, JsonValue.Create(-3));
        Assert.Equal(1, store.Get(Clamped));
    }

    [Fact]
    public void Unreadable_values_fall_back_to_the_default()
    {
        var store = SettingsStore.InMemory();
        store.SetRaw(Mode.Key, JsonValue.Create("noSuchMode"));
        Assert.Equal(TestMode.First, store.Get(Mode));
        store.SetRaw(Flag.Key, JsonValue.Create("not a bool"));
        Assert.False(store.Get(Flag));
    }

    [Fact]
    public void Enums_are_stored_as_camel_case_strings()
    {
        var store = SettingsStore.InMemory();
        store.Set(Mode, TestMode.Second);
        Assert.Equal("second", store.GetRaw(Mode.Key)!.GetValue<string>());
    }

    [Fact]
    public void Writing_the_same_value_does_not_notify()
    {
        var store = SettingsStore.InMemory();
        var count = 0;
        using var _ = store.Observe(Flag.Key, () => count++);
        store.Set(Flag, true);
        store.Set(Flag, true);
        Assert.Equal(1, count);
    }

    [Fact]
    public void Reset_restores_the_default_and_forgets_the_value()
    {
        var store = SettingsStore.InMemory();
        store.Set(Flag, true);
        store.Reset(Flag.Key);
        Assert.False(store.Get(Flag));
        Assert.False(store.IsSaved(Flag.Key));
    }

    [Fact]
    public void Store_round_trips_through_the_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rivet-tests-" + Guid.NewGuid());
        var path = Path.Combine(dir, "settings.json");
        try
        {
            using (var store = SettingsStore.Load(path))
            {
                store.Set(Flag, true);
                store.Set(Items, ["a", "b"]);
                store.Flush();
            }

            using var reloaded = SettingsStore.Load(path);
            Assert.True(reloaded.Get(Flag));
            Assert.Equal(["a", "b"], reloaded.Get(Items));
            Assert.False(reloaded.IsSaved(Clamped.Key));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Corrupt_file_is_set_aside()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rivet-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "settings.json");
        File.WriteAllText(path, "{ not json");
        try
        {
            using var store = SettingsStore.Load(path);
            Assert.False(store.Get(Flag));
            Assert.Contains(Directory.GetFiles(dir), f => f.Contains("corrupt", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Backup_skips_machine_state_and_round_trips()
    {
        var machine = new Setting<string>("test.machineOnly", "", machineState: true);
        var store = SettingsStore.InMemory();
        store.Set(Flag, true);
        store.Set(machine, "this-pc");
        var json = SettingsBackup.Export(store);
        Assert.DoesNotContain("test.machineOnly", json, StringComparison.Ordinal);

        var parsed = SettingsBackup.Parse(json);
        Assert.NotNull(parsed);
        var target = SettingsStore.InMemory();
        target.Set(machine, "other-pc");
        target.Set(Clamped, 3);
        SettingsBackup.Apply(target, parsed!);
        Assert.True(target.Get(Flag));
        Assert.Equal("other-pc", target.Get(machine));
        Assert.False(target.IsSaved(Clamped.Key));
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("{\"backupVersion\":99,\"settings\":{}}")]
    [InlineData("{\"backupVersion\":1}")]
    public void Invalid_backups_are_rejected(string json) => Assert.Null(SettingsBackup.Parse(json));
}
