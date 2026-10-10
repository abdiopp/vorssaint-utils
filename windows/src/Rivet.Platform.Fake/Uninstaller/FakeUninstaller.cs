// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Extensions.DependencyInjection;
using Rivet.Core.Diagnostics;
using Rivet.Core.Maintenance.Cleaner;
using Rivet.Core.Maintenance.Uninstaller;
using Rivet.Core.Modules;
using Rivet.Core.Platform;
using Rivet.Imaging.Skia;
using Rivet.Platform.Fake.Cleaner;
using SkiaSharp;

namespace Rivet.Platform.Fake.Uninstaller;

/// <summary>Sample installed apps, with drawn icons so lists look real in the development build.</summary>
public sealed class FakeInstalledApps : IInstalledAppsProvider
{
    private readonly object _gate = new();
    private readonly HashSet<string> _removed = new(StringComparer.Ordinal);
    private readonly List<InstalledApp> _apps =
    [
        Win32("ContosoStudio", "Contoso Studio", "Contoso Ltd.", "4.2.1", @"C:\Program Files\Contoso\Studio", 1_230_000_000, "2025-04-18", quiet: true),
        Win32("ContosoPaint", "Contoso Paint", "Contoso Ltd.", "2.0", @"C:\Program Files\Contoso\Paint", 312_000_000, "2024-11-02"),
        Win32("Mozilla Firefox 131.0.2 (x64 en-US)", "Mozilla Firefox (x64 en-US)", "Mozilla", "131.0.2", @"C:\Program Files\Mozilla Firefox", 260_000_000, "2026-09-30"),
        Win32("7-Zip", "7-Zip 24.08 (x64)", "Igor Pavlov", "24.08", @"C:\Program Files\7-Zip", 6_100_000, "2024-08-12"),
        Win32("{771FD6B0-FA20-440A-A002-3B3BAC16DC50}_is1", "Microsoft Visual Studio Code (User)", "Microsoft Corporation", "1.94.2", @"C:\Users\Alex\AppData\Local\Programs\Microsoft VS Code", 412_000_000, "2026-10-01", user: true),
        Win32("Spotify", "Spotify", "Spotify AB", "1.2.45.454", @"C:\Users\Alex\AppData\Roaming\Spotify", 380_000_000, "2026-06-11", user: true),
        Win32("ZoomUMX", "Zoom Workplace", "Zoom Video Communications, Inc.", "6.1.11", @"C:\Users\Alex\AppData\Roaming\Zoom\bin", 290_000_000, "2026-03-04", user: true),
        new InstalledApp
        {
            Key = "arp:Machine64:{8F1D2B1E-1C2B-4B4B-9A9A-0123456789AB}", Kind = InstalledAppKind.Msi, DisplayName = "Contoso Helper Service",
            Publisher = "Contoso Ltd.", Version = "3.1.0", ProductCode = "{8F1D2B1E-1C2B-4B4B-9A9A-0123456789AB}", SizeBytes = 18_400_000,
            InstallDate = new DateOnly(2023, 5, 9), Scope = RegistryScope.Machine64, RegistryKeyName = "{8F1D2B1E-1C2B-4B4B-9A9A-0123456789AB}",
        },
        new InstalledApp
        {
            Key = "msix:Fabrikam.Notes_8wekyb3d8bbwe", Kind = InstalledAppKind.Msix, DisplayName = "Fabrikam Notes", Publisher = "Fabrikam",
            Version = "3.4.12.0", InstallDate = new DateOnly(2026, 2, 20), PackageFullName = "Fabrikam.Notes_3.4.12.0_x64__8wekyb3d8bbwe",
            PackageFamilyName = "Fabrikam.Notes_8wekyb3d8bbwe",
        },
        new InstalledApp
        {
            Key = "msix:Microsoft.WindowsCalculator_8wekyb3d8bbwe", Kind = InstalledAppKind.Msix, DisplayName = "Windows Calculator", Publisher = "Microsoft Corporation",
            Version = "11.2405.2.0", InstallDate = new DateOnly(2025, 12, 1), PackageFullName = "Microsoft.WindowsCalculator_11.2405.2.0_x64__8wekyb3d8bbwe",
            PackageFamilyName = "Microsoft.WindowsCalculator_8wekyb3d8bbwe",
        },
    ];

    public IReadOnlyList<InstalledApp> Enumerate()
    {
        lock (_gate)
        {
            return _apps.Where(a => !_removed.Contains(a.Key)).ToList();
        }
    }

    public PixelBuffer? LoadIcon(InstalledApp app, int size = 32)
    {
        var hue = (uint)app.DisplayName.Aggregate(17, (h, c) => (h * 31) + c) % 360;
        var info = new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        using var fill = new SKPaint { Color = SKColor.FromHsl(hue, 62, 48), IsAntialias = true };
        canvas.DrawRoundRect(new SKRect(1, 1, size - 1, size - 1), size * 0.22f, size * 0.22f, fill);
        using var font = new SKFont(SKTypeface.Default, size * 0.55f) { Embolden = true };
        using var text = new SKPaint { Color = SKColors.White, IsAntialias = true };
        var letter = app.DisplayName[..1].ToUpperInvariant();
        var width = font.MeasureText(letter);
        canvas.DrawText(letter, (size - width) / 2, size * 0.7f, font, text);
        using var image = surface.Snapshot();
        return SkiaConvert.ToPixelBuffer(image);
    }

