// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Capture;
using Rivet.Core.Settings;
using Xunit;

namespace Rivet.Core.Tests.Capture;

/// <summary>Colour formats (spec 01 §6.19), file names and folders (§6.20).</summary>
public class FormattingAndNamingTests
{
    private const uint DodgerBlue = 0xFF1E90FF;

    [Fact]
    public void Formats_match_the_spec_examples()
    {
        Assert.Equal("#1E90FF", ColorFormatter.Format(DodgerBlue, ColorFormat.Hex));
        Assert.Equal("1E90FF", ColorFormatter.Format(DodgerBlue, ColorFormat.Hex, bareHex: true));
        Assert.Equal("rgb(30, 144, 255)", ColorFormatter.Format(DodgerBlue, ColorFormat.Rgb));
        Assert.Equal("hsl(210, 100%, 56%)", ColorFormatter.Format(DodgerBlue, ColorFormat.Hsl));
        Assert.Equal("Color.FromArgb(255, 30, 144, 255)", ColorFormatter.Format(DodgerBlue, ColorFormat.CSharp));
    }

    [Fact]
    public void Hsl_covers_greys_and_primaries()
    {
        Assert.Equal("hsl(0, 0%, 50%)", ColorFormatter.Format(0.5, 0.5, 0.5, ColorFormat.Hsl));
        Assert.Equal("hsl(0, 100%, 50%)", ColorFormatter.Format(1, 0, 0, ColorFormat.Hsl));
        Assert.Equal("hsl(120, 100%, 50%)", ColorFormatter.Format(0, 1, 0, ColorFormat.Hsl));
        Assert.Equal("hsl(240, 100%, 50%)", ColorFormatter.Format(0, 0, 1, ColorFormat.Hsl));
        Assert.Equal("hsl(300, 100%, 25%)", ColorFormatter.Format(0.5, 0, 0.5, ColorFormat.Hsl));
        Assert.Equal("hsl(0, 0%, 100%)", ColorFormatter.Format(2, 2, 2, ColorFormat.Hsl)); // clamped
    }

    [Fact]
    public void Format_keys_sanitize_and_map_the_macOS_value()
    {
        Assert.Equal("csharp", ColorFormatter.SanitizeFormatKey("swiftui"));
        Assert.Equal("hex", ColorFormatter.SanitizeFormatKey("cmyk"));
        Assert.Equal(ColorFormat.Rgb, ColorFormatter.Parse("rgb"));
        Assert.Equal("hsl", ColorFormatter.ToKey(ColorFormat.Hsl));
        var settings = SettingsStore.InMemory();
        settings.Set(CaptureSettings.ColorPickerFormat, "swiftui");
        Assert.Equal("csharp", settings.Get(CaptureSettings.ColorPickerFormat));
    }

    private static readonly DateTime When = new(2026, 10, 9, 14, 5, 33);

    [Fact]
    public void The_default_name_is_dated_and_colon_free()
    {
        Assert.Equal("Screenshot 2026-10-09 at 14.05.33.png", CaptureNaming.DefaultName(When));
        Assert.Equal("Screenshot 2026-10-09 at 14.05.33.png", CaptureNaming.FileName("  ", When, 1));
    }

    [Fact]
    public void Date_tokens_expand_longest_first()
    {
        Assert.Equal("2026 October 26 10 09 14 05 33", CaptureNaming.ExpandDateTokens("%year %month %y %mo %d %h %mi %s", When));
        Assert.Equal("26-10", CaptureNaming.ExpandDateTokens("%y-%mo", When));
    }

    [Fact]
    public void Number_runs_pad_to_their_length_minus_one()
    {
        Assert.Equal("Shot 7", CaptureNaming.ExpandNumberRuns("Shot %#", 7));
        Assert.Equal("Shot 0007 of 007", CaptureNaming.ExpandNumberRuns("Shot %#### of %###", 7));
        Assert.Equal("1234", CaptureNaming.ExpandNumberRuns("%##", 1234));
        Assert.True(CaptureNaming.UsesNumber("a %# b"));
        Assert.False(CaptureNaming.UsesNumber("a # b"));
    }

    [Fact]
    public void File_names_lose_slashes_colons_and_other_illegal_characters()
    {
        Assert.Equal("a-b-c-d.png", CaptureNaming.FileName("a/b:c\\d", When, 1));
        Assert.Equal("what- -x-.png", CaptureNaming.FileName("what? *x|", When, 1));
        Assert.Equal("Shot 26-10-09 03.png", CaptureNaming.FileName("Shot %y-%mo-%d %##", When, 3));
        Assert.Equal("name.png", CaptureNaming.FileName("name. . ", When, 1));
        Assert.Equal("_CON.png", CaptureNaming.FileName("CON", When, 1));
        Assert.Equal("_nul.txt.png", CaptureNaming.FileName("nul.txt", When, 1));
    }

