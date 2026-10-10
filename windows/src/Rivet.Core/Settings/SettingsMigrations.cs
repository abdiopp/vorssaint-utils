// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Diagnostics;

namespace Rivet.Core.Settings;

/// <summary>
/// Versioned, idempotent migrations that run before anything reads settings.
/// Add a step with the next version number; never reorder or remove one.
/// </summary>
public static class SettingsMigrations
{
    private static readonly List<(int Version, string Name, Action<SettingsStore> Apply)> Steps = [];

    public static int LatestVersion => Steps.Count == 0 ? SettingsStore.CurrentSchemaVersion : Steps.Max(s => s.Version);

    public static void Register(int version, string name, Action<SettingsStore> apply) =>
        Steps.Add((version, name, apply));

    public static void Run(SettingsStore store)
    {
        foreach (var step in Steps.Where(s => s.Version > store.SchemaVersion).OrderBy(s => s.Version))
        {
            try
            {
                step.Apply(store);
                Log.Info("settings", $"Migration {step.Version} ({step.Name}) applied.");
            }
            catch (Exception ex)
            {
                Log.Error("settings", $"Migration {step.Version} ({step.Name}) failed.", ex);
            }

            store.SetSchemaVersion(step.Version);
        }
    }
}
