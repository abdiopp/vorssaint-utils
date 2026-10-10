// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Rivet.Core.Platform;

namespace Rivet.Platform.Windows.Shell;

public sealed class WindowsPlatformInfo : IPlatformInfo
{
    public bool IsWindows => true;

    public Version OsVersion { get; } = Environment.OSVersion.Version;

    public bool IsElevated { get; } = Environment.IsPrivilegedProcess;

    public string Architecture { get; } = RuntimeInformation.OSArchitecture switch
    {
        System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
        _ => "x64",
    };

    public string OsDescription
    {
        get
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var display = key?.GetValue("DisplayVersion") as string ?? key?.GetValue("ReleaseId") as string ?? string.Empty;
            var name = OsVersion.Build >= 22000 ? "Windows 11" : "Windows 10";
            return $"{name} {display} ({OsVersion.Major}.{OsVersion.Minor}.{OsVersion.Build})".Replace("  ", " ");
        }
    }
}
