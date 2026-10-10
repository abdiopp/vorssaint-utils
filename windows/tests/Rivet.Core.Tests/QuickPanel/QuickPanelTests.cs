// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.QuickPanel;
using Rivet.Core.Shortcuts;
using Xunit;

namespace Rivet.Core.Tests.QuickPanel;

/// <summary>Spec 06 §3.9: tile order, hidden tiles and the clamped keyboard grid.</summary>
public class QuickPanelTests
{
    private static readonly string[] Known = ["keepAwake", "cleaner", "toggles", "micMute", "clipboard"];

    [Fact]
    public void Default_order_matches_the_spec()
    {
        Assert.Equal(
            ["keepAwake", "cleaner", "toggles", "micMute", "screenOCR", "colorPicker", "clipboard", "windowLayout", "cleaning",
             "homebrew", "media", "urlCleaner", "uninstaller", "screenshot", "screenRecorder", "cameraPreview", "scratchpad"],
            QuickPanelSettings.DefaultOrder);
    }

    [Theory]
    [InlineData("", "keepAwake,cleaner,toggles,micMute,clipboard")]
    [InlineData("clipboard,keepAwake", "clipboard,keepAwake,cleaner,toggles,micMute")]
    [InlineData("gone, toggles ,toggles,keepAwake", "toggles,keepAwake,cleaner,micMute,clipboard")]
    public void Order_drops_unknown_and_appends_missing(string stored, string expected) =>
        Assert.Equal(expected, string.Join(',', QuickPanelSettings.SanitizeOrder(stored, Known)));

    [Fact]
    public void Hidden_tiles_are_a_sorted_comma_list()
    {
        Assert.Equal("cleaner,toggles", QuickPanelSettings.FormatHidden(["toggles", "cleaner", "toggles"]));
        Assert.Equal(["a", "b"], QuickPanelSettings.ParseHidden(" a ,b,,").OrderBy(x => x));
    }

    [Theory]
    [InlineData(0, 10, -1, 0, 0)] // ← at column 0 stays
    [InlineData(2, 10, 1, 0, 2)] // → at the last column stays
    [InlineData(1, 10, 1, 0, 2)]
    [InlineData(9, 10, 1, 0, 9)] // → past the last tile stays
    [InlineData(4, 10, 0, -1, 1)] // ↑ by 3
    [InlineData(1, 10, 0, -1, 1)] // ↑ off the top stays
    [InlineData(7, 10, 0, 1, 7)] // ↓ past the end stays
    [InlineData(5, 10, 0, 1, 8)]
    [InlineData(0, 0, 1, 0, -1)]
    public void Arrows_move_one_cell_clamped(int index, int count, int dx, int dy, int expected) =>
        Assert.Equal(expected, QuickPanelSettings.Move(index, count, dx, dy));

    [Fact]
    public void Default_shortcut_is_ctrl_alt_win_q()
    {
        Assert.Equal(KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Win, QuickPanelSettings.DefaultShortcut.Modifiers);
        Assert.True(QuickPanelSettings.ShortcutEnabled.Default);
    }
}
