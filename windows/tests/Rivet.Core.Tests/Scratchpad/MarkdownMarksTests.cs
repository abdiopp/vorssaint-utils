// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Modules.Scratchpad;
using Xunit;

namespace Rivet.Core.Tests.Scratchpad;

public class MarkdownMarksTests
{
    /// <summary>Applies a mark to text where the selection is written as [ ] markers ("|" for a caret).</summary>
    private static string Run(MarkdownMark mark, string marked)
    {
        var (text, start, length) = Parse(marked);
        var edit = MarkdownMarks.Apply(mark, text, start, length);
        return Render(edit);
    }

    private static (string Text, int Start, int Length) Parse(string marked)
    {
        var caret = marked.IndexOf('|');
        if (caret >= 0)
        {
            return (marked.Remove(caret, 1), caret, 0);
        }

        var open = marked.IndexOf('[', StringComparison.Ordinal);
        var close = marked.LastIndexOf(']');
        if (open < 0 || close < 0)
        {
            return (marked, marked.Length, 0);
        }

        var text = marked.Remove(close, 1).Remove(open, 1);
        return (text, open, close - open - 1);
    }

    private static string Render(TextEdit edit) => edit.SelectionLength == 0
        ? edit.Text.Insert(edit.SelectionStart, "|")
        : edit.Text.Insert(edit.SelectionEnd, "]").Insert(edit.SelectionStart, "[");

    [Theory]
    [InlineData("[hello]", "**[hello]**")]
    [InlineData("[hello ]", "**[hello]** ")]
    [InlineData("**[hello]**", "[hello]")]
    [InlineData("[**hello**]", "[hello]")]
    [InlineData("|", "**|**")]
    [InlineData("**|**", "|")]
    [InlineData("[**one** and **two**]", "**[one and two]**")]
    public void Bold_toggles_and_joins(string input, string expected) => Assert.Equal(expected, Run(MarkdownMark.Bold, input));

    [Fact]
    public void Joining_is_refused_when_markers_are_odd_or_escaped()
    {
        Assert.Equal("[**a ** b**]", Run(MarkdownMark.Bold, "[**a ** b**]"));
        Assert.Equal("[**one\\** and **two**]", Run(MarkdownMark.Bold, "[**one\\** and **two**]"));
    }

    [Theory]
    [InlineData("[note]", "*[note]*")]
    [InlineData("**[note]**", "***[note]***")]
    [InlineData("***[note]***", "**[note]**")]
    [InlineData("*[note]*", "[note]")]
    [InlineData("[*note*]", "[note]")]
    [InlineData("[***note***]", "[***note***]")]
    public void Italic_counts_star_runs(string input, string expected) => Assert.Equal(expected, Run(MarkdownMark.Italic, input));

    [Theory]
    [InlineData("[gone]", "~~[gone]~~")]
    [InlineData("~~[gone]~~", "[gone]")]
    public void Strikethrough_toggles(string input, string expected) => Assert.Equal(expected, Run(MarkdownMark.Strikethrough, input));

    [Theory]
    [InlineData("[x]", "`[x]`")]
    [InlineData("`[x]`", "[x]")]
    [InlineData("[`a` and `b`]", "`[a and b]`")]
    public void Code_toggles(string input, string expected) => Assert.Equal(expected, Run(MarkdownMark.Code, input));

    [Theory]
    [InlineData("[site]", "[site]([url])")]
    [InlineData("[si|te](https://x.y)", "[site]")]
    [InlineData("[site](a_(b_(c)|))", "[site]")]
    [InlineData("[site](a\\)b|)", "[site]")]
    [InlineData("![al|t](pic.png)", "![al|t](pic.png)")]
    public void Link_wraps_or_unwraps(string input, string expected)
    {
        // Link examples use real brackets, so the selection is given with a caret.
        if (!input.Contains('|'))
        {
            Assert.Equal(expected, Run(MarkdownMark.Link, input));
            return;
        }

        var caret = input.IndexOf('|');
        var text = input.Remove(caret, 1);
        var edit = MarkdownMarks.Apply(MarkdownMark.Link, text, caret, 0);
        var rendered = edit.SelectionLength == 0
            ? edit.Text.Insert(edit.SelectionStart, "|")
            : edit.Text.Insert(edit.SelectionEnd, "]").Insert(edit.SelectionStart, "[");
        Assert.Equal(expected, rendered);
    }

    [Fact]
    public void Wrapping_a_link_selects_the_placeholder()
    {
        var edit = MarkdownMarks.Apply(MarkdownMark.Link, "see docs", 4, 4);
        Assert.Equal("see [docs](url)", edit.Text);
        Assert.Equal("url", edit.Text.Substring(edit.SelectionStart, edit.SelectionLength));
    }

    [Fact]
    public void Heading_cycles_through_three_levels_then_off()
    {
        var text = "title";
        var e1 = MarkdownMarks.Apply(MarkdownMark.Heading, text, 0, 0);
        Assert.Equal("# title", e1.Text);
        var e2 = MarkdownMarks.Apply(MarkdownMark.Heading, e1.Text, e1.SelectionStart, 0);
        Assert.Equal("## title", e2.Text);
        var e3 = MarkdownMarks.Apply(MarkdownMark.Heading, e2.Text, e2.SelectionStart, 0);
        Assert.Equal("### title", e3.Text);
        var e4 = MarkdownMarks.Apply(MarkdownMark.Heading, e3.Text, e3.SelectionStart, 0);
        Assert.Equal("title", e4.Text);
    }

