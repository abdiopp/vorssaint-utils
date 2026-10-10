// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using FluentIcons.Avalonia;
using Rivet.App.Controls;
using Rivet.Core.Localization;
using Rivet.Core.Maintenance.Cleaner;
using Rivet.Core.Platform;

namespace Rivet.App.Features.Maintenance;

/// <summary>
/// Small builders shared by the Cleaner, Uninstaller, App updates, package
/// manager, Kill Process and Port Manager views, so all six look alike and
/// follow the panel conventions (card, rowTitle, caption, theme brushes).
/// </summary>
internal static class MaintenanceUi
{
    public const double PanelWidth = 316;

    public static TextBlock Text(string text, double size = 13, FontWeight? weight = null, bool wrap = true, TextTrimming? trimming = null)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = size,
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            TextTrimming = trimming ?? (wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis),
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (weight is { } w)
        {
            block.FontWeight = w;
        }

        return block;
    }

    public static TextBlock Title(string text) => Text(text, 14, FontWeight.SemiBold);

    public static TextBlock Caption(string text, bool wrap = true) =>
        new() { Text = text, Classes = { "caption" }, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap, TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };

    /// <summary>A secondary line that keeps the end of a long path visible ("…\AppData\Local\Temp").</summary>
    public static TextBlock PathText(string text) =>
        new() { Text = text, Classes = { "caption" }, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.PrefixCharacterEllipsis, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };

    public static TextBlock SectionTitle(string text) =>
        new() { Text = text.ToUpper(Localizer.Current.Culture), Classes = { "sectionTitle" }, Margin = new Thickness(4, 4, 0, 0) };

    public static TextBlock Colored(string text, string brushKey, double size = 12, FontWeight? weight = null)
    {
        var block = Text(text, size, weight);
        Brush(block, TextBlock.ForegroundProperty, brushKey);
        return block;
    }

    public static SymbolIcon Icon(string name, double size = 16, string? brushKey = "AccentBrush")
    {
        var icon = new SymbolIcon { Symbol = IconConverter.Parse(name), FontSize = size, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        if (brushKey is not null)
        {
            Brush(icon, SymbolIcon.ForegroundProperty, brushKey);
        }

        return icon;
    }

    public static Button IconButton(string icon, string tooltip, Action click, double size = 14)
    {
        var button = new Button { Classes = { "icon" }, Content = Icon(icon, size, null), VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(button, tooltip);
        AutomationProperties.SetName(button, tooltip);
        button.Click += (_, _) => click();
        return button;
    }

    public static Button Button(string text, Action click, string? icon = null, bool accent = false, bool danger = false, bool stretch = false)
    {
        object content = icon is null
            ? text
            : HStack(6, Icon(icon, 14, null), new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        var button = new Button { Content = content, HorizontalAlignment = stretch ? HorizontalAlignment.Stretch : HorizontalAlignment.Left, HorizontalContentAlignment = HorizontalAlignment.Center };
        if (accent)
        {
            button.Classes.Add("accent");
        }

        if (danger)
        {
            Brush(button, Avalonia.Controls.Button.BackgroundProperty, "DangerBrush");
            button.Foreground = Brushes.White;
        }

        AutomationProperties.SetName(button, text);
        button.Click += (_, _) => click();
        return button;
    }

    public static Button LinkButton(string text, Action click)
    {
        var button = new Button { Classes = { "link" }, Content = text, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Left };
        button.Click += (_, _) => click();
        return button;
    }

    public static Border Pill(string text, string? backgroundKey = null, string? foregroundKey = null)
    {
        var label = new TextBlock { Text = text };
        if (foregroundKey is not null)
        {
            Brush(label, TextBlock.ForegroundProperty, foregroundKey);
        }

        var pill = new Border { Classes = { "pill" }, Child = label, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        if (backgroundKey is not null)
        {
            Brush(pill, Border.BackgroundProperty, backgroundKey);
        }

        return pill;
    }

    public static Border Card(Control child, double padding = 10) =>
        new() { Classes = { "card" }, Padding = new Thickness(padding), Child = child };

    public static StackPanel VStack(double spacing, params Control?[] children)
    {
        var panel = new StackPanel { Spacing = spacing };
        foreach (var child in children.OfType<Control>())
        {
            panel.Children.Add(child);
        }

        return panel;
    }

    public static StackPanel HStack(double spacing, params Control?[] children)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = spacing };
        foreach (var child in children.OfType<Control>())
        {
            panel.Children.Add(child);
        }

        return panel;
    }

    /// <summary>A grid row: children go into the columns in order (null skips a column).</summary>
    public static Grid Columns(string definition, double spacing, params Control?[] children)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(definition), ColumnSpacing = spacing };
        for (var i = 0; i < children.Length; i++)
        {
            if (children[i] is { } child)
            {
                Grid.SetColumn(child, i);
                grid.Children.Add(child);
            }
        }

        return grid;
    }

    public static void Brush(Control control, AvaloniaProperty property, string key) =>
        control.Bind(property, control.GetResourceObservable(key).ToBinding());

    public static CheckBox CheckBox(bool? state, Action<bool> changed, string automationName)
    {
        var box = new CheckBox { IsChecked = state, IsThreeState = false, MinWidth = 0, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(box, automationName);
        box.IsCheckedChanged += (_, _) => changed(box.IsChecked == true);
        return box;
    }

    /// <summary>A thin indeterminate (or determinate) bar used as the busy indicator.</summary>
    public static ProgressBar Progress(double? value, double width = double.NaN)
    {
        var bar = new ProgressBar { Minimum = 0, Maximum = 1, Height = 4, MinHeight = 4, MinWidth = 0, IsIndeterminate = value is null, Value = value ?? 0, VerticalAlignment = VerticalAlignment.Center };
        if (!double.IsNaN(width))
        {
            bar.Width = width;
        }

        return bar;
    }

    public static Control AppIcon(PixelBuffer? pixels, double size, string fallbackIcon = "Apps")
    {
        if (pixels is null)
        {
            return new Border { Width = size, Height = size, Child = Icon(fallbackIcon, size * 0.7) };
        }

        return new Image { Source = ImageInterop.ToBitmap(pixels), Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
    }

    public static string Size(long bytes) => ByteSize.Format(bytes);

    /// <summary>Short time today, short date and time otherwise.</summary>
    public static string When(DateTime utc)
    {
        var local = utc.ToLocalTime();
        var culture = Localizer.Current.Culture;
        return local.Date == DateTime.Today ? local.ToString("t", culture) : local.ToString("g", culture);
    }

    public static Window? WindowOf(Control control) => TopLevel.GetTopLevel(control) as Window;

    public static Task<bool> ConfirmAsync(Control anchor, string title, string message, string confirm, bool destructive = false) =>
        ConfirmDialog.ShowAsync(WindowOf(anchor), title, message, confirm, L.Get("Strings.uninstallerCancel"), destructive);

    /// <summary>An information dialog with a single OK button.</summary>
    public static async Task NotifyAsync(Control anchor, string title, string message)
    {
        var owner = WindowOf(anchor);
        var dialog = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            MaxWidth = 460,
        };
        var ok = new Button { Content = L.Get("ShelfPromiseDeliveryStrings.okButton"), IsDefault = true, IsCancel = true, Classes = { "accent" }, MinWidth = 96, HorizontalContentAlignment = HorizontalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        ok.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 412 },
                ok,
            },
        };
        if (owner is not null)
        {
            await dialog.ShowDialog(owner);
        }
        else
        {
            var closed = new TaskCompletionSource();
            dialog.Closed += (_, _) => closed.TrySetResult();
            dialog.Show();
            await closed.Task;
        }
    }

    public static ContextMenu Menu(params (string Text, Action Click, bool Enabled)[] items)
    {
        var menu = new ContextMenu();
        foreach (var (text, click, enabled) in items)
        {
            var item = new MenuItem { Header = text, IsEnabled = enabled };
            item.Click += (_, _) => click();
            menu.Items.Add(item);
        }

        return menu;
    }

    public static string Percent(double? value) =>
        value is { } v ? v.ToString("0.0", Localizer.Current.Culture) + "%" : "—";

    public static string Number(long value) => value.ToString("N0", Localizer.Current.Culture);

    public static void Swap(ContentControl host, Control content) => host.Content = content;

    /// <summary>
    /// Brings a bound collection in line with <paramref name="source"/> in
    /// place (replace changed slots, trim or append), so a virtualized list
    /// refreshing every few seconds keeps its scroll position.
    /// </summary>
    public static void SyncList<T>(System.Collections.ObjectModel.ObservableCollection<T> target, IReadOnlyList<T> source)
    {
        var shared = Math.Min(target.Count, source.Count);
        for (var i = 0; i < shared; i++)
        {
            if (!EqualityComparer<T>.Default.Equals(target[i], source[i]))
            {
                target[i] = source[i];
            }
        }

        while (target.Count > source.Count)
        {
            target.RemoveAt(target.Count - 1);
        }

        for (var i = target.Count; i < source.Count; i++)
        {
            target.Add(source[i]);
        }
    }

    /// <summary>
    /// Takes a long-lived control (a search box keeping focus, a list keeping
    /// its scroll offset) out of its old parent so a rebuilt layout can host it again.
    /// </summary>
    public static T Reuse<T>(T control)
        where T : Control
    {
        switch (control.Parent)
        {
            case Panel panel:
                panel.Children.Remove(control);
                break;
            case ContentControl host when ReferenceEquals(host.Content, control):
                host.Content = null;
                break;
            case Decorator decorator when ReferenceEquals(decorator.Child, control):
                decorator.Child = null;
                break;
        }

        return control;
    }
}
