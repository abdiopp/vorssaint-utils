// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace Rivet.App.Controls;

/// <summary>
/// One settings row: icon tile, title, optional description, and a trailing
/// control (the row's content). Usage:
/// <code>&lt;c:SettingsRow Icon="Camera" Title="{l:Tr ...}" Description="{l:Tr ...}"&gt;&lt;ToggleSwitch Classes="compact"/&gt;&lt;/c:SettingsRow&gt;</code>
/// </summary>
public class SettingsRow : ContentControl
{
    public static readonly StyledProperty<string?> IconProperty =
        AvaloniaProperty.Register<SettingsRow, string?>(nameof(Icon));

    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<SettingsRow, string?>(nameof(Title));

    public static readonly StyledProperty<string?> DescriptionProperty =
        AvaloniaProperty.Register<SettingsRow, string?>(nameof(Description));

    public string? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Description
    {
        get => GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }
}

/// <summary>A titled card that groups settings rows (Windows 11 Settings look).</summary>
public class SettingsCard : HeaderedContentControl
{
    public static readonly StyledProperty<string?> DescriptionProperty =
        AvaloniaProperty.Register<SettingsCard, string?>(nameof(Description));

    public string? Description
    {
        get => GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }
}

/// <summary>Page title plus intro text at the top of every Settings page.</summary>
public class PageHeader : TemplatedControl
{
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<PageHeader, string?>(nameof(Title));

    public static readonly StyledProperty<string?> DescriptionProperty =
        AvaloniaProperty.Register<PageHeader, string?>(nameof(Description));

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Description
    {
        get => GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }
}