    [Fact]
    public void An_empty_pad_starts_the_first_line()
    {
        Assert.Equal("# ", MarkdownMarks.Apply(MarkdownMark.Heading, "", 0, 0).Text);
        Assert.Equal("- ", MarkdownMarks.Apply(MarkdownMark.Bullet, "", 0, 0).Text);
        Assert.Equal("1. ", MarkdownMarks.Apply(MarkdownMark.Numbered, "", 0, 0).Text);
        Assert.Equal(2, MarkdownMarks.Apply(MarkdownMark.Heading, "", 0, 0).SelectionStart);
    }

    [Fact]
    public void Line_marks_replace_instead_of_stacking()
    {
        Assert.Equal("# item", MarkdownMarks.Apply(MarkdownMark.Heading, "- item", 3, 0).Text);
        Assert.Equal("1. one\n2. two", MarkdownMarks.Apply(MarkdownMark.Numbered, "- one\n- two", 0, 11).Text);
        Assert.Equal("one\ntwo", MarkdownMarks.Apply(MarkdownMark.Numbered, "1. one\n2. two", 0, 13).Text);
    }

    [Fact]
    public void Line_marks_keep_indentation_and_skip_blank_lines()
    {
        var edit = MarkdownMarks.Apply(MarkdownMark.Bullet, "a\n\n  b", 0, 6);
        Assert.Equal("- a\n\n  - b", edit.Text);
        var quote = MarkdownMarks.Apply(MarkdownMark.Quote, edit.Text, 0, edit.Text.Length);
        Assert.Equal("> a\n\n  > b", quote.Text);
    }

    [Fact]
    public void Bullets_toggle_off_when_every_line_has_one()
    {
        Assert.Equal("a\nb", MarkdownMarks.Apply(MarkdownMark.Bullet, "- a\n- b", 0, 7).Text);
    }

    [Fact]
    public void A_blank_line_in_a_non_empty_pad_is_left_alone()
    {
        var edit = MarkdownMarks.Apply(MarkdownMark.Heading, "text\n", 5, 0);
        Assert.Equal("text\n", edit.Text);
    }

    [Fact]
    public void The_selection_shifts_with_the_prefix()
    {
        var edit = MarkdownMarks.Apply(MarkdownMark.Bullet, "hello", 1, 3);
        Assert.Equal("- hello", edit.Text);
        Assert.Equal("ell", edit.Text.Substring(edit.SelectionStart, edit.SelectionLength));
    }

    [Fact]
    public void Selections_never_split_a_surrogate_pair()
    {
        var text = "a😀b";
        var (start, end) = MarkdownMarks.Clamp(text, 2, 1);
        Assert.Equal(1, start);
        Assert.Equal(3, end);
    }
}

public class ScratchpadLineMoverTests
{
    [Fact]
    public void Moves_the_touched_lines_up_and_the_selection_follows()
    {
        var edit = ScratchpadLineMover.Move("one\ntwo\nthree", 5, 0, up: true);
        Assert.NotNull(edit);
        Assert.Equal("two\none\nthree", edit!.Value.Text);
        Assert.Equal(1, edit.Value.SelectionStart);
    }

    [Fact]
    public void Moves_a_block_down()
    {
        var edit = ScratchpadLineMover.Move("a\nb\nc\nd", 0, 3, up: false);
        Assert.Equal("c\na\nb\nd", edit!.Value.Text);
        Assert.Equal("a\nb", edit.Value.Text.Substring(edit.Value.SelectionStart, edit.Value.SelectionLength));
    }

    [Fact]
    public void Falls_through_at_the_first_and_last_line()
    {
        Assert.Null(ScratchpadLineMover.Move("a\nb", 0, 0, up: true));
        Assert.Null(ScratchpadLineMover.Move("a\nb", 3, 0, up: false));
    }

    [Fact]
    public void Swapping_identical_lines_only_moves_the_caret()
    {
        var edit = ScratchpadLineMover.Move("x\nx", 2, 0, up: true);
        Assert.Equal("x\nx", edit!.Value.Text);
        Assert.Equal(0, edit.Value.SelectionStart);
    }
}

public class ScratchpadShortcutTests
{
    [Fact]
    public void Characters_are_matched_by_what_the_layout_types()
    {
        Assert.Equal(ScratchpadCommand.NewTab, ScratchpadShortcuts.Resolve('t', ScratchpadNamedKey.None, control: true, alt: false, shift: false));
        Assert.Equal(ScratchpadCommand.NewTab, ScratchpadShortcuts.Resolve('T', ScratchpadNamedKey.None, control: true, alt: false, shift: false));
        Assert.Equal(ScratchpadCommand.CloseTab, ScratchpadShortcuts.Resolve('\u0017', ScratchpadNamedKey.None, control: true, alt: false, shift: false));
        Assert.Null(ScratchpadShortcuts.Resolve('z', ScratchpadNamedKey.None, control: true, alt: false, shift: false));
        Assert.Equal(ScratchpadCommand.Find, ScratchpadShortcuts.Resolve('f', ScratchpadNamedKey.None, control: true, alt: false, shift: false));
        Assert.Equal(ScratchpadCommand.FindPrevious, ScratchpadShortcuts.Resolve(null, ScratchpadNamedKey.F3, control: false, alt: false, shift: true));
        Assert.Equal(ScratchpadCommand.MoveLinesUp, ScratchpadShortcuts.Resolve(null, ScratchpadNamedKey.Up, control: false, alt: true, shift: false));
        Assert.Null(ScratchpadShortcuts.Resolve(null, ScratchpadNamedKey.Up, control: true, alt: true, shift: false));
        Assert.Equal(ScratchpadCommand.Hide, ScratchpadShortcuts.Resolve(null, ScratchpadNamedKey.Escape, control: false, alt: false, shift: false));
        Assert.Null(ScratchpadShortcuts.Resolve('t', ScratchpadNamedKey.None, control: true, alt: true, shift: false));
    }
}
