// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentIcons.Avalonia;
using FluentIcons.Common;
using Rivet.App.Controls;
using Rivet.App.Shell;
using Rivet.Core.Localization;
using Rivet.Core.Modules.Scratchpad;
using Rivet.Core.Platform;

namespace Rivet.App.Features.Scratchpad;

/// <summary>
/// The floating notes window (spec 07 §3.3.4–3.3.8): a 34 DIP header (drag
/// handle, pin, close), a tab bar, the save-failure banner, a plain-text
/// editor that is never recreated (so undo survives the preview), the
/// Markdown preview laid over it, a find bar, the formatting row and the
/// footer. Borderless and resizable from 6 DIP edges and 12 DIP corners.
/// </summary>
public sealed class ScratchpadWindow : Window
{
    public const double InitialWidth = 380;
    public const double InitialHeight = 300;

    private readonly ScratchpadService _service;
    private readonly Border _card;
    private readonly TextBox _editor;
    private readonly ScrollViewer _previewScroll;
    private readonly StackPanel _tabStrip = new() { Orientation = Orientation.Horizontal, Spacing = 2 };
    private readonly ScrollViewer _tabScroll;
    private readonly Button _addTab;
    private readonly Button _pin;
    private readonly Border _banner;
    private readonly Border _formatRow;
    private readonly Border _findBar;
    private readonly TextBox _findBox = new() { PlaceholderText = "Find", MinWidth = 120, Classes = { "compact" } };
    private readonly TextBox _replaceBox = new() { PlaceholderText = "Replace", MinWidth = 120, IsVisible = false, Classes = { "compact" } };
    private readonly TextBlock _findCount = new() { Classes = { "caption" }, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right };
    private readonly StackPanel _replaceButtons = new() { Orientation = Orientation.Horizontal, Spacing = 4, IsVisible = false };
    private readonly ToggleButton _formatToggle;
    private readonly ToggleButton _previewToggle;
    private readonly Button _copy;
    private readonly Button _save;
    private readonly Button _clear;
    private readonly TextBlock _copyLabel = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
    private readonly SymbolIcon _copyIcon = new() { Symbol = Symbol.Copy, FontSize = 15 };
    private Guid _shownPadId;
    private bool _settingText;
    private bool _chromeApplied;
    private IDisposable? _copiedTimer;
    private int _findIndex = -1;
    private List<int> _matches = [];

    public ScratchpadWindow(ScratchpadService service)
    {
        _service = service;
        WindowDecorations = WindowDecorations.None;
        ShowInTaskbar = false;
        CanResize = true;
        Width = InitialWidth;
        Height = InitialHeight;
        MinWidth = 280;
        MinHeight = 220;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Transparent];
        Title = Core.App.AppIdentity.DisplayName;

        _editor = new TextBox
        {
            AcceptsReturn = true,
            AcceptsTab = true,
            NewLine = "\n",
            TextWrapping = TextWrapping.Wrap,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(7, 5, 7, 5),
            PlaceholderText = L.Get("scratchpad.placeholder"),
            IsInactiveSelectionHighlightEnabled = true,
            MinHeight = 0,
            VerticalContentAlignment = VerticalAlignment.Top,
        };
        ScrollViewer.SetVerticalScrollBarVisibility(_editor, ScrollBarVisibility.Auto);
        _editor.TextChanged += (_, _) => OnEditorTextChanged();
        _editor.Resources["TextControlBackgroundFocused"] = Brushes.Transparent;
        _editor.Resources["TextControlBackgroundPointerOver"] = Brushes.Transparent;
        _editor.Resources["TextControlBorderThemeThicknessFocused"] = new Thickness(0);

        _previewScroll = new ScrollViewer { IsVisible = false, Padding = new Thickness(10, 6), HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };

        var header = BuildHeader(out _pin);
        _tabScroll = new ScrollViewer
        {
            Content = _tabStrip,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _addTab = IconButton(Symbol.Add, L.Get("scratchpad.newPad"), () => _ = _service.NewTabAsync());
        var more = IconButton(Symbol.MoreHorizontal, L.Get("scratchpad.padActions"), () => { });
        more.Flyout = BuildPadMenu();
        var tabBar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
            Height = 32,
            Margin = new Thickness(6, 0, 6, 0),
            Children = { _tabScroll, Column(_addTab, 1), Column(more, 2) },
        };

