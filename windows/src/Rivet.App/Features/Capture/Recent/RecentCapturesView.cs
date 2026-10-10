// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using FluentIcons.Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Controls;
using Rivet.Core.Capture;
using Rivet.Core.Features;
using Rivet.Core.Localization;

namespace Rivet.App.Features.Capture.Recent;

/// <summary>
/// The recent-captures list (spec 01 §3.12): a header with Clear history,
/// rows with a thumbnail, the kind, the time and Restore / Open / Remove.
/// Used by the floating palette and inside the tray panel.
/// </summary>
internal sealed class RecentCapturesView : UserControl
{
    private readonly IServiceProvider _services;
    private readonly RecentCapturesStore _store;
    private readonly FeatureRuntime _runtime;
    private readonly StackPanel _rows = new() { Spacing = 4 };
    private readonly Button _clear;
    private readonly TextBlock _empty;
    private readonly bool _showHeader;

    public RecentCapturesView(IServiceProvider services, bool showHeader = true, Action? close = null)
    {
        _services = services;
        _store = services.GetRequiredService<RecentCapturesStore>();
        _runtime = services.GetRequiredService<FeatureRuntime>();
        _showHeader = showHeader;

        _clear = CaptureUi.IconButton("Delete", L.Get("recentCaptures.clear"), () => _ = ClearAsync());
        _empty = new TextBlock
        {
            Text = L.Get("recentCaptures.empty"),
            Classes = { "caption" },
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(12, 18),
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), ColumnSpacing = 6, Margin = new Thickness(2, 0, 0, 4) };
        var icon = new SymbolIcon { Symbol = FluentIcons.Common.Symbol.History, FontSize = 16, VerticalAlignment = VerticalAlignment.Center };
        icon.Bind(SymbolIcon.ForegroundProperty, icon.GetResourceObservable("AccentBrush").ToBinding());
        header.Children.Add(icon);
        var title = new TextBlock { Text = L.Get("recentCaptures.title"), FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(title, 1);
        header.Children.Add(title);
        if (showHeader)
        {
            Grid.SetColumn(_clear, 2);
            header.Children.Add(_clear);
        }

        if (close is not null)
        {
            var closeButton = CaptureUi.IconButton("Dismiss", L.Get("win.capture.close"), close);
            Grid.SetColumn(closeButton, 3);
            header.Children.Add(closeButton);
        }

        var scroller = new ScrollViewer
        {
            MaxHeight = 300,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = new StackPanel { Children = { _empty, _rows } },
        };
        var root = new StackPanel { Spacing = 4 };
        if (showHeader)
        {
            root.Children.Add(header);
        }
        else
        {
            // Inside the panel the hosted header already names the tool; keep Clear at the top right.
            _clear.HorizontalAlignment = HorizontalAlignment.Right;
            root.Children.Add(_clear);
        }

        root.Children.Add(scroller);
        Content = root;
        Rebuild();
    }

    /// <summary>Restore and Open ask the host to get out of the way first (palette hides, panel closes).</summary>
    public event EventHandler? Dismissing;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _store.Changed += OnChanged;
        Rebuild();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _store.Changed -= OnChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(Rebuild);

    private void Rebuild()
    {
        _rows.Children.Clear();
        var entries = _store.Entries.Where(IsShown).ToList();
        foreach (var entry in entries)
        {
            _rows.Children.Add(Row(entry));
        }

        _empty.IsVisible = entries.Count == 0;
        _clear.IsEnabled = entries.Count > 0 && !_store.IsFrozen;
    }

    /// <summary>Entries of an uninstalled feature are hidden.</summary>
    private bool IsShown(RecentCaptureEntry entry) =>
        entry.IsScreenshot ? _runtime.IsAvailable(FeatureIds.Screenshot) : _runtime.IsAvailable(FeatureIds.ScreenRecorder);

    private Control Row(RecentCaptureEntry entry)
    {
        var thumbnailPath = _store.ThumbnailPath(entry);
        Control thumbnailContent = thumbnailPath.Length > 0 && ImageInterop.LoadThumbnail(thumbnailPath, 208) is { } bitmap
            ? new Image { Source = bitmap, Stretch = Stretch.Uniform }
            : new SymbolIcon
            {
                Symbol = entry.IsScreenshot ? FluentIcons.Common.Symbol.Image : FluentIcons.Common.Symbol.Video,
                FontSize = 24,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0.6,
            };
        var compact = !_showHeader;
        var thumbnail = new Border
        {
            Width = compact ? 88 : 104,
            Height = compact ? 58 : 68,
            CornerRadius = new CornerRadius(7),
            ClipToBounds = true,
            Background = new SolidColorBrush(Color.FromArgb(33, 0, 0, 0)),
            Child = thumbnailContent,
        };

        var kind = new TextBlock
        {
            Text = L.Get(entry.IsScreenshot ? "recentCaptures.screenshot" : "recentCaptures.recording"),
            Classes = { "rowTitle" },
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var details = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Children = { kind } };
        if (!entry.IsScreenshot && entry.RecordingPath is { } path)
        {
            details.Children.Add(new TextBlock { Text = Path.GetFileName(path), Classes = { "caption" }, TextTrimming = TextTrimming.PrefixCharacterEllipsis, MaxLines = 1 });
        }

        details.Children.Add(new TextBlock { Text = When(entry.CreatedAt), Classes = { "caption", "tertiary" } });

        var primary = CaptureUi.TextButton(
            L.Get(entry.IsScreenshot ? "recentCaptures.restore" : "recentCaptures.open"),
            () => Activate(entry));
        primary.Padding = new Thickness(10, 3);
        var remove = CaptureUi.IconButton("Delete", L.Get("recentCaptures.remove"), () => _store.Remove(entry.Id), 14);
        remove.IsEnabled = !_store.IsFrozen;
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Children = { primary, remove } };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 10 };
        grid.Children.Add(thumbnail);
        Grid.SetColumn(details, 1);
        grid.Children.Add(details);
        if (compact)
        {
            // The panel is narrow: the buttons go under the text.
            actions.Margin = new Thickness(-4, 2, 0, 0);
            details.Children.Add(actions);
        }
        else
        {
            Grid.SetColumn(actions, 2);
            grid.Children.Add(actions);
        }
        var row = new Border { Padding = new Thickness(4), CornerRadius = new CornerRadius(8), Child = grid };
        AutomationProperties.SetName(row, kind.Text);
        return row;
    }

    private void Activate(RecentCaptureEntry entry)
    {
        Dismissing?.Invoke(this, EventArgs.Empty);
        DispatcherTimer.RunOnce(() => _services.GetRequiredService<RecentCapturesActions>().Activate(entry), TimeSpan.FromMilliseconds(120));
    }

    private async Task ClearAsync()
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        var confirmed = await ConfirmDialog.ShowAsync(owner, L.Get("recentCaptures.clear"), L.Get("win.capture.clearHistoryMessage"), L.Get("recentCaptures.clear"), L.Get("screenshot.cancel"), destructive: true);
        if (confirmed)
        {
            _store.Clear();
        }
    }

    private static string When(DateTimeOffset createdAt)
    {
        var (unit, value) = RelativeTime.Bucket(createdAt, DateTimeOffset.Now);
        return unit switch
        {
            "now" => L.Get("win.capture.justNow"),
            "minutes" => L.Format("win.capture.minutesAgoFormat", value),
            "hours" => L.Format("win.capture.hoursAgoFormat", value),
            _ => RelativeTime.Date(createdAt, Localizer.Current.Culture),
        };
    }
}
