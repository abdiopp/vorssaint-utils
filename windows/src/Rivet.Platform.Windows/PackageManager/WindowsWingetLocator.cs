// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Extensions.DependencyInjection;
using Rivet.Core.Maintenance.PackageManager;
using Rivet.Core.Modules;

namespace Rivet.Platform.Windows.PackageManager;

/// <summary>
/// winget.exe is an App Installer execution alias in
/// %LOCALAPPDATA%\Microsoft\WindowsApps; PATH is searched as a fallback
/// (some managed PCs install it elsewhere).
/// </summary>
public sealed class WindowsWingetLocator : IWingetLocator
{
    public string? Locate()
    {
        var alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "winget.exe");
        if (File.Exists(alias))
        {
            return alias;
        }

        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(folder.Trim('"'), "winget.exe");
                if (Path.IsPathFullyQualified(candidate) && File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
            }
        }

        return null;
    }
}

public sealed class PackageManagerRegistrar : IPlatformRegistrar
{
    public void Register(IServiceCollection services) => services.AddSingleton<IWingetLocator, WindowsWingetLocator>();
}
