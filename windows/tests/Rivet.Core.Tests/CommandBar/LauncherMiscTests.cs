// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Launcher;
using Xunit;

namespace Rivet.Core.Tests.CommandBar;

/// <summary>Script dispatch (spec 06 §3.8.10, §7.6), home abbreviation and the Windows Settings table (§7.3).</summary>
public class LauncherMiscTests
{
    [Fact]
    public void Scripts_dispatch_by_extension_without_evaluating_arguments()
    {
        var ps = ScriptRunner.StartInfo(@"C:\s\hello.ps1", "a b; rm x", windows: true)!;
        Assert.Equal("powershell.exe", ps.FileName);
        Assert.Equal(["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", @"C:\s\hello.ps1", "a b; rm x"], ps.ArgumentList);

        var exe = ScriptRunner.StartInfo(@"C:\s\tool.exe", "x", windows: true)!;
        Assert.Equal(@"C:\s\tool.exe", exe.FileName);
        Assert.Equal(["x"], exe.ArgumentList);

        var cmd = ScriptRunner.StartInfo(@"C:\s\run.cmd", "plain words", windows: true)!;
        Assert.Equal("cmd.exe", cmd.FileName);
        Assert.Contains("\"plain words\"", cmd.Arguments, StringComparison.Ordinal);

        Assert.Null(ScriptRunner.StartInfo(@"C:\s\run.bat", "a & calc", windows: true));
        Assert.Null(ScriptRunner.StartInfo(@"C:\s\run.bat", "100%", windows: true));
        Assert.Null(ScriptRunner.StartInfo(@"C:\s\notes.txt", string.Empty, windows: true));
    }

    [Theory]
    [InlineData(@"C:\Users\me", @"C:\Users\me\Projects", @"~\Projects")]
    [InlineData(@"C:\Users\me", @"c:\users\me", "~")]
    [InlineData(@"C:\Users\me", @"C:\Users\meg\x", @"C:\Users\meg\x")]
    [InlineData(@"C:\Users\me", @"D:\Work", @"D:\Work")]
    public void Home_is_abbreviated(string home, string path, string expected) =>
        Assert.Equal(expected, CommandBarLinks.AbbreviateHome(path, home));

    [Fact]
    public void Windows_settings_pages_are_unique_and_openable()
    {
        var pages = WindowsSettingsPages.All;
        Assert.True(pages.Count >= 51);
        Assert.Equal(pages.Count, pages.Select(p => p.Key).Distinct().Count());
        Assert.Equal(pages.Count, pages.Select(p => p.RowId).Distinct().Count());
        Assert.All(pages, p => Assert.True(
            p.Uri.StartsWith("ms-settings:", StringComparison.Ordinal) || p.Uri.EndsWith(".cpl", StringComparison.Ordinal)
            || p.Uri.EndsWith(':') || p.Uri.EndsWith(".msc", StringComparison.Ordinal) || p.Uri.StartsWith("control", StringComparison.Ordinal),
            p.Uri));
        Assert.All(pages, p => Assert.Equal(CommandSource.MacSettings, CommandSources.Of(p.RowId)));
    }
}
