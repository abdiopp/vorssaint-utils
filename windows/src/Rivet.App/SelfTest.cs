// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.App;
using Rivet.Core.Features;
using Rivet.Core.Localization;
using Rivet.Core.Settings;
using Rivet.Core.Shortcuts;

namespace Rivet.App;

/// <summary>
/// <c>--selftest</c>: quick health checks for CI. Prints <c>SELFTEST WARNING: …</c>
/// lines, then <c>SELFTEST OK</c> (exit 0) or <c>SELFTEST FAILED: …</c> (exit 1).
/// </summary>
internal static class SelfTest
{
    public static int Run()
    {
        var failures = new List<string>();
        void Check(bool condition, string name)
        {
            if (!condition)
            {
                failures.Add(name);
            }
        }

        try
        {
            var store = SettingsStore.InMemory();
            store.Set(ShellSettings.HasOnboarded, true);
            Check(store.Get(ShellSettings.HasOnboarded) && store.IsSaved(ShellSettings.HasOnboarded.Key), "settings round trip");

            var localizer = new Localizer(AppLanguage.EnUS);
            Check(localizer.Get("hub.pageTitle") != "hub.pageTitle", "strings load");
            foreach (var language in AppLanguages.All)
            {
                localizer.Language = language;
                Check(localizer.Get("Strings.panelQuit") != "Strings.panelQuit", $"strings {language.Code()}");
            }

            foreach (var feature in FeatureCatalog.All)
            {
                localizer.Language = AppLanguage.EnUS;
                Check(localizer.Get(feature.TitleKey) != feature.TitleKey, $"title of {feature.Id}");
                Check(localizer.Get(feature.DescriptionKey) != feature.DescriptionKey, $"description of {feature.Id}");
            }

            Check(KeyChord.TryParse("ctrl+alt+win:0x4B", out var chord) && chord.ToStorageString() == "ctrl+alt+win:0x4B", "shortcut storage");
            Check(Shell.TrayIconRenderer.Render(lightTaskbar: false).Count == Shell.TrayIconRenderer.Sizes.Length, "tray icon renders");
        }
        catch (Exception ex)
        {
            failures.Add($"exception: {ex.Message}");
        }

        if (failures.Count == 0)
        {
            Console.WriteLine($"SELFTEST OK ({AppIdentity.DisplayName} {AppIdentity.VersionString})");
            return 0;
        }

        Console.WriteLine($"SELFTEST FAILED: {string.Join("; ", failures)}");
        return 1;
    }
}
