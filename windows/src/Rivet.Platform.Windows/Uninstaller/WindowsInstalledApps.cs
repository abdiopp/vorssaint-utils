// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Rivet.Core.Diagnostics;
using Rivet.Core.Maintenance.AppUpdates;
using Rivet.Core.Maintenance.Cleaner;
using Rivet.Core.Maintenance.Uninstaller;
using Rivet.Core.Platform;
using Rivet.Imaging.Skia;
using Windows.Win32;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;
using WinPackage = global::Windows.ApplicationModel.Package;
using WinPackageManager = global::Windows.Management.Deployment.PackageManager;
using WinSignatureKind = global::Windows.ApplicationModel.PackageSignatureKind;

namespace Rivet.Platform.Windows.Uninstaller;

/// <summary>
/// Installed apps: Add/Remove Programs entries of the 64-bit and 32-bit
/// machine keys and the user key (read through explicit registry views), and
/// the MSIX/Store packages installed for the current user. Frameworks,
/// resource packages and system-signed packages are left out.
/// </summary>
public sealed class WindowsInstalledApps(IKnownFolders folders) : IInstalledAppsProvider
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    private static readonly string[] ValueNames =
    [
        "DisplayName", "DisplayVersion", "Publisher", "InstallDate", "InstallLocation", "DisplayIcon", "EstimatedSize",
        "UninstallString", "QuietUninstallString", "SystemComponent", "WindowsInstaller", "ParentKeyName", "ReleaseType", "NoRemove",
    ];

    public IReadOnlyList<InstalledApp> Enumerate()
    {
        var roots = folders.Folders.TrustedInstallRoots().ToList();
        var own = AppContext.BaseDirectory;
        var apps = new List<InstalledApp>();
        foreach (var (hive, view, scope) in new[]
                 {
                     (RegistryHive.LocalMachine, RegistryView.Registry64, RegistryScope.Machine64),
                     (RegistryHive.LocalMachine, RegistryView.Registry32, RegistryScope.Machine32),
                     (RegistryHive.CurrentUser, RegistryView.Default, RegistryScope.User),
                 })
        {
            foreach (var entry in ReadEntries(hive, view, scope))
            {
                if (ArpRules.ToApp(entry, roots, own) is { } app && entry.Number("NoRemove") != 1)
                {
                    apps.Add(app);
                }
            }
        }

        apps.AddRange(Packages());
        return ArpRules.Deduplicate(apps);
    }

    public PixelBuffer? LoadIcon(InstalledApp app, int size = 32)
    {
        try
        {
            if (app.Kind == InstalledAppKind.Msix)
            {
                return app.Icon is { } logo ? LoadLogo(logo, size) : null;
            }

            var (path, index) = ParseIcon(app.Icon);
            if (path is null && app.InstallLocation is not null)
            {
                path = OnlineUpdateSource.MainExecutable(app);
                index = 0;
            }

            if (path is null || !File.Exists(path))
            {
                return null;
            }

            if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            {
                return LoadLogo(path, size);
            }

            return IconPixels.FromFile(path, index, size);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or COMException or ExternalException)
        {
            Log.Debug("uninstaller", $"No icon for {app.DisplayName}: {ex.Message}");
            return null;
        }
    }

    /// <summary>"C:\App\app.exe,-101" → (path, index); environment variables expanded.</summary>
    public static (string? Path, int Index) ParseIcon(string? displayIcon)
    {
        if (string.IsNullOrWhiteSpace(displayIcon))
        {
            return (null, 0);
        }

        var text = Environment.ExpandEnvironmentVariables(displayIcon.Trim());
        var index = 0;
        string path;
        if (text.StartsWith('"'))
        {
            var close = text.IndexOf('"', 1);
            path = close > 1 ? text[1..close] : text.Trim('"');
            var rest = close > 0 ? text[(close + 1)..].Trim() : string.Empty;
            if (rest.StartsWith(',') && int.TryParse(rest[1..].Trim(), out var parsed))
            {
                index = parsed;
            }
        }
        else
        {
            var comma = text.LastIndexOf(',');
            if (comma > 0 && int.TryParse(text[(comma + 1)..].Trim(), out var parsed))
            {
                index = parsed;
                path = text[..comma].Trim();
            }
            else
            {
                path = text;
            }
        }

        return (path.Length == 0 ? null : path, index);
    }

    private static IEnumerable<ArpEntry> ReadEntries(RegistryHive hive, RegistryView view, RegistryScope scope)
    {
        var entries = new List<ArpEntry>();
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = root.OpenSubKey(UninstallPath);
            if (uninstall is null)
            {
                return entries;
            }

            foreach (var name in uninstall.GetSubKeyNames())
            {
                try
                {
                    using var key = uninstall.OpenSubKey(name);
                    if (key is null)
                    {
                        continue;
                    }

                    var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    foreach (var valueName in ValueNames)
                    {
                        var value = key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                        if (value is not null)
                        {
                            values[valueName] = value is string text && valueName is "InstallLocation" or "DisplayIcon"
                                ? Environment.ExpandEnvironmentVariables(text)
                                : value;
                        }
                    }

                    entries.Add(new ArpEntry(name, scope, values));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    Log.Debug("uninstaller", $"Cannot read uninstall entry {name}: {ex.Message}");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log.Warn("uninstaller", $"Cannot read {hive}\\{UninstallPath} ({view}).", ex);
        }

        return entries;
    }

    private static List<InstalledApp> Packages()
    {
        var apps = new List<InstalledApp>();
        IEnumerable<WinPackage> packages;
        try
        {
            packages = new WinPackageManager().FindPackagesForUser(string.Empty).ToList();
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
        {
            Log.Warn("uninstaller", "Cannot list installed packages.", ex);
            return apps;
        }

        foreach (var package in packages)
        {
            try
            {
                if (package.IsFramework || package.IsResourcePackage || package.SignatureKind == WinSignatureKind.System)
                {
                    continue;
                }

                var name = package.DisplayName;
                if (string.IsNullOrWhiteSpace(name) || name.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var id = package.Id;
                var version = $"{id.Version.Major}.{id.Version.Minor}.{id.Version.Build}.{id.Version.Revision}";
                string? location = null;
                try
                {
                    location = package.InstalledPath;
                }
                catch (Exception ex) when (ex is COMException or InvalidOperationException)
                {
                }

                string? logo = null;
                try
                {
                    logo = package.Logo is { IsFile: true } uri ? uri.LocalPath : null;
                }
                catch (Exception ex) when (ex is COMException or InvalidOperationException)
                {
                }

                apps.Add(new InstalledApp
                {
                    Key = "msix:" + id.FamilyName,
                    Kind = InstalledAppKind.Msix,
                    DisplayName = name.Trim(),
                    Publisher = package.PublisherDisplayName,
                    Version = version,
                    InstallDate = DateOnly.FromDateTime(package.InstalledDate.LocalDateTime),
                    InstallLocation = location,
                    Icon = logo,
                    PackageFullName = id.FullName,
                    PackageFamilyName = id.FamilyName,
                });
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
            {
                Log.Debug("uninstaller", $"Skipped a package: {ex.Message}");
            }
        }

        return apps;
    }

    /// <summary>
    /// Package logos name a base file ("Assets\StoreLogo.png") while the
    /// package ships scaled variants ("StoreLogo.scale-200.png"): the closest
    /// existing one is used.
    /// </summary>
    private static PixelBuffer? LoadLogo(string path, int size)
    {
        var file = path;
        if (!File.Exists(file))
        {
            var folder = Path.GetDirectoryName(path);
            var stem = Path.GetFileNameWithoutExtension(path);
            if (folder is null || !Directory.Exists(folder))
            {
                return null;
            }

            file = Directory.EnumerateFiles(folder, stem + ".*" + Path.GetExtension(path))
                .OrderBy(f => f.Contains("scale-200", StringComparison.OrdinalIgnoreCase) ? 0 : f.Contains("scale-100", StringComparison.OrdinalIgnoreCase) ? 1 : 2)
                .FirstOrDefault() ?? string.Empty;
            if (file.Length == 0)
            {
                return null;
            }
        }

        using var image = SkiaConvert.LoadImage(file, size * 2);
        if (image is null)
        {
            return null;
        }

        using var resized = SkiaConvert.Resize(image, size, size);
        return SkiaConvert.ToPixelBuffer(resized);
    }
}

/// <summary>Icons from executables and .ico files as premultiplied BGRA pixels.</summary>
internal static unsafe class IconPixels
{
    public static PixelBuffer? FromFile(string path, int index, int size)
    {
        Span<HICON> icons = stackalloc HICON[1];
        var count = PInvoke.ExtractIconEx(path, index, icons, default);
        var large = icons[0];
        if (count == 0 || large.IsNull)
        {
            return null;
        }

        try
        {
            return FromIcon(large);
        }
        finally
        {
            PInvoke.DestroyIcon(large);
        }
    }

    private static PixelBuffer? FromIcon(HICON icon)
    {
        ICONINFO info;
        if (!PInvoke.GetIconInfo(icon, &info))
        {
            return null;
        }

        try
        {
            if (info.hbmColor.IsNull)
            {
                return null;
            }

            BITMAP bitmap;
            if (PInvoke.GetObject(new HGDIOBJ(info.hbmColor.Value), sizeof(BITMAP), &bitmap) == 0 || bitmap.bmWidth <= 0 || bitmap.bmHeight <= 0)
            {
                return null;
            }

            var width = bitmap.bmWidth;
            var height = bitmap.bmHeight;
            var pixels = ReadBits(info.hbmColor, width, height);
            if (pixels is null)
            {
                return null;
            }

            var hasAlpha = false;
            for (var i = 3; i < pixels.Length; i += 4)
            {
                if (pixels[i] != 0)
                {
                    hasAlpha = true;
                    break;
                }
            }

            if (!hasAlpha && !info.hbmMask.IsNull && ReadBits(info.hbmMask, width, height) is { } mask)
            {
                // Old icons: the AND mask says which pixels are transparent.
                for (var i = 0; i < pixels.Length; i += 4)
                {
                    pixels[i + 3] = mask[i] == 0 ? (byte)255 : (byte)0;
                }
            }
            else if (!hasAlpha)
            {
                for (var i = 3; i < pixels.Length; i += 4)
                {
                    pixels[i] = 255;
                }
            }

            // Premultiply for the shared pixel format.
            for (var i = 0; i < pixels.Length; i += 4)
            {
                var a = pixels[i + 3];
                if (a == 255)
                {
                    continue;
                }

                pixels[i] = (byte)(pixels[i] * a / 255);
                pixels[i + 1] = (byte)(pixels[i + 1] * a / 255);
                pixels[i + 2] = (byte)(pixels[i + 2] * a / 255);
            }

            return new PixelBuffer(width, height, pixels);
        }
        finally
        {
            if (!info.hbmColor.IsNull)
            {
                PInvoke.DeleteObject(new HGDIOBJ(info.hbmColor.Value));
            }

            if (!info.hbmMask.IsNull)
            {
                PInvoke.DeleteObject(new HGDIOBJ(info.hbmMask.Value));
            }
        }
    }

    private static byte[]? ReadBits(HBITMAP bitmap, int width, int height)
    {
        var header = new BITMAPINFO();
        header.bmiHeader.biSize = (uint)sizeof(BITMAPINFOHEADER);
        header.bmiHeader.biWidth = width;
        header.bmiHeader.biHeight = -height;
        header.bmiHeader.biPlanes = 1;
        header.bmiHeader.biBitCount = 32;
        header.bmiHeader.biCompression = 0; // BI_RGB
        var pixels = new byte[width * height * 4];
        var dc = PInvoke.GetDC(default);
        try
        {
            fixed (byte* data = pixels)
            {
                return PInvoke.GetDIBits(dc, bitmap, 0, (uint)height, data, &header, DIB_USAGE.DIB_RGB_COLORS) == 0 ? null : pixels;
            }
        }
        finally
        {
            PInvoke.ReleaseDC(default, dc);
        }
    }
}
