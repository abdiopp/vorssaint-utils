// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Maintenance.Uninstaller;
using Xunit;

namespace Rivet.Core.Tests.Uninstaller;

public class UninstallerRulesTests
{
    private static readonly string[] Roots = ["C:\\Program Files", "C:\\Program Files (x86)", "C:\\Users\\a\\AppData\\Local\\Programs"];

    [Fact]
    public void Hidden_updates_and_unremovable_entries_are_not_offered()
    {
        Assert.Null(ArpRules.ToApp(Entry("A", ("DisplayName", "Contoso"), ("SystemComponent", 1), ("UninstallString", "x.exe")), Roots));
        Assert.Null(ArpRules.ToApp(Entry("B", ("DisplayName", "KB123"), ("ParentKeyName", "Office"), ("UninstallString", "x.exe")), Roots));
        Assert.Null(ArpRules.ToApp(Entry("C", ("DisplayName", "Patch"), ("ReleaseType", "Security Update"), ("UninstallString", "x.exe")), Roots));
        Assert.Null(ArpRules.ToApp(Entry("D", ("DisplayName", "No way out")), Roots));
        Assert.Null(ArpRules.ToApp(Entry("E", ("UninstallString", "x.exe")), Roots));
        Assert.NotNull(ArpRules.ToApp(Entry("F", ("DisplayName", "Contoso"), ("UninstallString", "x.exe")), Roots));
    }

    [Fact]
    public void Msi_products_need_no_uninstall_string()
    {
        var app = ArpRules.ToApp(Entry("{8F1D2B1E-1C2B-4B4B-9A9A-0123456789AB}", ("DisplayName", "Contoso Tools"), ("WindowsInstaller", 1), ("EstimatedSize", 2048), ("InstallDate", "20250314")), Roots)!;
        Assert.Equal(InstalledAppKind.Msi, app.Kind);
        Assert.Equal("{8F1D2B1E-1C2B-4B4B-9A9A-0123456789AB}", app.ProductCode);
        Assert.Equal(2048L * 1024, app.SizeBytes);
        Assert.Equal(new DateOnly(2025, 3, 14), app.InstallDate);
        Assert.True(app.CanUninstall);
        Assert.True(app.HasQuietUninstall);
    }

