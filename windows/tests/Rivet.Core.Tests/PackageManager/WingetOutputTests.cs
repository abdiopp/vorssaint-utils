// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Maintenance.PackageManager;
using Xunit;

namespace Rivet.Core.Tests.PackageManager;

public class WingetOutputTests
{
    // `winget upgrade --include-unknown` as captured with redirected output (120 columns):
    // spinner frames before the header, a truncated name, version and id, a Store row,
    // the summary line and a second table of packages that need explicit targeting.
    private const string UpgradeTable = """
        Name                                    Id                                       Version         Available       Source
        ------------------------------------------------------------------------------------------------------------------------
        Microsoft Edge                          Microsoft.Edge                           129.0.2792.65   129.0.2792.79   winget
        Microsoft Visual C++ 2015-2022 Redistr… Microsoft.VCRedist.2015+.x64             14.36.32532.0   14.40.33810.0   winget
        PowerToys (Preview)                     Microsoft.PowerToys                      0.84.1          0.85.0          winget
        Spotify                                 Spotify.Spotify                          1.2.45.454.gc1… 1.2.47.366.gc4… winget
        Windows Terminal                        9N0DX20HK701                             1.20.11781.0    1.21.2361.0     msstore
        Contoso Studio Enterprise Edition 2024  Contoso.StudioEnterpriseEditionWithAddi… Unknown         24.2.1          winget
        6 upgrades available.

        The following packages have an upgrade available, but require explicit targeting for upgrade:
        Name    Id              Version  Available Source
        -------------------------------------------------
        Discord Discord.Discord 1.0.9163 1.0.9164  winget
        """;

    private const string ListTable = """
        Name                                   Id                                                   Version          Source
        -------------------------------------------------------------------------------------------------------------------
        Microsoft Edge                         Microsoft.Edge                                       129.0.2792.79    winget
        Microsoft Edge Update                  Microsoft Edge Update                                1.3.195.19
        Git                                    Git.Git                                              2.46.0           winget
        Windows Calculator                     Microsoft.WindowsCalculator_8wekyb3d8bbwe            11.2405.2.0
        Contoso Helper                         ARP\Machine\X64\{8F1D2B1E-1C2B-4B4B-9A9A-0123456789… 3.1
        Visual Studio Code                     Microsoft.VisualStudioCode                           1.93.1           winget
        """;

    private const string SearchTable = """
        Name                        Id                          Version   Match            Source
        -----------------------------------------------------------------------------------------
        Visual Studio Code          Microsoft.VisualStudioCode  1.93.1    Moniker: vscode  winget
        VSCodium                    VSCodium.VSCodium           1.93.1.2… Tag: vscode      winget
        Visual Studio Code Insiders Microsoft.VisualStudioCode… 1.94.0                     winget
        """;

    // Japanese UI: wide characters take two terminal cells, so columns are sliced by cells.
    private const string JapaneseUpgradeTable = """
        名前                 ID                       バージョン   利用可能     ソース
        ------------------------------------------------------------------------------
        微信                 Tencent.WeChat           3.9.10.27    3.9.11.17    winget
        秀丸エディタ (64ビ…  Hidemaru.Hidemaru        9.25         9.35         winget
        Git                  Git.Git                  2.45.2       2.46.0       winget
        """;

    private const string GermanListTable = """
        Name                   ID                     Version       Verfügbar     Quelle
        --------------------------------------------------------------------------------
        Mozilla Firefox (x64 … Mozilla.Firefox.de     130.0         130.0.1       winget
        7-Zip 24.08 (x64)      7zip.7zip              24.08                       winget
        Zoom Workplace         Zoom.Zoom              6.1.11        6.2.0         winget
        """;

    [Fact]
    public void Upgrade_table_reads_columns_from_the_header_positions()
    {
        var output = "   - \r   \\ \r   | \r" + UpgradeTable.Replace("\n", "\r\n");
        var packages = WingetOutput.ParsePackages(output, WingetTableKind.Upgrade);

        Assert.Equal(7, packages.Count);
        var edge = packages[0];
        Assert.Equal("Microsoft Edge", edge.Name);
        Assert.Equal("Microsoft.Edge", edge.Id);
        Assert.Equal("129.0.2792.65", edge.Version);
        Assert.Equal("129.0.2792.79", edge.Available);
        Assert.Equal("winget", edge.Source);
        Assert.False(edge.RequiresExplicitUpgrade);

        var redist = packages[1];
        Assert.True(redist.NameTruncated);
        Assert.False(redist.IdTruncated);
        Assert.Equal("Microsoft.VCRedist.2015+.x64", redist.Id);

        Assert.True(packages[3].VersionTruncated);
        Assert.True(packages[4].IsStore);
        Assert.Equal("9N0DX20HK701", packages[4].Id);

        var contoso = packages[5];
        Assert.True(contoso.IdTruncated);
        Assert.Equal("Unknown", contoso.Version);
        Assert.Equal("24.2.1", contoso.Available);

        var discord = packages[6];
        Assert.Equal("Discord.Discord", discord.Id);
        Assert.True(discord.RequiresExplicitUpgrade);
    }

