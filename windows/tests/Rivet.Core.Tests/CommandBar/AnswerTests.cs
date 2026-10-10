// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Rivet.Core.Launcher;
using Xunit;

namespace Rivet.Core.Tests.CommandBar;

/// <summary>Spec 06 §6.6–§6.9: calculator, unit and colour conversions, date questions.</summary>
public class AnswerTests
{
    private static readonly CultureInfo EnUs = CultureInfo.GetCultureInfo("en-US");
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    private static string? Calc(string text, CultureInfo? culture = null) => CommandBarMath.Evaluate(text, culture ?? EnUs)?.Formatted;

    [Theory]
    [InlineData("2+2*3", "8")]
    [InlineData("(2+2)*3", "12")]
    [InlineData("-2^2", "-4")]
    [InlineData("2^3^2", "512")]
    [InlineData("0.1+0.2", "0.3")]
    [InlineData("480+15%", "552")]
    [InlineData("480-15%", "408")]
    [InlineData("20% of 480", "96")]
    [InlineData("20% de 480", "96")]
    [InlineData("200*10%", "20")]
    [InlineData("1/1000000000", "1e-9")]
    [InlineData("2^100", "1.2676506e30")]
    [InlineData("1,000+1", "1,001")]
    [InlineData("0,125*8", "1")]
    [InlineData("100-50", "50")]
    [InlineData("sqrt(16)", "4")]
    [InlineData("2pi", "6.28318531")]
    [InlineData("2(3+4)", "14")]
    [InlineData("3x4", "12")]
    [InlineData("10 ÷ 4", "2.5")]
    [InlineData("10 − 4 × 2", "2")]
    [InlineData("round(2.5)", "3")]
    [InlineData("log10(1000)", "3")]
    [InlineData("ln(e)", "1")]
    [InlineData("12/25", "0.48")]
    [InlineData("1e3+1", "1,001")]
    [InlineData("5*3=", "15")]
    public void Calculator_cases(string input, string expected) => Assert.Equal(expected, Calc(input));

    [Theory]
    [InlineData("hello")]
    [InlineData("1password")]
    [InlineData("volume 20")]
    [InlineData("e-mail")]
    [InlineData("42")]
    [InlineData("50%")]
    [InlineData("pi")]
    [InlineData("1/0")]
    [InlineData("sqrt(-1)")]
    [InlineData("asin(2)")]
    [InlineData("ln(0)")]
    [InlineData("2026-07-28")]
    [InlineData("10:30")]
    [InlineData("1/2/3")]
    [InlineData("2+2)")]
    [InlineData("(2+3]")]
    [InlineData("1=2")]
    [InlineData("sqrt 4")]
    [InlineData("5 % of")]
    public void Not_a_sum(string input) => Assert.Null(Calc(input));

    [Fact]
    public void Missing_closers_are_added_and_reported()
    {
        var result = CommandBarMath.Evaluate("([2+3", EnUs)!;
        Assert.Equal("5", result.Formatted);
        Assert.Equal("])", result.Closers);
        Assert.Equal("([2+3])", result.ClosedExpression);
    }

    [Fact]
    public void Too_long_expressions_are_refused() =>
        Assert.Null(Calc(string.Join("+", Enumerable.Repeat("1", 61))));

    [Theory]
    [InlineData("1.234,5 + 1", 1235.5)]
    [InlineData("1,5*2", 3)]
    [InlineData("1.500+1", 1501)]
    [InlineData("0,125*8", 1)]
    public void Brazilian_separators(string input, double expected) => Assert.Equal(expected, CommandBarMath.Evaluate(input, PtBr)!.Value);

    [Fact]
    public void Brazilian_output_uses_local_marks() => Assert.Equal("1.235,5", Calc("1.234,5 + 1", PtBr));

    [Fact]
    public void Space_grouping_locales()
    {
        var fr = (CultureInfo)CultureInfo.GetCultureInfo("fr-FR").Clone();
        fr.NumberFormat.NumberGroupSeparator = " ";
        fr.NumberFormat.NumberDecimalSeparator = ",";
        Assert.Equal(2.5, CommandBarMath.Evaluate("1.5+1", fr)!.Value);
        Assert.Equal(1235.5, CommandBarMath.Evaluate("1 234,5+1", fr)!.Value);
        Assert.Null(CommandBarMath.Evaluate("1 5+1", fr));
        var pt = (CultureInfo)CultureInfo.GetCultureInfo("pt-PT").Clone();
        pt.NumberFormat.NumberGroupSeparator = " ";
        pt.NumberFormat.NumberDecimalSeparator = ",";
        Assert.Equal(1235.5, CommandBarMath.Evaluate("1 234,5+1", pt)!.Value);
    }