    internal void MarkRemoved(InstalledApp app)
    {
        lock (_gate)
        {
            _removed.Add(app.Key);
        }
    }

    internal bool IsRemoved(InstalledApp app)
    {
        lock (_gate)
        {
            return _removed.Contains(app.Key);
        }
    }

    private static InstalledApp Win32(string key, string name, string publisher, string version, string location, long size, string date, bool user = false, bool quiet = false) => new()
    {
        Key = $"arp:{(user ? "User" : "Machine64")}:{key}",
        Kind = InstalledAppKind.Win32,
        DisplayName = name,
        Publisher = publisher,
        Version = version,
        InstallLocation = location,
        SizeBytes = size,
        InstallDate = DateOnly.Parse(date, System.Globalization.CultureInfo.InvariantCulture),
        UninstallString = $"\"{location}\\unins000.exe\"",
        QuietUninstallString = quiet ? $"\"{location}\\unins000.exe\" /VERYSILENT" : null,
        Scope = user ? RegistryScope.User : RegistryScope.Machine64,
        RegistryKeyName = key,
    };
}

/// <summary>Pretends to run uninstallers (a short wait) and keeps a small registry for the leftover scan.</summary>
public sealed class FakeUninstallerPlatform(FakeInstalledApps apps, InMemoryFileSystem fs) : IUninstallerPlatform
{
    private readonly object _gate = new();
    private readonly HashSet<string> _keys = new(StringComparer.OrdinalIgnoreCase)
    {
        @"HKEY_CURRENT_USER\Software\Contoso",
        @"HKEY_CURRENT_USER\Software\Contoso\Studio",
        @"HKEY_CURRENT_USER\Software\Contoso\Paint",
        @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\App Paths\studio.exe",
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Contoso",
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Contoso\Studio",
    };

    public bool IsElevated => false;

    public async Task<UninstallRunResult> RunAsync(InstalledApp app, bool quiet, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        try
        {
            for (var step = 1; step <= 6; step++)
            {
                await Task.Delay(300, cancellationToken).ConfigureAwait(false);
                progress?.Report(step / 6.0);
            }
        }
        catch (OperationCanceledException)
        {
            return new UninstallRunResult(UninstallRunStatus.Cancelled);
        }

        apps.MarkRemoved(app);
        if (app.InstallLocation is { } location)
        {
            // Like many uninstallers, it leaves a plugin folder behind.
            foreach (var entry in fs.List(location).Where(e => !e.Name.Equals("plugins", StringComparison.OrdinalIgnoreCase)))
            {
                fs.Remove(entry.Path);
            }
        }

        Log.Info("uninstaller", $"[fake] uninstalled {app.DisplayName}");
        return new UninstallRunResult(UninstallRunStatus.Finished, 0);
    }

    public bool IsStillInstalled(InstalledApp app) => !apps.IsRemoved(app);

    public IReadOnlyList<string> SubKeyNames(RegistryKeyRef key)
    {
        lock (_gate)
        {
            var prefix = key.FullPath + "\\";
            return _keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(k => k[prefix.Length..].Split('\\')[0])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public bool KeyExists(RegistryKeyRef key)
    {
        lock (_gate)
        {
            return _keys.Contains(key.FullPath);
        }
    }

    public RegKeySnapshot? Snapshot(RegistryKeyRef key) =>
        KeyExists(key) ? new RegKeySnapshot(key.FullPath, [new RegValue("LastProject", 1, @"C:\Users\Alex\Documents\Demo.cstudio")], []) : null;

    public bool DeleteKey(RegistryKeyRef key)
    {
        lock (_gate)
        {
            _keys.RemoveWhere(k => k.Equals(key.FullPath, StringComparison.OrdinalIgnoreCase) || k.StartsWith(key.FullPath + "\\", StringComparison.OrdinalIgnoreCase));
        }

        Log.Info("uninstaller", $"[fake] deleted {key.DisplayPath}");
        return true;
    }

    public IReadOnlyList<ScheduledTaskInfo> ScheduledTasks() =>
        [new(@"\Contoso\Studio Update", ["\"C:\\Program Files\\Contoso\\Studio\\studio.exe\" --check-updates"])];

    public IReadOnlyList<ServiceInfo> Services() =>
        [new("ContosoStudioSvc", "Contoso Studio Licensing", "\"C:\\Program Files\\Contoso\\Studio\\license-svc.exe\"")];
}

public sealed class FakeUninstallerRegistrar : IPlatformRegistrar
{
    public void Register(IServiceCollection services)
    {
        services.AddSingleton<FakeInstalledApps>();
        services.AddSingleton<IInstalledAppsProvider>(sp => sp.GetRequiredService<FakeInstalledApps>());
        services.AddSingleton<IUninstallerPlatform, FakeUninstallerPlatform>();
    }
}
