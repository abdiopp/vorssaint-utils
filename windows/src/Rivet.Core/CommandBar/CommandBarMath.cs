// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;

namespace Rivet.Core.Launcher;

/// <summary>A calculator answer: the value, its display text and any brackets that were closed virtually.</summary>
public sealed record MathResult(double Value, string Formatted, string Expression, string Closers)
{
    /// <summary>The expression with the virtual closers appended ("([2+3" → "([2+3])").</summary>
    public string ClosedExpression => Expression + Closers;
}

/// <summary>
/// The Command Bar calculator (spec 06 §6.6): <c>+ - * / ^ %</c>, brackets,
/// constants, functions, "of" percentages and implicit products, with
/// locale-aware number reading. Anything that fails a gate stays a search.
/// </summary>
public static class CommandBarMath
{
    private static readonly HashSet<string> Functions = new(StringComparer.Ordinal)
    {
        "sqrt", "abs", "sin", "cos", "tan", "asin", "acos", "atan", "ln", "log", "log10", "exp", "floor", "ceil", "round",
    };

    private static readonly HashSet<string> OfWords = new(StringComparer.Ordinal) { "of", "de", "da", "do", "von", "di", "del", "dal" };

    private enum Kind
    {
        Number,
        Constant,
        Function,
        Plus,
        Minus,
        Times,
        Divide,
        Power,
        Percent,
        Of,
        Open,
        Close,
    }

    private readonly record struct Token(Kind Kind, double Value = 0, string Text = "", char Bracket = '(');

    private readonly record struct Value(double Number, double? Percent);

    /// <summary>Evaluates a sum; null when the text is not one.</summary>
    public static MathResult? Evaluate(string input, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        var text = input.Trim();
        var equals = text.IndexOf('=');
        if (equals >= 0)
        {
            if (equals != text.Length - 1)
            {
                return null;
            }

            text = text[..^1].TrimEnd();
        }

        if (text.Length is 0 or > 120 || LooksLikeDateOrTime(text))
        {
            return null;
        }

        if (Tokenize(text, culture) is not { } tokens || !IsCalculation(tokens))
        {
            return null;
        }

        if (CloseBrackets(tokens) is not { } closers)
        {
            return null;
        }

        var parser = new Parser(tokens);
        try
        {
            var value = parser.ParseExpression(0);
            if (!parser.AtEnd || !double.IsFinite(value.Number))
            {
                return null;
            }

            var rounded = RoundSignificant(value.Number, 10);
            return new MathResult(rounded, Format(rounded, culture), text, closers);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Refuses date/time shapes: splitting by one of <c>- / :</c> gives 2–3
    /// groups of 1–4 digits and the separator is ':' or there are 3 groups.
    /// "100-50" stays a subtraction.
    /// </summary>
    public static bool LooksLikeDateOrTime(string text)
    {
        foreach (var separator in new[] { '-', '/', ':' })
        {
            var groups = text.Split(separator).Select(g => g.Trim()).ToArray();
            if (groups.Length is < 2 or > 3)
            {
                continue;
            }

            if (groups.All(g => g.Length is >= 1 and <= 4 && g.All(char.IsAsciiDigit)) && (separator == ':' || groups.Length == 3))
            {
                return true;
            }
        }

        return false;
    }

    private static List<Token>? Tokenize(string text, CultureInfo culture)
    {
        var (dec, alternate, groupingOnly) = Separators(culture);
        var tokens = new List<Token>();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsAsciiDigit(c) || ((c == dec || c == alternate) && i + 1 < text.Length && char.IsAsciiDigit(text[i + 1])))
            {
                if (ReadNumber(text, ref i, dec, alternate, groupingOnly) is not { } number)
                {
                    return null;
                }

                tokens.Add(new Token(Kind.Number, number));
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (char.IsLetter(c))
            {
                var start = i;
                while (i < text.Length && char.IsLetter(text[i]))
                {
                    i++;
                }

                var word = text[start..i].ToLowerInvariant();
                if (word == "log" && i + 1 < text.Length && text[i] == '1' && text[i + 1] == '0')
                {
                    word = "log10";
                    i += 2;
                }

                if (word == "π" || word == "pi")
                {
                    tokens.Add(new Token(Kind.Constant, Math.PI));
                }
                else if (word == "e")
                {
                    tokens.Add(new Token(Kind.Constant, Math.E));
                }
                else if (word == "x")
                {
                    tokens.Add(new Token(Kind.Times));
                }
                else if (Functions.Contains(word))
                {
                    tokens.Add(new Token(Kind.Function, Text: word));
                }
                else if (OfWords.Contains(word))
                {
                    tokens.Add(new Token(Kind.Of));
                }
                else
                {
                    return null;
                }

                continue;
            }

            Token? symbol = c switch
            {
                '+' => new Token(Kind.Plus),
                '-' or '−' => new Token(Kind.Minus),
                '*' or '×' => new Token(Kind.Times),
                '/' or '÷' => new Token(Kind.Divide),
                '^' => new Token(Kind.Power),
                '%' => new Token(Kind.Percent),
                '(' or '[' => new Token(Kind.Open, Bracket: c),
                ')' or ']' => new Token(Kind.Close, Bracket: c),
                _ => null,
            };
            if (symbol is null)
            {
                return null;
            }

            tokens.Add(symbol.Value);
            i++;
        }

        return tokens;
    }

    /// <summary>The decimal mark, the alternate (. or ,) and a non-./, grouping mark (space, NBSP, apostrophe…).</summary>
    public static (char Decimal, char Alternate, char? GroupingOnly) Separators(CultureInfo culture)
    {
        var dec = culture.NumberFormat.NumberDecimalSeparator is { Length: 1 } d ? d[0] : '.';
        var grouping = culture.NumberFormat.NumberGroupSeparator is { Length: 1 } g ? g[0] : (char?)null;
        var alternate = grouping is '.' or ',' && grouping != dec ? grouping.Value : (dec == '.' ? ',' : '.');
        var groupingOnly = grouping is { } gs && gs != '.' && gs != ',' && gs != dec ? gs : (char?)null;
        return (dec, alternate, groupingOnly);
    }

    private static double? ReadNumber(string text, ref int i, char dec, char alternate, char? groupingOnly)
    {
        var start = i;
        var builder = new StringBuilder();
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsAsciiDigit(c) || c == dec || c == alternate)
            {
                builder.Append(c);
                i++;
            }
            else if (groupingOnly is { } go && c == go && i + 1 < text.Length && char.IsAsciiDigit(text[i + 1]))
            {
                builder.Append(c);
                i++;
            }
            else
            {
                break;
            }
        }