        _banner = new Border
        {
            IsVisible = false,
            Padding = new Thickness(10, 6),
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                ColumnSpacing = 8,
                Children =
                {
                    new SymbolIcon { Symbol = Symbol.Warning, FontSize = 14, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 0, 0) },
                    Column(new TextBlock { Text = L.Get("scratchpad.saveFailed"), TextWrapping = TextWrapping.Wrap, FontSize = 12 }, 1),
                },
            },
        };
        _banner.Bind(Border.BackgroundProperty, this.GetResourceObservable("WarningSoftBrush").ToBinding());

        _findBar = BuildFindBar();
        _formatRow = BuildFormatRow();
        var footer = BuildFooter(out _formatToggle, out _previewToggle, out _copy, out _save, out _clear);

        var editorArea = new Grid { Children = { _editor, _previewScroll } };
        var body = new Grid { RowDefinitions = new RowDefinitions("34,32,Auto,Auto,*,Auto,Auto") };
        body.Children.Add(header);
        body.Children.Add(Row(tabBar, 1));
        body.Children.Add(Row(new Border { Classes = { "separator" }, VerticalAlignment = VerticalAlignment.Bottom }, 1));
        body.Children.Add(Row(_banner, 2));
        body.Children.Add(Row(_findBar, 3));
        body.Children.Add(Row(editorArea, 4));
        body.Children.Add(Row(_formatRow, 5));
        body.Children.Add(Row(footer, 6));

        _card = new Border
        {
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(31, 255, 255, 255)),
            ClipToBounds = true,
            Child = body,
        };

        Content = new Grid { Children = { _card, BuildResizeZones() } };
        AddHandler(KeyDownEvent, OnPreviewKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Opened += (_, _) => OnOpenedOnce();
        ActualThemeVariantChanged += (_, _) => ApplyBackground();
    }

    public bool Pinned { get; private set; }

    public bool PreviewOn { get; private set; }

    public bool FormatExpanded { get; private set; }

    public TextBox Editor => _editor;

    public string ShownText => _editor.Text ?? string.Empty;

    /// <summary>Resets the per-show state: preview off, formatting collapsed, pin from the setting.</summary>
    public void ResetForShow(bool pinned)
    {
        Pinned = pinned;
        SetPreview(false);
        FormatExpanded = false;
        _formatToggle.IsChecked = false;
        CloseFind();
        UpdateChrome();
    }

    public void SetPinned(bool pinned)
    {
        Pinned = pinned;
        UpdateChrome();
    }

    /// <summary>Shows the document: tabs, banner, and the selected tab's text (replacing it wipes undo history).</summary>
    public void Render(ScratchpadDocument document, bool saveFailed, double textSize, double opacity, bool forceText = false)
    {
        _banner.IsVisible = saveFailed;
        _editor.FontSize = textSize;
        ApplyBackground(opacity);
        var selected = document.Selected;
        if (forceText || selected.Id != _shownPadId)
        {
            _shownPadId = selected.Id;
            SetEditorText(selected.Text);
        }

        RebuildTabs(document);
        _addTab.IsEnabled = document.CanAddPad;
        ToolTip.SetTip(_addTab, document.CanAddPad ? L.Get("scratchpad.newPad") : L.Format("scratchpad.padLimitFormat", ScratchpadDocument.MaxPads));
        UpdateChrome();
    }

    /// <summary>Caret to the end and keyboard focus to the editor.</summary>
    public void FocusEditor()
    {
        if (PreviewOn)
        {
            return;
        }

        _editor.Focus();
        _editor.CaretIndex = _editor.Text?.Length ?? 0;
    }

    /// <summary>Replaces the editor text through the editor (one undo step) and sets the selection.</summary>
    public void ApplyEdit(TextEdit edit)
    {
        var old = _editor.Text ?? string.Empty;
        var next = edit.Text;
        if (old != next)
        {
            var prefix = 0;
            var max = Math.Min(old.Length, next.Length);
            while (prefix < max && old[prefix] == next[prefix])
            {
                prefix++;
            }

            var suffix = 0;
            while (suffix < old.Length - prefix && suffix < next.Length - prefix && old[^(suffix + 1)] == next[^(suffix + 1)])
            {
                suffix++;
            }

            _editor.SelectionStart = prefix;
            _editor.SelectionEnd = old.Length - suffix;
            _editor.SelectedText = next[prefix..(next.Length - suffix)];
        }

        _editor.SelectionStart = edit.SelectionStart;
        _editor.SelectionEnd = edit.SelectionEnd;

        // TextChanged may arrive later; record the edit now so the immediate save sees it.
        _service.OnTextEdited(_shownPadId, _editor.Text ?? string.Empty);
        if (!PreviewOn)
        {
            _editor.Focus();
        }
    }

    public void ShowCopied()
    {
        _copyIcon.Symbol = Symbol.Checkmark;
        _copyLabel.Text = L.Get("scratchpad.copied");
        _copyIcon.Bind(SymbolIcon.ForegroundProperty, this.GetResourceObservable("SuccessBrush").ToBinding());
        _copiedTimer?.Dispose();
        _copiedTimer = DispatcherTimer.RunOnce(() =>
        {
            _copyIcon.Symbol = Symbol.Copy;
            _copyIcon.ClearValue(SymbolIcon.ForegroundProperty);
            _copyLabel.Text = string.Empty;
        }, TimeSpan.FromSeconds(1.2));
    }

    public void SetPreview(bool on)
    {
        if (on && string.IsNullOrEmpty(_editor.Text))
        {
            on = false;
        }

        PreviewOn = on;
        _previewToggle.IsChecked = on;
        ToolTip.SetTip(_previewToggle, on ? L.Get("scratchpad.editText") : L.Get("scratchpad.previewFormatting"));
        if (on)
        {
            CloseFind();
            FormatExpanded = false;
            _formatToggle.IsChecked = false;
            _previewScroll.Content = MarkdownPreview.Render(_editor.Text ?? string.Empty, PreviewStyle());
            _previewScroll.IsVisible = true;
            _editor.IsVisible = false;
            Focus();
        }
        else
        {
            _previewScroll.IsVisible = false;
            _previewScroll.Content = null;
            _editor.IsVisible = true;
        }

        UpdateChrome();
    }

    public void OpenFind()
    {
        if (PreviewOn)
        {
            SetPreview(false);
        }

        _findBar.IsVisible = true;
        _findBox.Focus();
        _findBox.SelectAll();
        RefreshMatches();
    }

    public void CloseFind()
    {
        if (!_findBar.IsVisible)
        {
            return;
        }

        _findBar.IsVisible = false;
        _matches = [];
        _findIndex = -1;
        if (!PreviewOn)
        {
            _editor.Focus();
        }
    }

    public bool FindBarFocused => _findBar.IsVisible && (_findBox.IsFocused || _replaceBox.IsFocused);

    public void FindStep(int direction)
    {
        if (!_findBar.IsVisible)
        {
            OpenFind();
        }

        if (_matches.Count == 0)
        {
            RefreshMatches();
            if (_matches.Count == 0)
            {
                return;
            }
        }

        _findIndex = ((_findIndex + direction) % _matches.Count + _matches.Count) % _matches.Count;
        SelectMatch();
    }

    /// <summary>Puts the window on the monitor: centred, 42 % down the free space, 16 DIP inside the work area.</summary>
    public void PlaceInitially(ScreenInfo screen)
    {
        var scale = screen.Scale <= 0 ? 1 : screen.Scale;
        var work = screen.WorkArea;
        var w = (int)Math.Round(Width * scale);
        var h = (int)Math.Round(Height * scale);
        var inset = (int)Math.Round(16 * scale);
        var x = work.X + ((work.Width - w) / 2);
        var y = work.Y + (int)Math.Round(0.42 * (work.Height - h));
        x = Math.Clamp(x, work.X + inset, Math.Max(work.X + inset, work.Right - w - inset));
        y = Math.Clamp(y, work.Y + inset, Math.Max(work.Y + inset, work.Bottom - h - inset));
        Position = new Avalonia.PixelPoint(x, y);
    }

    private void OnOpenedOnce()
    {
        if (_chromeApplied)
        {
            return;
        }

        _chromeApplied = true;
        WindowInterop.ApplyChrome(this, Core.Platform.WindowChromeOptions.ToolWindow);
        WindowInterop.SetRoundedCorners(this);
        ApplyBackground();
    }

    private double _opacity;

    private void ApplyBackground(double? opacity = null)
    {
        if (opacity is { } value)
        {
            _opacity = value;
        }

        var translucent = ActualTransparencyLevel == WindowTransparencyLevel.AcrylicBlur;
        var baseBrush = this.FindResource(ActualThemeVariant, "PanelBackgroundBrush") as ISolidColorBrush;
        var color = baseBrush?.Color ?? Colors.White;
        // Over the blur the setting controls the opaque fill; without a blur the pad must stay readable.
        _card.Background = new SolidColorBrush(color, translucent ? Math.Max(0.35, _opacity) : 1.0);
        _card.BorderBrush = ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark
            ? new SolidColorBrush(Color.FromArgb(31, 255, 255, 255))
            : new SolidColorBrush(Color.FromArgb(28, 0, 0, 0));
    }

    private MarkdownPreview.Style PreviewStyle()
    {
        IBrush Brush(string key, IBrush fallback) => this.FindResource(ActualThemeVariant, key) as IBrush ?? fallback;
        return new MarkdownPreview.Style(_editor.FontSize, Brush("TextPrimaryBrush", Brushes.Black), Brush("TextSecondaryBrush", Brushes.Gray), Brush("AccentBrush", Brushes.DodgerBlue), _service.OpenLink);
    }

    private void SetEditorText(string text)
    {
        _settingText = true;
        try
        {
            // Programmatic replacement wipes the undo history (no undo into another tab's text).
            _editor.Text = text;
            _editor.CaretIndex = text.Length;
        }
        finally
        {
            _settingText = false;
        }
    }

    private void OnEditorTextChanged()
    {
        if (_settingText)
        {
            return;
        }

        var text = _editor.Text ?? string.Empty;
        if (text.Contains('\r'))
        {
            // Pasted Windows line breaks: the notes model uses "\n".
            var caret = _editor.CaretIndex;
            var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            _settingText = true;
            _editor.Text = normalized;
            _editor.CaretIndex = Math.Min(normalized.Length, caret);
            _settingText = false;
            text = normalized;
        }

        _service.OnTextEdited(_shownPadId, text);
        if (_findBar.IsVisible)
        {
            RefreshMatches();
        }

        UpdateChrome();
    }

    private void UpdateChrome()
    {
        var empty = string.IsNullOrEmpty(_editor.Text);
        _previewToggle.IsEnabled = !empty;
        _copy.IsEnabled = !empty;
        _save.IsEnabled = !empty;
        _clear.IsEnabled = !empty;
        foreach (var control in new Control[] { _previewToggle, _copy, _save, _clear })
        {
            control.Opacity = control.IsEnabled ? 1 : 0.5;
        }

        _formatRow.IsVisible = FormatExpanded && !PreviewOn;
        _pin.Content = new SymbolIcon { Symbol = Pinned ? Symbol.PinOff : Symbol.Pin, FontSize = 13 };
        if (Pinned)
        {
            _pin.Bind(ForegroundProperty, this.GetResourceObservable("AccentBrush").ToBinding());
        }
        else
        {
            _pin.ClearValue(ForegroundProperty);
        }

        ToolTip.SetTip(_pin, Pinned ? L.Get("win.scratchpad.closeOnOutsideClick") : L.Get("scratchpad.keepOpen"));
        AutomationProperties.SetName(_pin, Pinned ? L.Get("win.scratchpad.closeOnOutsideClick") : L.Get("scratchpad.keepOpen"));
    }

    private Control BuildHeader(out Button pin)
    {
        var title = new TextBlock
        {
            Text = L.Get("scratchpad.pageTitle"),
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
            Classes = { "secondary" },
        };
        pin = IconButton(Symbol.Pin, L.Get("scratchpad.keepOpen"), () => _service.TogglePin());
        var close = IconButton(Symbol.Dismiss, L.Get("Strings.menuClose"), () => _service.Hide());
        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
            Background = Brushes.Transparent,
            Children = { title, Column(pin, 1), Column(close, 2) },
        };
        header.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(header).Properties.IsLeftButtonPressed && e.Source is not Button && (e.Source as Visual)?.FindAncestorOfType<Button>() is null)
            {
                BeginMoveDrag(e);
            }
        };
        return header;
    }

    private MenuFlyout BuildPadMenu()
    {
        var rename = new MenuItem { Header = L.Get("scratchpad.renamePad") };
        rename.Click += (_, _) => _ = _service.RenameTabAsync(null);
        var close = new MenuItem { Header = L.Get("scratchpad.closePad") };
        close.Click += (_, _) => _ = _service.CloseTabAsync(null);
        var flyout = new MenuFlyout { Items = { rename, close } };
        flyout.Opening += (_, _) => close.IsEnabled = _service.Document?.Pads.Count > 1;
        return flyout;
    }

    private void RebuildTabs(ScratchpadDocument document)
    {
        _tabStrip.Children.Clear();
        var many = document.Pads.Count > 1;
        foreach (var pad in document.Pads)
        {
            var selected = pad.Id == document.SelectedId;
            var label = new TextBlock
            {
                Text = pad.Name,
                FontSize = 11,
                FontWeight = selected ? FontWeight.SemiBold : FontWeight.Normal,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 104,
            };
            var closeButton = new Button
            {
                Classes = { "icon" },
                Padding = new Thickness(2),
                Content = new SymbolIcon { Symbol = Symbol.Dismiss, FontSize = 9 },
                IsVisible = many && selected,
                VerticalAlignment = VerticalAlignment.Center,
            };
            ToolTip.SetTip(closeButton, L.Get("scratchpad.closePad"));
            AutomationProperties.SetName(closeButton, L.Get("scratchpad.closePad"));
            var padId = pad.Id;
            closeButton.Click += (_, e) =>
            {
                e.Handled = true;
                _ = _service.CloseTabAsync(padId);
            };

            var tab = new Button
            {
                MinWidth = 46,
                MaxWidth = 130,
                Height = 24,
                Padding = new Thickness(8, 0, 4, 0),
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Children = { label, closeButton } },
            };
            if (selected)
            {
                tab.Bind(BackgroundProperty, this.GetResourceObservable("AccentSoftBrush").ToBinding());
            }

            tab.PointerEntered += (_, _) => closeButton.IsVisible = many;
            tab.PointerExited += (_, _) => closeButton.IsVisible = many && selected;
            tab.Click += (_, _) => _service.SelectTab(padId);
            var rename = new MenuItem { Header = L.Get("scratchpad.renamePad") };
            rename.Click += (_, _) => _ = _service.RenameTabAsync(padId);
            var close = new MenuItem { Header = L.Get("scratchpad.closePad"), IsEnabled = many };
            close.Click += (_, _) => _ = _service.CloseTabAsync(padId);
            tab.ContextMenu = new ContextMenu { Items = { rename, close } };
            AutomationProperties.SetName(tab, pad.Name);
            _tabStrip.Children.Add(tab);
            if (selected)
            {
                Dispatcher.UIThread.Post(() => tab.BringIntoView(), DispatcherPriority.Background);
            }
        }
    }

    private Border BuildFindBar()
    {
        _findBox.TextChanged += (_, _) => RefreshMatches();
        _findBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                FindStep(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
            }
        };
        var previous = IconButton(Symbol.ChevronUp, L.Get("win.scratchpad.findPrevious"), () => FindStep(-1));
        var next = IconButton(Symbol.ChevronDown, L.Get("win.scratchpad.findNext"), () => FindStep(1));
        var toggleReplace = IconButton(Symbol.ArrowSwap, L.Get("win.scratchpad.replace"), () =>
        {
            _replaceBox.IsVisible = !_replaceBox.IsVisible;
            _replaceButtons.IsVisible = _replaceBox.IsVisible;
        });
        var close = IconButton(Symbol.Dismiss, L.Get("Strings.menuClose"), CloseFind);
        var replaceOne = new Button { Content = L.Get("win.scratchpad.replace"), FontSize = 11, Padding = new Thickness(8, 2) };
        replaceOne.Click += (_, _) => ReplaceCurrent();
        var replaceAll = new Button { Content = L.Get("win.scratchpad.replaceAll"), FontSize = 11, Padding = new Thickness(8, 2) };
        replaceAll.Click += (_, _) => ReplaceAll();
        _replaceButtons.Children.Add(replaceOne);
        _replaceButtons.Children.Add(replaceAll);
        _findBox.PlaceholderText = L.Get("win.scratchpad.find");
        _replaceBox.PlaceholderText = L.Get("win.scratchpad.replaceWith");
        var bar = new Border
        {
            IsVisible = false,
            Padding = new Thickness(8, 4),
            Child = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new Grid
                    {
                        ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto,Auto,Auto"),
                        ColumnSpacing = 4,
                        Children = { _findBox, Column(_findCount, 1), Column(previous, 2), Column(next, 3), Column(toggleReplace, 4), Column(close, 5) },
                    },
                    new Grid
                    {
                        ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                        ColumnSpacing = 4,
                        Children = { _replaceBox, Column(_replaceButtons, 1) },
                    },
                },
            },
        };
        bar.Bind(Border.BackgroundProperty, this.GetResourceObservable("PanelControlBrush").ToBinding());
        return bar;
    }

    private void RefreshMatches()
    {
        var query = _findBox.Text ?? string.Empty;
        var text = _editor.Text ?? string.Empty;
        _matches = [];
        if (query.Length > 0)
        {
            var index = 0;
            while ((index = text.IndexOf(query, index, StringComparison.CurrentCultureIgnoreCase)) >= 0)
            {
                _matches.Add(index);
                index += Math.Max(1, query.Length);
            }
        }

        if (_matches.Count == 0)
        {
            _findIndex = -1;
            _findCount.Text = query.Length == 0 ? string.Empty : "0/0";
            return;
        }

        var caret = _editor.SelectionStart;
        _findIndex = Math.Max(0, _matches.FindIndex(m => m >= caret));
        SelectMatch();
    }

    private void SelectMatch()
    {
        if (_findIndex < 0 || _findIndex >= _matches.Count)
        {
            return;
        }

        var start = _matches[_findIndex];
        _editor.SelectionStart = start;
        _editor.SelectionEnd = start + (_findBox.Text?.Length ?? 0);
        _findCount.Text = $"{_findIndex + 1}/{_matches.Count}";
    }

    private void ReplaceCurrent()
    {
        if (_findIndex < 0 || _findIndex >= _matches.Count)
        {
            return;
        }

        var query = _findBox.Text ?? string.Empty;
        var text = _editor.Text ?? string.Empty;
        var start = _matches[_findIndex];
        var replacement = _replaceBox.Text ?? string.Empty;
        ApplyEdit(new TextEdit(string.Concat(text.AsSpan(0, start), replacement, text.AsSpan(start + query.Length)), start + replacement.Length, 0));
        RefreshMatches();
    }

    private void ReplaceAll()
    {
        var query = _findBox.Text ?? string.Empty;
        if (query.Length == 0 || _matches.Count == 0)
        {
            return;
        }

        var text = _editor.Text ?? string.Empty;
        var replacement = _replaceBox.Text ?? string.Empty;
        var builder = new System.Text.StringBuilder();
        var last = 0;
        foreach (var match in _matches)
        {
            builder.Append(text, last, match - last).Append(replacement);
            last = match + query.Length;
        }

        builder.Append(text, last, text.Length - last);
        ApplyEdit(new TextEdit(builder.ToString(), 0, 0));
        RefreshMatches();
    }

    private Border BuildFormatRow()
    {
        (Symbol Icon, string Tip, MarkdownMark Mark)[] marks =
        [
            (Symbol.TextBold, "scratchpad.markBold", MarkdownMark.Bold),
            (Symbol.TextItalic, "scratchpad.markItalic", MarkdownMark.Italic),
            (Symbol.TextStrikethrough, "scratchpad.markStrikethrough", MarkdownMark.Strikethrough),
            (Symbol.TextHeader1, "scratchpad.markHeading", MarkdownMark.Heading),
            (Symbol.TextBulletList, "scratchpad.markBullet", MarkdownMark.Bullet),
            (Symbol.TextNumberList, "scratchpad.markNumbered", MarkdownMark.Numbered),
            (Symbol.TextQuote, "scratchpad.markQuote", MarkdownMark.Quote),
            (Symbol.Code, "scratchpad.markCode", MarkdownMark.Code),
            (Symbol.Link, "scratchpad.markLink", MarkdownMark.Link),
        ];
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(6, 2) };
        foreach (var (icon, tip, mark) in marks)
        {
            var button = IconButton(icon, L.Get(tip), () => _service.ApplyMark(mark));
            button.Width = 26;
            button.Height = 26;
            row.Children.Add(button);
        }

        return new Border { Height = 30, IsVisible = false, Child = row };
    }

    private Control BuildFooter(out ToggleButton format, out ToggleButton preview, out Button copy, out Button save, out Button clear)
    {
        format = new ToggleButton
        {
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { new SymbolIcon { Symbol = Symbol.TextEditStyle, FontSize = 13 }, new TextBlock { Text = L.Get("scratchpad.formatMarks"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center } } },
            Padding = new Thickness(8, 3),
            CornerRadius = new CornerRadius(12),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var formatToggle = format;
        format.IsCheckedChanged += (_, _) =>
        {
            FormatExpanded = formatToggle.IsChecked == true;
            UpdateChrome();
        };
        AutomationProperties.SetName(format, L.Get("scratchpad.formatMarks"));

        preview = new ToggleButton
        {
            Content = new SymbolIcon { Symbol = Symbol.Eye, FontSize = 14 },
            Padding = new Thickness(6),
            Classes = { "icon" },
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var previewToggle = preview;
        preview.Click += (_, _) => SetPreview(previewToggle.IsChecked == true);
        ToolTip.SetTip(preview, L.Get("scratchpad.previewFormatting"));
        AutomationProperties.SetName(preview, L.Get("scratchpad.previewFormatting"));

        copy = new Button
        {
            Classes = { "icon" },
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { _copyIcon, _copyLabel } },
            VerticalAlignment = VerticalAlignment.Center,
        };
        copy.Click += (_, _) => _service.CopyAll();
        ToolTip.SetTip(copy, L.Get("scratchpad.copyAll"));
        AutomationProperties.SetName(copy, L.Get("scratchpad.copyAll"));

        save = IconButton(Symbol.Save, L.Get("scratchpad.exportAction"), () => _ = _service.ExportAsync());
        clear = IconButton(Symbol.Delete, L.Get("scratchpad.clearAction"), () => _service.ClearTab());

        return new Grid
        {
            Height = 36,
            Margin = new Thickness(8, 0),
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,Auto,*,Auto"),
            ColumnSpacing = 2,
            Children = { format, Column(preview, 1), Column(copy, 2), Column(save, 3), Column(clear, 5) },
        };
    }

    private Control BuildResizeZones()
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("12,*,12"),
            RowDefinitions = new RowDefinitions("12,*,12"),
            IsHitTestVisible = true,
        };

        void Zone(int row, int column, WindowEdge edge, StandardCursorType cursor, HorizontalAlignment h, VerticalAlignment v, double width, double height)
        {
            var zone = new Border
            {
                Background = Brushes.Transparent,
                Cursor = new Cursor(cursor),
                HorizontalAlignment = h,
                VerticalAlignment = v,
                Width = width,
                Height = height,
            };
            zone.PointerPressed += (_, e) =>
            {
                if (e.GetCurrentPoint(zone).Properties.IsLeftButtonPressed)
                {
                    BeginResizeDrag(edge, e);
                    e.Handled = true;
                }
            };
            Grid.SetRow(zone, row);
            Grid.SetColumn(zone, column);
            grid.Children.Add(zone);
        }

        var nan = double.NaN;
        Zone(0, 0, WindowEdge.NorthWest, StandardCursorType.TopLeftCorner, HorizontalAlignment.Stretch, VerticalAlignment.Stretch, nan, nan);
        Zone(0, 2, WindowEdge.NorthEast, StandardCursorType.TopRightCorner, HorizontalAlignment.Stretch, VerticalAlignment.Stretch, nan, nan);
        Zone(2, 0, WindowEdge.SouthWest, StandardCursorType.BottomLeftCorner, HorizontalAlignment.Stretch, VerticalAlignment.Stretch, nan, nan);
        Zone(2, 2, WindowEdge.SouthEast, StandardCursorType.BottomRightCorner, HorizontalAlignment.Stretch, VerticalAlignment.Stretch, nan, nan);
        Zone(0, 1, WindowEdge.North, StandardCursorType.TopSide, HorizontalAlignment.Stretch, VerticalAlignment.Top, nan, 6);
        Zone(2, 1, WindowEdge.South, StandardCursorType.BottomSide, HorizontalAlignment.Stretch, VerticalAlignment.Bottom, nan, 6);
        Zone(1, 0, WindowEdge.West, StandardCursorType.LeftSide, HorizontalAlignment.Left, VerticalAlignment.Stretch, 6, nan);
        Zone(1, 2, WindowEdge.East, StandardCursorType.RightSide, HorizontalAlignment.Right, VerticalAlignment.Stretch, 6, nan);

        // Only the zones themselves take input; the rest of the grid lets clicks through.
        grid.Background = null;
        return grid;
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (_service.DialogOpen)
        {
            return;
        }

        var named = e.Key switch
        {
            Key.Escape => ScratchpadNamedKey.Escape,
            Key.F3 => ScratchpadNamedKey.F3,
            Key.Up => ScratchpadNamedKey.Up,
            Key.Down => ScratchpadNamedKey.Down,
            _ => ScratchpadNamedKey.None,
        };
        char? character = e.KeySymbol is { Length: 1 } symbol ? symbol[0] : e.Key is >= Key.A and <= Key.Z ? (char)('a' + (e.Key - Key.A)) : null;
        var command = ScratchpadShortcuts.Resolve(character, named,
            e.KeyModifiers.HasFlag(KeyModifiers.Control), e.KeyModifiers.HasFlag(KeyModifiers.Alt), e.KeyModifiers.HasFlag(KeyModifiers.Shift));
        if (command is null)
        {
            return;
        }

        switch (command)
        {
            case ScratchpadCommand.Hide:
                if (FindBarFocused)
                {
                    CloseFind();
                }
                else
                {
                    _service.Hide();
                }

                break;
            case ScratchpadCommand.NewTab:
                _ = _service.NewTabAsync();
                break;
            case ScratchpadCommand.CloseTab:
                _ = _service.CloseTabAsync(null, hideWhenLast: true);
                break;
            case ScratchpadCommand.Find:
                OpenFind();
                break;
            case ScratchpadCommand.FindNext:
                FindStep(1);
                break;
            case ScratchpadCommand.FindPrevious:
                FindStep(-1);
                break;
            case ScratchpadCommand.MoveLinesUp or ScratchpadCommand.MoveLinesDown:
                if (!_editor.IsFocused || PreviewOn)
                {
                    return;
                }

                var moved = ScratchpadLineMover.Move(_editor.Text ?? string.Empty, Math.Min(_editor.SelectionStart, _editor.SelectionEnd), Math.Abs(_editor.SelectionEnd - _editor.SelectionStart), command == ScratchpadCommand.MoveLinesUp);
                if (moved is null)
                {
                    // At the first or last line the key keeps its default behaviour.
                    return;
                }

                ApplyEdit(moved.Value);
                break;
        }

        e.Handled = true;
    }

    private Button IconButton(Symbol symbol, string tip, Action onClick)
    {
        var button = new Button
        {
            Classes = { "icon" },
            Content = new SymbolIcon { Symbol = symbol, FontSize = 13 },
            Width = 26,
            Height = 26,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        button.Click += (_, _) => onClick();
        ToolTip.SetTip(button, tip);
        AutomationProperties.SetName(button, tip);
        return button;
    }

    private static Control Column(Control control, int column)
    {
        Grid.SetColumn(control, column);
        return control;
    }

    private static Control Row(Control control, int row)
    {
        Grid.SetRow(control, row);
        return control;
    }
}
