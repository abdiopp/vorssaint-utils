// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Features.Cleaner;
using Rivet.App.Tests.Maintenance;
using Rivet.Core.Maintenance.Cleaner;
using Rivet.Core.Maintenance.Uninstaller;
using Rivet.Core.Settings;
using Rivet.Platform.Fake.Cleaner;
using Xunit;

namespace Rivet.App.Tests.Cleaner;

public class CleanerViewTests
{
    [AvaloniaFact]
    public async Task Scan_of_the_sample_profile_follows_the_windows_rules()
    {
        var (_, cleaner) = Fresh();
        await cleaner.ScanAsync(attended: true);
        Assert.Equal(CleanerPhase.Results, cleaner.Phase);
        var items = cleaner.Items;

        // Edge is running: its cache is skipped and the scan says so.
        Assert.Contains("Microsoft Edge", cleaner.SkippedRunning);
        Assert.DoesNotContain(items, i => i.Path.Contains(@"\Edge\", StringComparison.Ordinal));
        // Document copies in INetCache, files Explorer holds open, fresh temp files and our own files are never listed.
        Assert.DoesNotContain(items, i => i.Path.Contains("Content.Outlook", StringComparison.Ordinal));
        Assert.DoesNotContain(items, i => i.Name == "thumbcache_idx.db");
        Assert.DoesNotContain(items, i => i.Name == "~DF3A1.tmp");
        Assert.DoesNotContain(items, i => i.Name.StartsWith("Rivet", StringComparison.Ordinal));
        // A startup entry on a removable drive proves nothing; one on C: with its program gone is orphaned.
        Assert.Contains(items, i => i.Kind == CleanerItemKind.StartupValue && i.Name == "OldUpdater" && cleaner.IsIncluded(i));
        Assert.DoesNotContain(items, i => i.Name == "PortableSync");
        Assert.DoesNotContain(items, i => i.Name == "OneDrive");
        // Leftovers start unchecked; the Recycle Bin is permanent and unchecked.
        Assert.All(items.Where(i => i.Category == CleanerCategory.Leftovers), i => Assert.False(cleaner.IsIncluded(i)));
        var bin = Assert.Single(items, i => i.Kind == CleanerItemKind.RecycleBin);
        Assert.True(bin.Permanent);
        Assert.False(cleaner.IsIncluded(bin));
        // Screenshots: only default names older than 30 days.
        Assert.Equal(["Screenshot (12).png", "Screenshot (13).png"], items.Where(i => i.Category == CleanerCategory.Screenshots).Select(i => i.Name).Order());
    }

    [AvaloniaFact]
    public async Task Panel_states_render()
    {
        foreach (var (theme, variant) in MaintenanceSnapshots.Themes)
        {
            var (services, cleaner) = Fresh();
            MaintenanceSnapshots.Panel(() => new CleanerView(services, compact: true), $"cleaner-panel-idle-{theme}", "Strings.cleanerName", "Sparkle", variant);

            await cleaner.ScanAsync(attended: true);
            CleanerView.ExpandGroups(CleanerGroup.SafeCaches, CleanerGroup.Leftovers);
            var results = MaintenanceSnapshots.Panel(() => new CleanerView(services, compact: true), $"cleaner-panel-results-{theme}", "Strings.cleanerName", "Sparkle", variant);
            Assert.True(File.Exists(results));

            foreach (var item in cleaner.Items)
            {
                cleaner.SetIncluded(item, item.Category == CleanerCategory.Caches && item.Recommended);
            }

            await cleaner.CleanSelectedAsync(escalate: true);
            Assert.Equal(CleanerPhase.Done, cleaner.Phase);
            Assert.True(cleaner.LastResult!.FreedBytes > 0);
            MaintenanceSnapshots.Panel(() => new CleanerView(services, compact: true), $"cleaner-panel-done-{theme}", "Strings.cleanerName", "Sparkle", variant);
        }

        CleanerView.ExpandGroups();
    }

    [AvaloniaFact]
    public async Task Settings_page_renders()
    {
        foreach (var (theme, variant) in MaintenanceSnapshots.Themes)
        {
            var (services, _) = Fresh();
            services.GetRequiredService<ISettingsStore>().Set(WhatsAppSettings.Enabled, true);
            var whatsApp = services.GetRequiredService<WhatsAppDownloadsService>();
            await whatsApp.ScanAsync();
            Assert.Contains(whatsApp.Candidates, c => c.Name == "Lease agreement.pdf" && c.Evidence == WhatsAppEvidence.WebMark);
            Assert.Contains(whatsApp.Candidates, c => c.Name.StartsWith("WhatsApp Image", StringComparison.Ordinal) && c.Evidence == WhatsAppEvidence.NameOnly);
            Assert.DoesNotContain(whatsApp.Candidates, c => c.Name == "Holiday photos.zip");
            Assert.DoesNotContain(whatsApp.Candidates.Where(c => c.Evidence == WhatsAppEvidence.NameOnly), whatsApp.IsSelected);
            MaintenanceSnapshots.SettingsPage(services, CleanerModule.PageId, $"settings-cleaner-{theme}", variant, height: 1700);
            services.GetRequiredService<ISettingsStore>().Set(WhatsAppSettings.Enabled, false);
        }
    }

    /// <summary>A Cleaner over a fresh copy of the sample profile, so tests never share state.</summary>
    private static (IServiceProvider Services, CleanerService Cleaner) Fresh()
    {
        var host = TestApp.Host.Services;
        var fs = new InMemoryFileSystem();
        var platform = new FakeCleanerPlatform(fs, host.GetRequiredService<IInstalledAppsProvider>());
        var recycler = new FakeRecycler(fs);
        var folders = new FakeKnownFolders();
        var settings = host.GetRequiredService<ISettingsStore>();
        var backups = Path.Combine(Path.GetTempPath(), "rivet-cleaner-backups-" + Guid.NewGuid().ToString("N"));
        var cleaner = new CleanerService(fs, platform, recycler, folders, settings, () => Directory.CreateDirectory(backups).FullName);
        var whatsApp = new WhatsAppDownloadsService(fs, new FakeWebMarkReader(), recycler, folders, settings);
        var services = new OverrideServices(host,
            (typeof(CleanerService), cleaner),
            (typeof(WhatsAppDownloadsService), whatsApp),
            (typeof(ICleanerFileSystem), fs),
            (typeof(ICleanerPlatform), platform));
        return (services, cleaner);
    }
}