        var raw = builder.ToString();
        string normalized;
        if (groupingOnly is { } grouping && raw.Contains(grouping))
        {
            var firstPoint = raw.IndexOfAny([dec, alternate]);
            var integer = firstPoint >= 0 ? raw[..firstPoint] : raw;
            if (firstPoint >= 0 && raw[firstPoint..].Contains(grouping))
            {
                return null;
            }

            if (!LooksLikeGrouping(integer, grouping))
            {
                return null;
            }

            raw = raw.Replace(grouping.ToString(), string.Empty);
        }

        var hasDec = raw.Contains(dec);
        var hasAlt = raw.Contains(alternate);
        if (hasDec && hasAlt)
        {
            var point = raw.LastIndexOf(dec) > raw.LastIndexOf(alternate) ? dec : alternate;
            var group = point == dec ? alternate : dec;
            normalized = raw.Replace(group.ToString(), string.Empty).Replace(point, '.');
        }
        else if (hasAlt)
        {
            normalized = LooksLikeGrouping(raw, alternate) ? raw.Replace(alternate.ToString(), string.Empty) : raw.Replace(alternate, '.');
        }
        else
        {
            normalized = raw.Replace(dec, '.');
        }

        if (normalized.Count(c => c == '.') > 1)
        {
            return null;
        }

        // Optional ASCII exponent right after the number.
        if (i < text.Length && text[i] is 'e' or 'E')
        {
            var j = i + 1;
            if (j < text.Length && text[j] is '+' or '-')
            {
                j++;
            }

            var digitsStart = j;
            while (j < text.Length && char.IsAsciiDigit(text[j]))
            {
                j++;
            }

            if (j > digitsStart)
            {
                normalized += "e" + text[(i + 1)..j];
                i = j;
            }
        }

