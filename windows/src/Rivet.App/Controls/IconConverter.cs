// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Avalonia.Data.Converters;
using FluentIcons.Common;

namespace Rivet.App.Controls;

/// <summary>
/// Core code names icons with Fluent UI System Icons strings ("Screenshot");
/// this turns them into the icon enum. Unknown names show a neutral glyph.
/// </summary>
public sealed class IconConverter : IValueConverter
{
    public static IconConverter Instance { get; } = new();

    public static Symbol Parse(string? name) =>
        name is not null && Enum.TryParse<Symbol>(name, ignoreCase: false, out var symbol) ? symbol : Symbol.Apps;

    public static bool IsKnown(string name) => Enum.TryParse<Symbol>(name, ignoreCase: false, out _);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Parse(value as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value?.ToString() ?? string.Empty;
}