    [Fact]
    public void Install_location_comes_from_the_entry_or_a_trusted_program_path()
    {
        var withLocation = ArpRules.ToApp(Entry("G", ("DisplayName", "Contoso"), ("UninstallString", "x.exe"), ("InstallLocation", "\"C:\\Program Files\\Contoso\\\"")), Roots)!;
        Assert.Equal("C:\\Program Files\\Contoso", withLocation.InstallLocation);

        var fromIcon = ArpRules.ToApp(Entry("H", ("DisplayName", "Contoso"), ("UninstallString", "\"C:\\Program Files\\Contoso\\uninstall\\unins000.exe\""), ("DisplayIcon", "C:\\Program Files\\Contoso\\bin\\contoso.exe,0")), Roots)!;
        Assert.Equal("C:\\Program Files\\Contoso", fromIcon.InstallLocation);

        var untrusted = ArpRules.ToApp(Entry("I", ("DisplayName", "Contoso"), ("UninstallString", "C:\\Windows\\Installer\\{X}\\setup.exe"), ("DisplayIcon", "C:\\Windows\\Installer\\{X}\\icon.ico")), Roots)!;
        Assert.Null(untrusted.InstallLocation);
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\Contoso\\uninstall.exe\" /S", "C:\\Program Files\\Contoso\\uninstall.exe", "/S")]
    [InlineData("C:\\Program Files\\Contoso\\uninstall.exe /S", "C:\\Program Files\\Contoso\\uninstall.exe", "/S")]
    [InlineData("C:\\PROGRA~1\\Contoso\\unins000.exe", "C:\\PROGRA~1\\Contoso\\unins000.exe", "")]
    [InlineData("\"C:\\ProgramData\\Package Cache\\{abc}\\setup.exe\" /uninstall /quiet", "C:\\ProgramData\\Package Cache\\{abc}\\setup.exe", "/uninstall /quiet")]
    [InlineData("rundll32.exe \"C:\\Program Files\\Contoso\\x.dll\",Uninstall", "rundll32.exe", "\"C:\\Program Files\\Contoso\\x.dll\",Uninstall")]
    public void Uninstall_strings_split_into_program_and_arguments(string command, string program, string arguments)
    {
        var app = new InstalledApp { Key = "k", Kind = InstalledAppKind.Win32, DisplayName = "Contoso", UninstallString = command };
        var invocation = ArpRules.Invocation(app, quiet: false, _ => false, s => s)!;
        Assert.Equal(program, invocation.FileName);
        Assert.Equal(arguments, invocation.Arguments);
        Assert.False(invocation.IsMsi);
    }

    [Fact]
    public void Unquoted_paths_prefer_an_existing_executable()
    {
        var app = new InstalledApp { Key = "k", Kind = InstalledAppKind.Win32, DisplayName = "x", UninstallString = "C:\\Apps\\my.exe tool\\remove.exe --all" };
        var invocation = ArpRules.Invocation(app, false, p => p == "C:\\Apps\\my.exe tool\\remove.exe", s => s)!;
        Assert.Equal("C:\\Apps\\my.exe tool\\remove.exe", invocation.FileName);
        Assert.Equal("--all", invocation.Arguments);
    }

    [Fact]
    public void Msi_uninstalls_use_x_with_the_product_code()
    {
        var modify = new InstalledApp { Key = "k", Kind = InstalledAppKind.Win32, DisplayName = "x", UninstallString = "MsiExec.exe /I{8f1d2b1e-1c2b-4b4b-9a9a-0123456789ab}" };
        var invocation = ArpRules.Invocation(modify, quiet: false, _ => false, s => s)!;
        Assert.True(invocation.IsMsi);
        Assert.Equal("msiexec.exe", invocation.FileName);
        Assert.Equal("/x {8F1D2B1E-1C2B-4B4B-9A9A-0123456789AB}", invocation.Arguments);

        var msi = new InstalledApp { Key = "k", Kind = InstalledAppKind.Msi, DisplayName = "x", ProductCode = "{8F1D2B1E-1C2B-4B4B-9A9A-0123456789AB}" };
        Assert.Equal("/x {8F1D2B1E-1C2B-4B4B-9A9A-0123456789AB} /qb", ArpRules.Invocation(msi, quiet: true, _ => false, s => s)!.Arguments);
    }

    [Fact]
    public void Quiet_mode_uses_the_quiet_uninstall_string()
    {
        var app = new InstalledApp { Key = "k", Kind = InstalledAppKind.Win32, DisplayName = "x", UninstallString = "\"C:\\A\\u.exe\"", QuietUninstallString = "\"C:\\A\\u.exe\" /VERYSILENT" };
        Assert.Equal("/VERYSILENT", ArpRules.Invocation(app, true, _ => false, s => s)!.Arguments);
        Assert.Equal(string.Empty, ArpRules.Invocation(app, false, _ => false, s => s)!.Arguments);
        Assert.Null(ArpRules.Invocation(app with { Kind = InstalledAppKind.Msix }, false, _ => false, s => s));
    }

    [Fact]
    public void Duplicate_registrations_collapse()
    {
        var a = new InstalledApp { Key = "1", Kind = InstalledAppKind.Win32, DisplayName = "Contoso", Version = "1.0", InstallLocation = "C:\\Program Files\\Contoso", UninstallString = "x" };
        var b = a with { Key = "2", InstallLocation = "c:\\program files\\contoso\\" };
        var c = a with { Key = "3", Version = "2.0" };
        Assert.Equal(["1", "3"], ArpRules.Deduplicate([a, b, c]).Select(x => x.Key));
    }

    [Theory]
    [InlineData("Mozilla Firefox (x64 en-US)", "Mozilla Firefox")]
    [InlineData("Contoso Studio 2024", "Contoso Studio")]
    [InlineData("Foo Nightly", "Foo")]
    [InlineData("Bar 1.2.3 (64-bit)", "Bar")]
    [InlineData("7-Zip", "7-Zip")]
    public void Display_names_lose_versions_and_qualifiers(string name, string expected) =>
        Assert.Equal(expected, AppTokens.CleanDisplayName(name));

    [Theory]
    [InlineData("Mozilla Corporation", "mozilla")]
    [InlineData("The Git Development Community", "git")]
    [InlineData("JetBrains s.r.o.", "jetbrains")]
    [InlineData("Google LLC", "google")]
    [InlineData("Discord Inc.", "discord")]
    [InlineData("", null)]
    public void Publishers_reduce_to_a_vendor_token(string publisher, string? expected) =>
        Assert.Equal(expected, AppTokens.PublisherToken(publisher));

    [Fact]
    public void Product_names_drop_the_vendor_prefix()
    {
        Assert.Equal("Firefox", AppTokens.ProductName("Mozilla Firefox (x64 en-US)", "mozilla"));
        Assert.Equal("Visual Studio Code", AppTokens.ProductName("Microsoft Visual Studio Code", "microsoft"));
        Assert.Null(AppTokens.ProductName("Firefox", "mozilla"));
    }

    [Fact]
    public void Names_match_tokens_exactly_or_by_long_prefix()
    {
        var tokens = new HashSet<string> { "firefox", "contosostudio", "abc" };
        Assert.Equal("firefox", AppTokens.Match("Firefox", tokens));
        Assert.Equal("contosostudio", AppTokens.Match("Contoso Studio Backups", tokens));
        Assert.Null(AppTokens.Match("abcdef", tokens)); // short tokens never match by prefix
        Assert.Null(AppTokens.Match("com.contosostudio.helper", tokens)); // reverse-DNS names need an exact match
        Assert.Null(AppTokens.Match("firefox_2024-01-01_crash", tokens));
        Assert.Null(AppTokens.Match(".firefox", tokens));
        Assert.Equal("firefox", AppTokens.Match("firefox.json", tokens));
    }

    [Fact]
    public void Role_words_are_never_tokens()
    {
        Assert.False(AppTokens.IsToken("helper"));
        Assert.False(AppTokens.IsToken("windows"));
        Assert.False(AppTokens.IsToken("ab"));
        Assert.False(AppTokens.IsToken("2024"));
        Assert.True(AppTokens.IsToken("firefox"));
        Assert.True(AppTokens.IsHelperExecutable("unins000.exe"));
        Assert.True(AppTokens.IsHelperExecutable("Update.exe"));
        Assert.False(AppTokens.IsHelperExecutable("Code.exe"));
    }

    [Fact]
    public void Error_report_folders_name_the_executable()
    {
        var executables = new HashSet<string> { "code.exe" };
        Assert.True(LeftoverScanner.IsReportFor("AppCrash_Code.exe_6bc1c5f3a7e1d1e0_9c1f_cab_0f5a", executables));
        Assert.True(LeftoverScanner.IsReportFor("AppHang_code.exe_1234", executables));
        Assert.False(LeftoverScanner.IsReportFor("AppCrash_codehelper.exe_1234", executables));
        Assert.False(LeftoverScanner.IsReportFor("Kernel_141_abc", executables));
    }

    [Fact]
    public void Registry_paths_show_the_32_bit_view()
    {
        var key = new RegistryKeyRef(RegistryKeyRef.LocalMachine, "SOFTWARE\\Contoso\\Studio", View32: true);
        Assert.Equal("HKEY_LOCAL_MACHINE\\SOFTWARE\\WOW6432Node\\Contoso\\Studio", key.FullPath);
        Assert.Equal("HKLM\\SOFTWARE\\WOW6432Node\\Contoso\\Studio", key.DisplayPath);
        Assert.Equal("HKCU\\Software\\Contoso", new RegistryKeyRef(RegistryKeyRef.CurrentUser, "Software\\Contoso").DisplayPath);
    }

    private static ArpEntry Entry(string key, params (string Name, object Value)[] values) =>
        new(key, RegistryScope.Machine64, values.ToDictionary(v => v.Name, v => (object?)v.Value));
}