        if (!double.TryParse(normalized, NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
        {
            i = start;
            return null;
        }

        return value;
    }

    /// <summary>First group 1–3 digits not starting with 0, every later group exactly 3 digits.</summary>
    private static bool LooksLikeGrouping(string text, char separator)
    {
        var groups = text.Split(separator);
        return groups.Length >= 2
               && groups[0].Length is >= 1 and <= 3 && groups[0][0] != '0' && groups[0].All(char.IsAsciiDigit)
               && groups.Skip(1).All(g => g.Length == 3 && g.All(char.IsAsciiDigit));
    }

    /// <summary>An explicit operator, a function or an implicit product (a lone number, percentage or constant is not).</summary>
    private static bool IsCalculation(List<Token> tokens)
    {
        for (var i = 0; i < tokens.Count; i++)
        {
            var kind = tokens[i].Kind;
            if (kind is Kind.Plus or Kind.Minus or Kind.Times or Kind.Divide or Kind.Power or Kind.Of or Kind.Function)
            {
                return true;
            }

            if (i + 1 < tokens.Count && kind is Kind.Number or Kind.Constant or Kind.Close or Kind.Percent
                && tokens[i + 1].Kind is Kind.Constant or Kind.Function or Kind.Open)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Checks bracket kinds; appends missing closers (innermost first) and returns them, or null on a mismatch.</summary>
    private static string? CloseBrackets(List<Token> tokens)
    {
        var stack = new Stack<char>();
        foreach (var token in tokens)
        {
            if (token.Kind == Kind.Open)
            {
                stack.Push(token.Bracket);
            }
            else if (token.Kind == Kind.Close)
            {
                if (stack.Count == 0)
                {
                    return null;
                }

                var opener = stack.Pop();
                if ((opener == '(' && token.Bracket != ')') || (opener == '[' && token.Bracket != ']'))
                {
                    return null;
                }
            }
        }

        var closers = new StringBuilder();
        while (stack.Count > 0)
        {
            var closer = stack.Pop() == '(' ? ')' : ']';
            closers.Append(closer);
            tokens.Add(new Token(Kind.Close, Bracket: closer));
        }

        return closers.ToString();
    }

    private sealed class Parser(List<Token> tokens)
    {
        private int _position;

        public bool AtEnd => _position >= tokens.Count;

        private Token? Peek => _position < tokens.Count ? tokens[_position] : null;

        public Value ParseExpression(int depth)
        {
            Guard(depth);
            var left = ParseTerm(depth);
            while (Peek is { Kind: Kind.Plus or Kind.Minus } op)
            {
                _position++;
                var right = ParseTerm(depth);
                var sign = op.Kind == Kind.Plus ? 1 : -1;
                var result = right.Percent is { } p
                    ? left.Number + (sign * left.Number * p / 100)
                    : left.Number + (sign * right.Number);
                left = new Value(Finite(result), null);
            }

            return left;
        }

        private Value ParseTerm(int depth)
        {
            var left = ParseFactor(depth);
            while (Peek is { } next)
            {
                if (next.Kind is Kind.Times or Kind.Divide)
                {
                    _position++;
                    var right = ParseFactor(depth);
                    if (next.Kind == Kind.Divide && right.Number == 0)
                    {
                        throw new InvalidOperationException("Division by zero.");
                    }

                    left = new Value(Finite(next.Kind == Kind.Times ? left.Number * right.Number : left.Number / right.Number), null);
                }
                else if (next.Kind == Kind.Of)
                {
                    if (left.Percent is null)
                    {
                        throw new InvalidOperationException("'of' needs a percentage on its left.");
                    }

                    _position++;
                    var right = ParseFactor(depth);
                    left = new Value(Finite(left.Number * right.Number), null);
                }
                else if (next.Kind is Kind.Constant or Kind.Function or Kind.Open
                         && _position > 0 && tokens[_position - 1].Kind is Kind.Number or Kind.Constant or Kind.Close or Kind.Percent)
                {
                    var right = ParseFactor(depth);
                    left = new Value(Finite(left.Number * right.Number), null);
                }
                else
                {
                    break;
                }
            }

            return left;
        }

        private Value ParseFactor(int depth)
        {
            if (Peek is { Kind: Kind.Plus or Kind.Minus } sign)
            {
                _position++;
                var inner = ParseFactor(depth + 1);
                return sign.Kind == Kind.Minus ? new Value(-inner.Number, -inner.Percent) : inner;
            }

            return ParsePower(depth);
        }

        private Value ParsePower(int depth)
        {
            var baseValue = ParsePrimary(depth);
            if (Peek is { Kind: Kind.Power })
            {
                _position++;
                var exponent = ParseFactor(depth + 1);
                return new Value(Finite(Math.Pow(baseValue.Number, exponent.Number)), null);
            }

            return baseValue;
        }

        private Value ParsePrimary(int depth)
        {
            Guard(depth);
            var token = Peek ?? throw new InvalidOperationException("Unexpected end.");
            double value;
            switch (token.Kind)
            {
                case Kind.Number or Kind.Constant:
                    _position++;
                    value = token.Value;
                    break;
                case Kind.Open:
                    value = ParseBracketed(depth + 1);
                    break;
                case Kind.Function:
                    _position++;
                    if (Peek is not { Kind: Kind.Open })
                    {
                        throw new InvalidOperationException("Functions need brackets.");
                    }

                    value = Apply(token.Text, ParseBracketed(depth + 1));
                    break;
                default:
                    throw new InvalidOperationException("Unexpected token.");
            }

            if (Peek is { Kind: Kind.Percent })
            {
                _position++;
                return new Value(value / 100, value);
            }

            return new Value(value, null);
        }

        private double ParseBracketed(int depth)
        {
            _position++; // opener
            var inner = ParseExpression(depth);
            if (Peek is not { Kind: Kind.Close })
            {
                throw new InvalidOperationException("Missing closer.");
            }

            _position++;
            return inner.Number;
        }

        private static double Apply(string function, double x)
        {
            var result = function switch
            {
                "sqrt" => x < 0 ? double.NaN : Math.Sqrt(x),
                "abs" => Math.Abs(x),
                "sin" => Math.Sin(x),
                "cos" => Math.Cos(x),
                "tan" => Math.Tan(x),
                "asin" => x is < -1 or > 1 ? double.NaN : Math.Asin(x),
                "acos" => x is < -1 or > 1 ? double.NaN : Math.Acos(x),
                "atan" => Math.Atan(x),
                "ln" => x <= 0 ? double.NaN : Math.Log(x),
                "log" or "log10" => x <= 0 ? double.NaN : Math.Log10(x),
                "exp" => Math.Exp(x),
                "floor" => Math.Floor(x),
                "ceil" => Math.Ceiling(x),
                "round" => Math.Round(x, MidpointRounding.AwayFromZero),
                _ => double.NaN,
            };
            return Finite(result);
        }

        private static double Finite(double value) =>
            double.IsFinite(value) ? value : throw new InvalidOperationException("Not finite.");

        private static void Guard(int depth)
        {
            if (depth >= 32)
            {
                throw new InvalidOperationException("Too deep.");
            }
        }
    }

    /// <summary>Rounds to <paramref name="digits"/> significant digits (removes binary noise: 0.1+0.2 = 0.3).</summary>
    public static double RoundSignificant(double value, int digits)
    {
        if (value == 0 || !double.IsFinite(value))
        {
            return value == 0 ? 0 : value;
        }

        var magnitude = (int)Math.Floor(Math.Log10(Math.Abs(value))) + 1;
        var decimals = digits - magnitude;
        if (decimals is >= 0 and <= 15)
        {
            return Math.Round(value, decimals, MidpointRounding.AwayFromZero);
        }

        var scale = Math.Pow(10, decimals);
        return Math.Round(value * scale, MidpointRounding.AwayFromZero) / scale;
    }

    /// <summary>
    /// Scientific (≤ 8 significant digits, "e") for |x| ≥ 1e12 or below 1e-6;
    /// otherwise grouped decimal with up to 8 fraction digits. −0 prints as 0.
    /// </summary>
    public static string Format(double value, CultureInfo culture)
    {
        if (value == 0)
        {
            return "0";
        }

        var magnitude = Math.Abs(value);
        if (magnitude >= 1e12 || magnitude < 1e-6)
        {
            var exponent = (int)Math.Floor(Math.Log10(magnitude));
            var mantissa = RoundSignificant(value / Math.Pow(10, exponent), 8);
            if (Math.Abs(mantissa) >= 10)
            {
                mantissa /= 10;
                exponent++;
            }

            var mantissaText = mantissa.ToString("0.#######", culture);
            return mantissaText + "e" + exponent.ToString(CultureInfo.InvariantCulture);
        }

        return value.ToString("#,##0.########", culture);
    }

    /// <summary>
    /// Tab completion text: the shortest round-trip decimal, locale decimal
    /// mark, negatives in brackets so "(-4)^2" stays 16.
    /// </summary>
    public static string ReusableText(double value, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        var text = value.ToString("R", CultureInfo.InvariantCulture);
        if (text.EndsWith(".0", StringComparison.Ordinal))
        {
            text = text[..^2];
        }

        if (text.Contains('E'))
        {
            text = value.ToString("0.###############", CultureInfo.InvariantCulture);
        }

        text = text.Replace(".", culture.NumberFormat.NumberDecimalSeparator, StringComparison.Ordinal);
        return value < 0 ? $"({text})" : text;
    }
}