    [Fact]
    public void Subfolders_cannot_escape_the_base_folder()
    {
        var sep = Path.DirectorySeparatorChar;
        Assert.Equal($"2026{sep}10", CaptureNaming.Subfolder("%year/%mo", When));
        Assert.Equal($"a{sep}b", CaptureNaming.Subfolder("../a/./b/..//", When));
        Assert.Equal($"x{sep}y", CaptureNaming.Subfolder(@"x\y", When));
        Assert.Equal(string.Empty, CaptureNaming.Subfolder("  ", When));
        Assert.Equal(string.Empty, CaptureNaming.Subfolder("../..", When));
    }

    [Fact]
    public void Unique_names_count_up_from_two()
    {
        var taken = new HashSet<string> { Path.Combine("dir", "a.png"), Path.Combine("dir", "a 2.png") };
        Assert.Equal("b.png", CaptureNaming.UniqueName("dir", "b.png", taken.Contains));
        Assert.Equal("a 3.png", CaptureNaming.UniqueName("dir", "a.png", taken.Contains));
        Assert.Equal("a.png", CaptureNaming.UniqueName("dir", "a.png", _ => true));
    }

    [Fact]
    public void Long_names_are_shortened_to_stay_under_max_path()
    {
        var folder = "C:\\" + new string('f', 200);
        var name = CaptureNaming.UniqueName(folder, new string('n', 120) + ".png", _ => false);
        Assert.True(folder.Length + 1 + name.Length <= CaptureNaming.MaxPathLength);
        Assert.EndsWith(".png", name, StringComparison.Ordinal);
    }

    [Fact]
    public void The_number_sequence_rewinds_only_when_nothing_else_advanced_it()
    {
        var settings = SettingsStore.InMemory();
        settings.Set(CaptureSettings.FileNumberNext, 5);
        var first = FileNumberSequence.Consume(settings);
        Assert.Equal(5, first);
        Assert.Equal(6, FileNumberSequence.Peek(settings));
        Assert.True(FileNumberSequence.Rewind(settings, first));
        Assert.Equal(5, FileNumberSequence.Peek(settings));

        var a = FileNumberSequence.Consume(settings);
        var b = FileNumberSequence.Consume(settings);
        Assert.False(FileNumberSequence.Rewind(settings, a));
        Assert.True(FileNumberSequence.Rewind(settings, b));
        Assert.Equal(6, FileNumberSequence.Peek(settings));

        settings.Set(CaptureSettings.FileNumberStart, 100);
        FileNumberSequence.Restart(settings);
        Assert.Equal(100, FileNumberSequence.Peek(settings));
    }

    [Fact]
    public void Stored_values_are_sanitized()
    {
        var settings = SettingsStore.InMemory();
        settings.Set(CaptureSettings.Delay, 7);
        Assert.Equal(0, settings.Get(CaptureSettings.Delay));
        settings.Set(CaptureSettings.PreviewDuration, 4);
        Assert.Equal(3, settings.Get(CaptureSettings.PreviewDuration));
        settings.Set(CaptureSettings.PreviewDuration, 0);
        Assert.Equal(0, settings.Get(CaptureSettings.PreviewDuration));
        settings.Set(CaptureSettings.DefaultAction, "print");
        Assert.Equal(string.Empty, settings.Get(CaptureSettings.DefaultAction));
        settings.Set(CaptureSettings.LoupeDefaultZoom, 9);
        Assert.Equal(13 / 3.0, settings.Get(CaptureSettings.LoupeDefaultZoom), 6);
        settings.Set(CaptureSettings.FileNumberStart, 5_000_000);
        Assert.Equal(999_999, settings.Get(CaptureSettings.FileNumberStart));
    }

    [Fact]
    public void Turning_automatic_copy_off_strips_the_copy_half_of_the_action()
    {
        Assert.Equal(ScreenshotDefaultAction.Ask, CaptureSettings.WithoutCopy(ScreenshotDefaultAction.Copy));
        Assert.Equal(ScreenshotDefaultAction.Save, CaptureSettings.WithoutCopy(ScreenshotDefaultAction.SaveAndCopy));
        Assert.Equal(ScreenshotDefaultAction.Edit, CaptureSettings.WithoutCopy(ScreenshotDefaultAction.Edit));
        Assert.Equal("saveAndCopy", CaptureSettings.ToStorage(ScreenshotDefaultAction.SaveAndCopy));
        Assert.Equal(ScreenshotDefaultAction.Ask, CaptureSettings.ParseDefaultAction("whatever"));
        Assert.Equal(PreviewPosition.TopRight, CaptureSettings.ParsePreviewPosition("topRight"));
    }
}
