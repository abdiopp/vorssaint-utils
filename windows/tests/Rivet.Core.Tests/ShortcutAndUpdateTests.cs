// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Shortcuts;
using Rivet.Core.Update;
using Xunit;
using static Rivet.Core.Shortcuts.KeyModifiers;

namespace Rivet.Core.Tests;

public class ShortcutAndUpdateTests
{
    [Theory]
    [InlineData("ctrl+alt+win:0x4B", Control | Alt | Win, 0x4B)]
    [InlineData("win+ctrl+alt:0x4b", Control | Alt | Win, 0x4B)]
    [InlineData("shift:0x74", Shift, 0x74)]
    [InlineData(":0x2C", None, 0x2C)]
    public void Chords_parse_in_any_token_order(string text, KeyModifiers modifiers, int vk)
    {
        Assert.True(KeyChord.TryParse(text, out var chord));
        Assert.Equal(new KeyChord(modifiers, vk), chord);
    }

    [Fact]
    public void Chords_store_tokens_in_fixed_order()
    {
        Assert.Equal("ctrl+alt+shift+win:0x4B", new KeyChord(Win | Shift | Alt | Control, 0x4B).ToStorageString());
        Assert.Equal(string.Empty, KeyChord.None.ToStorageString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("ctrl+alt")]
    [InlineData("hyper:0x41")]
    [InlineData("ctrl:0xFFF")]
    public void Bad_chords_do_not_parse(string text) => Assert.False(KeyChord.TryParse(text, out _));

    [Fact]
    public void Validity_needs_ctrl_alt_or_win_unless_function_key()
    {
        Assert.True(new KeyChord(Control | Alt | Win, VirtualKeys.Letter('K')).IsValidGlobalShortcut);
        Assert.False(new KeyChord(Shift, VirtualKeys.Letter('K')).IsValidGlobalShortcut);
        Assert.True(new KeyChord(Shift, VirtualKeys.Function(5)).IsValidGlobalShortcut);
        Assert.True(new KeyChord(None, VirtualKeys.Snapshot).IsValidGlobalShortcut);
        Assert.False(new KeyChord(Control, VirtualKeys.Control).IsValidGlobalShortcut);
    }

    [Fact]
    public void Reserved_combinations_are_refused()
    {
        Assert.True(ReservedShortcuts.IsReserved(new KeyChord(Win, VirtualKeys.Letter('L'))));
        Assert.True(ReservedShortcuts.IsReserved(new KeyChord(Alt, VirtualKeys.Tab)));
        Assert.True(ReservedShortcuts.IsReserved(new KeyChord(Control | Alt, VirtualKeys.Letter('E'))));
        Assert.True(ReservedShortcuts.IsReserved(new KeyChord(Control | Alt | Shift | Win, VirtualKeys.Letter('W'))));
        Assert.False(ReservedShortcuts.IsReserved(new KeyChord(Control | Alt | Win, VirtualKeys.Letter('K'))));
        Assert.False(ReservedShortcuts.IsReserved(new KeyChord(Control | Alt, VirtualKeys.Left)));
    }

    [Fact]
    public void Display_names_are_windows_style()
    {
        Assert.Equal("Ctrl+Alt+Win+K", new KeyChord(Control | Alt | Win, VirtualKeys.Letter('K')).ToDisplayString());
        Assert.Equal("Shift+F5", new KeyChord(Shift, VirtualKeys.Function(5)).ToDisplayString());
    }

    [Theory]
    [InlineData("1.2.3", "1.2.4", -1)]
    [InlineData("1.2.3", "1.2.3-beta.1", 1)]
    [InlineData("1.2.3-beta.2", "1.2.3-beta.10", -1)]
    [InlineData("1.2.3-alpha", "1.2.3-beta", -1)]
    [InlineData("1.2.3-beta", "1.2.3-beta.1", -1)]
    [InlineData("v2.0.0", "1.99.99", 1)]
    [InlineData("1.0.0+abc", "1.0.0", 0)]
    public void Semver_compares_like_the_updater(string a, string b, int expected) =>
        Assert.Equal(expected, Math.Sign(SemVer.Parse(a).CompareTo(SemVer.Parse(b))));

    [Fact]
    public void Update_selection_picks_the_newest_eligible_release()
    {
        var releases = new List<UpdateChecker.GitHubRelease>
        {
            new() { TagName = "v0.2.0", HtmlUrl = "https://example/0.2.0", Assets = [new() { Name = UpdateChecker.AssetNameFor(SemVer.Parse("0.2.0"), "x64"), BrowserDownloadUrl = "https://example/setup.exe", Size = 10 }] },
            new() { TagName = "v0.3.0-beta.1", Prerelease = true },
            new() { TagName = "v0.4.0", Draft = true },
            new() { TagName = "garbage" },
        };

        var stable = UpdateChecker.Select(releases, SemVer.Parse("0.1.0"), includePrerelease: false, "x64");
        Assert.Equal(UpdateCheckStatus.Available, stable.Status);
        Assert.Equal("0.2.0", stable.Update!.Version.ToString());
        Assert.NotNull(stable.Update.Asset);

        var beta = UpdateChecker.Select(releases, SemVer.Parse("0.1.0"), includePrerelease: true, "x64");
        Assert.Equal("0.3.0-beta.1", beta.Update!.Version.ToString());
        Assert.Null(beta.Update.Asset);

        Assert.Equal(UpdateCheckStatus.UpToDate, UpdateChecker.Select(releases, SemVer.Parse("0.2.0"), false, "x64").Status);
    }
}