    [Fact]
    public void Summary_and_later_messages_never_become_rows()
    {
        var output = UpgradeTable + "\n\n1 package(s) have version numbers that cannot be determined. Use --include-unknown to see all results.\n";
        var packages = WingetOutput.ParsePackages(output, WingetTableKind.Upgrade);
        Assert.DoesNotContain(packages, p => p.Name.Contains("upgrades available", StringComparison.Ordinal));
        Assert.DoesNotContain(packages, p => p.Name.Contains("package(s)", StringComparison.Ordinal));
        Assert.Equal(7, packages.Count);
    }

    [Fact]
    public void A_summary_wider_than_a_narrow_name_column_is_not_a_row()
    {
        const string narrow = """
            Name Id      Version Available Source
            -------------------------------------
            Git  Git.Git 2.45.2  2.46.0    winget
            Zoom Zoom.Zo… 6.1.11 6.2.0     winget
            2 upgrades available.
            """;
        var packages = WingetOutput.ParsePackages(narrow, WingetTableKind.Upgrade);
        Assert.Equal(["Git.Git"], packages.Select(p => p.Id));

        const string aligned = """
            Name Id       Version Available Source
            --------------------------------------
            Git  Git.Git  2.45.2  2.46.0    winget
            Zoom Zoom.Zo… 6.1.11  6.2.0     winget
            2 upgrades available.
            """;
        Assert.Equal(["Git.Git", "Zoom.Zo…"], WingetOutput.ParsePackages(aligned, WingetTableKind.Upgrade).Select(p => p.Id));
    }

    [Fact]
    public void List_without_available_column_keeps_local_ids_with_spaces()
    {
        var packages = WingetOutput.ParsePackages(ListTable, WingetTableKind.List);
        Assert.Equal(6, packages.Count);

        var update = packages[1];
        Assert.Equal("Microsoft Edge Update", update.Id);
        Assert.True(update.IsLocal);
        Assert.Null(update.Available);

        Assert.Equal("winget", packages[2].Source);
        Assert.Null(packages[2].Available);
        Assert.True(packages[4].IdTruncated);
        Assert.StartsWith("ARP\\Machine\\X64\\", packages[4].Id, StringComparison.Ordinal);
        Assert.Equal("1.93.1", packages[5].Version);
    }

    [Fact]
    public void Search_reads_the_match_column()
    {
        var packages = WingetOutput.ParsePackages(SearchTable, WingetTableKind.Search);
        Assert.Equal(3, packages.Count);
        Assert.Equal("Moniker: vscode", packages[0].Match);
        Assert.Equal("winget", packages[0].Source);
        Assert.Equal("Tag: vscode", packages[1].Match);
        Assert.Null(packages[2].Match);
        Assert.True(packages[2].IdTruncated);
    }

    [Fact]
    public void Wide_characters_take_two_cells()
    {
        var packages = WingetOutput.ParsePackages(JapaneseUpgradeTable, WingetTableKind.Upgrade);
        Assert.Equal(3, packages.Count);
        Assert.Equal("微信", packages[0].Name);
        Assert.Equal("Tencent.WeChat", packages[0].Id);
        Assert.Equal("3.9.11.17", packages[0].Available);
        Assert.Equal("winget", packages[0].Source);
        Assert.True(packages[1].NameTruncated);
        Assert.Equal("Hidemaru.Hidemaru", packages[1].Id);
        Assert.Equal("9.35", packages[1].Available);
    }

    [Fact]
    public void Localized_headers_are_recognized()
    {
        var packages = WingetOutput.ParsePackages(GermanListTable, WingetTableKind.List);
        Assert.Equal(3, packages.Count);
        Assert.Equal("130.0.1", packages[0].Available);
        Assert.Equal("winget", packages[0].Source);
        Assert.Null(packages[1].Available);
        Assert.True(packages[0].NameTruncated);
    }

    [Fact]
    public void Unknown_headers_fall_back_to_the_column_content()
    {
        var table = GermanListTable.Replace("Verfügbar", "Disponib.").Replace("Quelle", "Origem");
        var packages = WingetOutput.ParsePackages(table, WingetTableKind.List);
        Assert.Equal("6.2.0", packages[2].Available);
        Assert.Equal("winget", packages[2].Source);
    }

    [Fact]
    public void No_table_means_no_packages()
    {
        Assert.Empty(WingetOutput.ParsePackages("No installed package found matching input criteria.\r\n", WingetTableKind.Upgrade));
        Assert.Empty(WingetOutput.ParsePackages(string.Empty, WingetTableKind.List));
    }

    [Fact]
    public void Clean_lines_strip_ansi_carriage_return_frames_and_backspaces()
    {
        var lines = WingetOutput.CleanLines("\u001b[2K\u001b[1G  ██████▒▒▒▒  40%\r  ██████████  100%\r\nab\bc\n\u001b[32mDone\u001b[0m");
        Assert.Equal("  ██████████  100%", lines[0]);
        Assert.Equal("ac", lines[1]);
        Assert.Equal("Done", lines[2]);
    }