    [Theory]
    [InlineData(-4, "(-4)")]
    [InlineData(2.5, "2.5")]
    [InlineData(3, "3")]
    public void Tab_reuse_text(double value, string expected) => Assert.Equal(expected, CommandBarMath.ReusableText(value, EnUs));

    [Fact]
    public void Reused_negative_values_square_correctly()
    {
        var reused = CommandBarMath.ReusableText(-4, EnUs);
        Assert.Equal("16", Calc(reused + "^2"));
    }

    [Theory]
    [InlineData("100 km to mi", "62.14 mi")]
    [InlineData("100km to mi", "62.14 mi")]
    [InlineData("20 c to f", "68°F")]
    [InlineData("5 gb in mb", "5,000 MB")]
    [InlineData("1 gib to mib", "1,024 MiB")]
    [InlineData("2 hours to min", "120 min")]
    [InlineData("1 kg to lb", "2.2 lb")]
    [InlineData("1.5 l to ml", "1,500 mL")]
    [InlineData("0.72 l to cups", "3 c")]
    [InlineData("180 cm to ft", "5 ft 10.87 in")]
    [InlineData("6 ft to ft", "6 ft")]
    [InlineData("2 cm to ft", "0.0656 ft")]
    [InlineData("5 in to cm", "12.7 cm")]
    [InlineData("32 f para c", "0°C")]
    [InlineData("1 mile → km", "1.61 km")]
    [InlineData("300 k to c", "26.85°C")]
    public void Unit_conversions(string input, string expected) => Assert.Equal(expected, CommandBarUnits.Convert(input, EnUs));

    [Theory]
    [InlineData("minutes to read the article")]
    [InlineData("safari to dock")]
    [InlineData("100 km")]
    [InlineData("5 kg to km")]
    [InlineData("to mi")]
    public void Not_a_conversion(string input) => Assert.Null(CommandBarUnits.Convert(input, EnUs));

    [Fact]
    public void Unit_numbers_follow_the_locale() => Assert.Equal("62,14 mi", CommandBarUnits.Convert("100 km to mi", PtBr));

    [Theory]
    [InlineData("#a2b3b4 to rgb", "rgb(162, 179, 180)")]
    [InlineData("#A2B3B4 in HSL", "hsl(183, 11%, 67%)")]
    [InlineData("rgba(255, 0, 0, 0.5) to hex", "#FF000080")]
    [InlineData("#00000080 to rgb", "rgba(0, 0, 0, 0.502)")]
    [InlineData("#f80 nach rgb", "rgb(255, 136, 0)")]
    [InlineData("#336699 to swift", "Color(red: 0.200, green: 0.400, blue: 0.600)")]
    [InlineData("hsl(120, 100%, 50%) to hex", "#00FF00")]
    [InlineData("Color(red: 1, green: 0, blue: 0, opacity: 0.5) to rgba", "rgba(255, 0, 0, 0.5)")]
    [InlineData("rgb(255 0 0 / 50%) to hex", "#FF000080")]
    public void Colour_conversions(string input, string expected) => Assert.Equal(expected, CommandBarColors.Convert(input));

    [Theory]
    [InlineData("#abc", true)]
    [InlineData("#abcd", true)]
    [InlineData("#aabbcc", true)]
    [InlineData("#aabbccdd", true)]
    [InlineData("rgb(1, 2, 3)", true)]
    [InlineData("rgba(1,2,3,0.4)", true)]
    [InlineData("hsl(-30deg, 50%, 50%)", true)]
    [InlineData("Color(red: 0.2, green: 0.4, blue: 0.6)", true)]
    [InlineData("aabbcc", false)]
    [InlineData("#12345", false)]
    [InlineData("Color(green: 0.2, red: 0.4, blue: 0.6)", false)]
    [InlineData("rgb(256, 0, 0)", false)]
    [InlineData("rgb(1, 2, 3,)", false)]
    [InlineData("the color #abc", false)]
    public void Lone_colour_values(string text, bool expected) => Assert.Equal(expected, ColorValue.IsColor(text));

