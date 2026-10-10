// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Rivet.Core.Localization;

namespace Rivet.Core.Launcher;

public enum AnswerKind
{
    Math,
    Units,
    ColorConversion,
    ColorPreview,
    Date,
}

/// <summary>An answer computed from the typed text.</summary>
public sealed record ComputedAnswer(AnswerKind Kind, string Title, string Subtitle, string CopyText, uint? Swatch = null, MathResult? Math = null)
{
    public string RowId => Kind switch
    {
        AnswerKind.Math => "math.result",
        AnswerKind.Units => "units.result",
        AnswerKind.ColorConversion => "color.result",
        AnswerKind.ColorPreview => "color.preview",
        _ => "date.result",
    };
}

/// <summary>
/// The "Sums and conversions" answer (spec 06 §3.8.6 step 2): the first of
/// calculator → unit conversion → colour conversion → a lone colour → date question.
/// </summary>
public static class CommandBarAnswers
{
    public static ComputedAnswer? Compute(string query, CultureInfo culture, DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        var text = query.Trim();
        if (text.Length == 0)
        {
            return null;
        }

        var copyHint = L.Get("commandBar.copyHint");
        if (CommandBarMath.Evaluate(text, culture) is { } math)
        {
            var subtitle = math.Closers.Length > 0 ? $"{math.ClosedExpression} · {copyHint}" : copyHint;
            return new ComputedAnswer(AnswerKind.Math, math.Formatted, subtitle, math.Formatted, Math: math);
        }

        if (CommandBarUnits.Convert(text, culture) is { } units)
        {
            return new ComputedAnswer(AnswerKind.Units, units, copyHint, units);
        }

        if (CommandBarColors.Convert(text) is { } converted)
        {
            var swatch = ColorValue.TryParse(converted, out var c) ? c.ToArgb() : (uint?)null;
            return new ComputedAnswer(AnswerKind.ColorConversion, converted, copyHint, converted, swatch);
        }

        if (ColorValue.TryParse(text, out var color))
        {
            return new ComputedAnswer(AnswerKind.ColorPreview, text, copyHint, text, color.ToArgb());
        }

        if (CommandBarDates.Answer(text, now, culture, zone) is { } date)
        {
            return new ComputedAnswer(AnswerKind.Date, date.Text, date.Detail, date.Text);
        }

        return null;
    }
}