    [Fact]
    public void Truncated_ids_are_completed_only_by_a_unique_export_match()
    {
        var packages = WingetOutput.ParsePackages(UpgradeTable, WingetTableKind.Upgrade);
        var export = """
            {
              "$schema" : "https://aka.ms/winget-packages.schema.2.0.json",
              "Sources" : [
                { "Packages" : [
                    { "PackageIdentifier" : "Contoso.StudioEnterpriseEditionWithAddins2024", "Version" : "Unknown" },
                    { "PackageIdentifier" : "Microsoft.Edge", "Version" : "129.0.2792.65" } ],
                  "SourceDetails" : { "Argument" : "https://cdn.winget.microsoft.com/cache", "Identifier" : "Microsoft.Winget.Source_8wekyb3d8bbwe", "Name" : "winget", "Type" : "Microsoft.PreIndexed.Package" } }
              ],
              "WinGetVersion" : "1.8.1911"
            }
            """;
        var resolved = WingetOutput.ResolveTruncatedIds(packages, WingetOutput.ParseExport(export));
        Assert.Equal("Contoso.StudioEnterpriseEditionWithAddins2024", resolved[5].Id);
        Assert.False(resolved[5].IdTruncated);

        var ambiguous = WingetOutput.ResolveTruncatedIds(packages, [("Contoso.StudioEnterpriseEditionWithAddins2024", "winget"), ("Contoso.StudioEnterpriseEditionWithAddins2025", "winget")]);
        Assert.True(ambiguous[5].IdTruncated);
    }

    [Fact]
    public void Show_output_is_read_as_fields()
    {
        const string show = """
            Found Git [Git.Git]
            Version: 2.46.0
            Publisher: The Git Development Community
            Publisher Url: https://gitforwindows.org/
            Author: Johannes Schindelin
            Moniker: git
            Description:
              Git for Windows focuses on offering a lightweight, native set of tools
              that bring the full feature set of the Git SCM to Windows.
            Homepage: https://gitforwindows.org/
            License: GPL-2.0
            Tags:
              bash
              git
            Installer:
              Installer Type: inno
              Installer Url: https://github.com/git-for-windows/git/releases/download/v2.46.0.windows.1/Git-2.46.0-64-bit.exe
            """;
        var details = WingetOutput.ParseShow(show, "Git.Git");
        Assert.Equal("Git", details.Name);
        Assert.Equal("2.46.0", details.Version);
        Assert.Equal("The Git Development Community", details.Publisher);
        Assert.Equal("https://gitforwindows.org/", details.Homepage);
        Assert.Equal("GPL-2.0", details.License);
        Assert.StartsWith("Git for Windows focuses", details.Description, StringComparison.Ordinal);
        Assert.EndsWith("to Windows.", details.Description, StringComparison.Ordinal);
        Assert.Contains(details.Fields, f => f.Key == "Tags" && f.Value == "bash git");
        Assert.DoesNotContain(details.Fields, f => f.Key == "Installer");
    }

    [Fact]
    public void Sources_and_version_parse()
    {
        const string sources = """
            Name    Argument                                      Explicit
            --------------------------------------------------------------
            msstore https://storeedgefd.dsx.mp.microsoft.com/v9.0 false
            winget  https://cdn.winget.microsoft.com/cache        false
            """;
        var list = WingetOutput.ParseSources(sources);
        Assert.Equal(2, list.Count);
        Assert.Equal("winget", list[1].Name);
        Assert.Equal("https://cdn.winget.microsoft.com/cache", list[1].Argument);
        Assert.Equal(new Version(1, 8, 1911), WingetOutput.ParseVersion("v1.8.1911\r\n"));
        Assert.Null(WingetOutput.ParseVersion("not winget"));
    }

    [Theory]
    [InlineData("Git.Git", true)]
    [InlineData("Microsoft.VCRedist.2015+.x64", true)]
    [InlineData("9NBLGGH4NNS1", true)]
    [InlineData("ARP\\Machine\\X64\\{8F1D2B1E-1C2B-4B4B-9A9A-0123456789AB}", true)]
    [InlineData("--force", false)]
    [InlineData("", false)]
    [InlineData("Contoso.Studio…", false)]
    [InlineData("a..b", false)]
    [InlineData("bad\"quote", false)]
    public void Package_ids_are_validated_before_reaching_argv(string id, bool valid) =>
        Assert.Equal(valid, PackageArguments.IsValidId(id));

    [Fact]
    public void Search_text_is_cleaned()
    {
        Assert.Equal("vscode", PackageArguments.CleanQuery("  vscode "));
        Assert.Null(PackageArguments.CleanQuery("-q"));
        Assert.Null(PackageArguments.CleanQuery("   "));
        Assert.Null(PackageArguments.CleanQuery(new string('a', 101)));
    }

    [Fact]
    public void Display_width_slicing_handles_cjk_and_combining_marks()
    {
        Assert.Equal(4, TextColumns.Width("微信"));
        Assert.Equal(1, TextColumns.Width("é"));
        Assert.Equal("信", TextColumns.Slice("微信Git", 2, 4));
        Assert.Equal(new[] { 0, 6, 10 }, TextColumns.TokenStarts("Name  Id  Version"));
    }
}