    [Fact]
    public void Every_8_bit_alpha_survives_a_round_trip()
    {
        for (var a = 0; a <= 255; a++)
        {
            var hex = $"#102030{a:X2}";
            Assert.True(ColorValue.TryParse(hex, out var color));
            foreach (var format in new[] { ColorFormat.Rgba, ColorFormat.Hsla, ColorFormat.SwiftUI })
            {
                Assert.True(ColorValue.TryParse(color.Format(format), out var back), color.Format(format));
                Assert.Equal(a, (int)(back.ToArgb() >> 24));
            }
        }
    }

    private static readonly DateTimeOffset Today = new(2026, 7, 28, 10, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("in 3 weeks", "August 18, 2026", "Tuesday")]
    [InlineData("3 days ago", "July 25, 2026", "Saturday")]
    [InlineData("ha 3 dias", "July 25, 2026", "Saturday")]
    [InlineData("today + 10 days", "August 7, 2026", "Friday")]
    [InlineData("today - 10 days", "July 18, 2026", "Saturday")]
    [InlineData("in 2 months", "September 28, 2026", "Monday")]
    public void Relative_dates(string input, string text, string detail)
    {
        var answer = CommandBarDates.Answer(input, Today, EnUs, TimeZoneInfo.Utc)!;
        Assert.Equal(text, answer.Text);
        Assert.Equal(detail, answer.Detail);
    }

    [Fact]
    public void Days_until_counts_whole_days_to_the_next_occurrence()
    {
        var answer = CommandBarDates.Answer("days until 12/25", Today, EnUs, TimeZoneInfo.Utc)!;
        Assert.Equal("150 days", answer.Text);
        Assert.Equal("December 25, 2026", answer.Detail);
        Assert.Equal("1 day", CommandBarDates.Answer("days until 7/29", Today, EnUs, TimeZoneInfo.Utc)!.Text);
        Assert.Equal("July 1, 2027", CommandBarDates.Answer("days until 7/1", Today, EnUs, TimeZoneInfo.Utc)!.Detail);
    }

    [Fact]
    public void Time_elsewhere_uses_the_city_table()
    {
        var answer = CommandBarDates.Answer("time in Tokyo", Today, EnUs, TimeZoneInfo.Utc)!;
        Assert.Equal("7:00 PM", answer.Text.Replace(' ', ' '));
        Assert.StartsWith("Tokyo · ", answer.Detail);
        Assert.NotNull(CommandBarDates.Answer("hora em lisboa", Today, EnUs, TimeZoneInfo.Utc));
        Assert.NotNull(CommandBarDates.Answer("time new york", Today, EnUs, TimeZoneInfo.Utc));
    }

    [Theory]
    [InlineData("1password")]
    [InlineData("2 monitors")]
    [InlineData("3 tags")]
    [InlineData("notes")]
    [InlineData("day one")]
    [InlineData("5 minutes")]
    [InlineData("2026-07-28")]
    [InlineData("the 3 body problem")]
    [InlineData("time")]
    [InlineData("3 days")]
    public void Must_stay_a_search(string input) => Assert.Null(CommandBarDates.Answer(input, Today, EnUs, TimeZoneInfo.Utc));

    [Fact]
    public void Answers_pick_the_first_engine_that_understands()
    {
        Assert.Equal(AnswerKind.Math, CommandBarAnswers.Compute("2+2", EnUs, Today)!.Kind);
        Assert.Equal(AnswerKind.Units, CommandBarAnswers.Compute("1 km to m", EnUs, Today)!.Kind);
        Assert.Equal(AnswerKind.ColorConversion, CommandBarAnswers.Compute("#fff to rgb", EnUs, Today)!.Kind);
        Assert.Equal(AnswerKind.ColorPreview, CommandBarAnswers.Compute("#fff", EnUs, Today)!.Kind);
        Assert.Equal(AnswerKind.Date, CommandBarAnswers.Compute("in 2 days", EnUs, Today, TimeZoneInfo.Utc)!.Kind);
        Assert.Null(CommandBarAnswers.Compute("notepad", EnUs, Today));
        Assert.Equal("([2+3]) · Enter copies", CommandBarAnswers.Compute("([2+3", EnUs, Today)!.Subtitle);
    }
}
