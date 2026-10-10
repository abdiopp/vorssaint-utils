// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Controls;
using Rivet.App.Features.Clipboard;
using Rivet.App.Hosting;
using Rivet.App.Modules;
using Rivet.Core.Launcher;
using Rivet.Core.Localization;
using Rivet.Core.Settings;
using Rivet.Core.Shortcuts;

namespace Rivet.App.Features.Launcher;

/// <summary>
/// The Command Bar window (spec 06 §3.8.2–§3.8.4): the field with its drag
/// mark, the category chips, the list (or a mode card) and the footer. It
/// renders <see cref="CommandBarSession"/> and forwards keys to the controller.
/// </summary>
public sealed class CommandBarWindow : FloatingPanel
{
    private const double BarWidth = 560;
    private const double ListMaxHeight = 452;
    private const int HomeLineCap = 140;
    private static readonly Dictionary<string, Bitmap?> IconCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, List<Image>> IconWaiters = new(StringComparer.OrdinalIgnoreCase);

    private readonly CommandBarController _controller;
    private readonly CommandBarPreferences _preferences;
    private readonly ICommandBarPlatform _platform;
    private readonly ISettingsStore _settings;
    private readonly TextBox _field = new();
    private readonly Border _capsule = new() { Classes = { "pill" }, IsVisible = false, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _capsuleText = new() { FontSize = 12, MaxWidth = 180, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Button _clear;
    private readonly TextBlock _compactHint = new() { Classes = { "caption", "tertiary" }, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _message = new() { Classes = { "caption" }, Margin = new Thickness(16, 0, 16, 6), TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly StackPanel _chips = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private readonly ScrollViewer _chipScroller;
    private readonly Border _divider = new() { Classes = { "separator" } };
    private readonly WrapPanel _try = new() { Margin = new Thickness(12, 8, 12, 2) };
    private readonly StackPanel _list = new() { Spacing = 1 };
    private readonly ScrollViewer _scroller;
    private readonly ContentControl _card = new() { Margin = new Thickness(12, 10), IsVisible = false };
    private readonly StackPanel _empty = new() { Spacing = 8, Margin = new Thickness(16, 18), HorizontalAlignment = HorizontalAlignment.Center, IsVisible = false };
    private readonly Grid _footer = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(14, 6, 14, 8) };
    private readonly TextBlock _footerShortcut = new() { Classes = { "caption", "tertiary" }, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _footerHints = new() { Classes = { "caption", "tertiary" }, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
    private readonly List<(CommandRow Row, Button Button)> _rowButtons = [];
    private readonly List<Button> _actionButtons = [];
    private bool _syncingField;
    private bool _ctrlHeld;
    private Point? _lastPointer;

    public CommandBarWindow(CommandBarController controller)
    {
        _controller = controller;
        var services = controller.Services;
        _preferences = services.GetRequiredService<CommandBarPreferences>();
        _platform = services.GetRequiredService<ICommandBarPlatform>();
        _settings = services.GetRequiredService<ISettingsStore>();
        Title = L.Get("commandBar.pageTitle");
        Width = BarWidth + 20;
        SizeToContent = SizeToContent.Height;
        Surface.CornerRadius = new CornerRadius(14);

        var mark = new Border
        {
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(8),
            Classes = { "iconTile" },
            Cursor = new Cursor(StandardCursorType.SizeAll),
            VerticalAlignment = VerticalAlignment.Center,
            Child = ClipboardUi.Icon("Search", 16, "AccentBrush"),
        };
        ToolTip.SetTip(mark, L.Get("commandBar.dragHint"));
        mark.PointerPressed += OnMarkPressed;

        _capsule.Child = _capsuleText;
        _field.FontSize = 16;
        _field.BorderThickness = new Thickness(0);
        _field.Background = Brushes.Transparent;
        _field.VerticalContentAlignment = VerticalAlignment.Center;
        _field.MinHeight = 36;
        AutomationProperties.SetName(_field, L.Get("commandBar.searchPlaceholder"));
        _field.TextChanged += (_, _) =>
        {
            if (!_syncingField)
            {
                _controller.SetQuery(_field.Text ?? string.Empty);
            }
        };
        _clear = ClipboardUi.IconButton("Dismiss", L.Get("Strings.urlCleanerClearButton"), () => _controller.SetQuery(string.Empty), 12);

        var fieldRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto,Auto"), ColumnSpacing = 8, Margin = new Thickness(10, 8, 10, 8) };
        fieldRow.Children.Add(mark);
        Grid.SetColumn(_capsule, 1);
        fieldRow.Children.Add(_capsule);
        Grid.SetColumn(_field, 2);
        fieldRow.Children.Add(_field);
        Grid.SetColumn(_clear, 3);
        fieldRow.Children.Add(_clear);
        Grid.SetColumn(_compactHint, 4);
        fieldRow.Children.Add(_compactHint);

        _chipScroller = new ScrollViewer
        {
            Content = _chips,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(12, 0, 12, 8),
        };
        _scroller = new ScrollViewer
        {
            Content = _list,
            MaxHeight = ListMaxHeight,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(6, 4),
        };
        _footer.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { ClipboardUi.Icon("Keyboard", 12, "TextTertiaryBrush"), _footerShortcut } });
        Grid.SetColumn(_footerHints, 2);
        _footer.Children.Add(_footerHints);

        var seeSuggestions = new Button { Classes = { "link" }, Content = L.Get("commandBar.noResultsAction"), HorizontalAlignment = HorizontalAlignment.Center };
        seeSuggestions.Click += (_, _) =>
        {
            _controller.Session.ShowSuggestions();
            _controller.Refresh();
        };
        _empty.Children.Add(new Border { HorizontalAlignment = HorizontalAlignment.Center, Opacity = 0.35, Child = ClipboardUi.Icon("Search", 28, "TextTertiaryBrush") });
        _empty.Children.Add(new TextBlock { Text = L.Get("commandBar.noResultsTitle"), Classes = { "caption" }, HorizontalAlignment = HorizontalAlignment.Center });
        _empty.Children.Add(seeSuggestions);

        Surface.Child = new StackPanel
        {
            Children =
            {
                fieldRow,
                _message,
                _chipScroller,
                _divider,
                _card,
                _try,
                _scroller,
                _empty,
                _footer,
            },
        };

        PointerMoved += (_, e) => _lastPointer ??= e.GetPosition(this);
        KeyUp += (_, e) =>
        {
            if (e.Key is Key.LeftCtrl or Key.RightCtrl && _ctrlHeld)
            {
                _ctrlHeld = false;
                Render();
            }
        };
        controller.Changed += (_, _) => Render();
    }

    protected override void OnPresented() => _field.Focus();

    protected override void OnDismissed(FloatingCloseReason reason)
    {
        _ctrlHeld = false;
        _controller.OnWindowDismissed(reason);
    }

    // ── Placement ──────────────────────────────────────────────────────

    /// <summary>The top edge sits 28 % below the top of the work area, plus the user's drag offset.</summary>
    protected override void Place()
    {
        var (area, scale) = PointerScreen();
        var width = (int)Math.Ceiling(Bounds.Width * scale);
        var height = (int)Math.Ceiling(Math.Max(Bounds.Height, 60) * scale);
        if (width <= 0)
        {
            return;
        }

        var (dx, dy) = _preferences.PositionOffset;
        var x = area.X + ((area.Width - width) / 2) + (int)(dx * scale);
        var y = area.Y + (int)(area.Height * 0.28) - (int)(dy * scale);
        Position = Clamp(new PixelPoint(x, y), width, height, area, scale);
    }

    private void OnMarkPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (e.ClickCount >= 2)
        {
            _preferences.PositionOffset = (0, 0);
            Place();
            return;
        }

        var before = Position;
        BeginMoveDrag(e);
        if (Position == before)
        {
            return;
        }

        // Save the offset from the default spot (DIPs; positive dy = moved up).
        var (area, scale) = PointerScreen();
        var width = (int)Math.Ceiling(Bounds.Width * scale);
        var defaultX = area.X + ((area.Width - width) / 2);
        var defaultY = area.Y + (int)(area.Height * 0.28);
        _preferences.PositionOffset = ((int)Math.Round((Position.X - defaultX) / scale), (int)Math.Round((defaultY - Position.Y) / scale));
        _field.Focus();
    }

    // ── Rendering ──────────────────────────────────────────────────────

    private IBrush Brush(string key, IBrush fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : fallback;

    public void Render()
    {
        var session = _controller.Session;
        _syncingField = true;
        try
        {
            if ((_field.Text ?? string.Empty) != session.Query)
            {
                _field.Text = session.Query;
                _field.CaretIndex = session.Query.Length;
            }

            _field.PlaceholderText = session.Placeholder;
            _field.IsReadOnly = session.Mode == CommandBarMode.CapturingShortcut;
        }
        finally
        {
            _syncingField = false;
        }

        var row = session.ModeRow;
        _capsule.IsVisible = session.Mode is CommandBarMode.Argument or CommandBarMode.Naming or CommandBarMode.CapturingShortcut && row is not null;
        _capsuleText.Text = row?.Title;
        _clear.IsVisible = session.Query.Length > 0 && session.Mode is CommandBarMode.Search or CommandBarMode.Naming or CommandBarMode.Argument;
        var compactHome = session.Compact && session.IsHome && !session.Peeked;
        _compactHint.IsVisible = compactHome;
        _compactHint.Text = $"↓ {L.Get("commandBar.suggestionsLabel")}   Esc";

        _message.IsVisible = !string.IsNullOrEmpty(session.Message);
        _message.Text = session.Message;
        _message.Foreground = Brush("WarningBrush", Brushes.Orange);

        RenderChips(session);
        RenderTry(session);
        RenderCard(session);
        RenderList(session);
        _divider.IsVisible = !compactHome;
        _footer.IsVisible = !compactHome;
        RenderFooter(session);
        Dispatcher.UIThread.Post(Place, DispatcherPriority.Background);
    }

    private void RenderChips(CommandBarSession session)
    {
        _chipScroller.IsVisible = session.ShowsChips;
        _chips.Children.Clear();
        if (!session.ShowsChips)
        {
            return;
        }

        foreach (var category in session.Chips)
        {
            var selected = session.Category == category;
            var text = category is { } c ? CommandBarEngine.CategoryTitle(c) : L.Get("commandBar.categoryAll");
            var chip = new Button
            {
                Content = new TextBlock { Text = text, FontSize = 12 },
                Padding = new Thickness(10, 3),
                CornerRadius = new CornerRadius(11),
                MinHeight = 0,
                Background = selected ? Brush("AccentBrush", Brushes.DodgerBlue) : Brush("PanelCardBrush", Brushes.Transparent),
                Foreground = selected ? Brushes.White : Brush("TextPrimaryBrush", Brushes.Black),
                BorderThickness = new Thickness(0),
            };
            AutomationProperties.SetName(chip, text);
            var target = category;
            chip.Click += (_, _) =>
            {
                session.SelectCategory(target);
                _controller.Refresh();
                _field.Focus();
            };
            _chips.Children.Add(chip);
        }
    }

    private void RenderTry(CommandBarSession session)
    {
        var show = session.IsHome && !session.Compact && session.Mode == CommandBarMode.Search;
        _try.IsVisible = show;
        if (!show || _try.Children.Count > 0)
        {
            return;
        }

        _try.Children.Add(new TextBlock { Text = L.Get("commandBar.tryTheseLabel"), Classes = { "caption", "tertiary" }, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 6, 0) });
        var examples = new List<string> { "100 km to mi", "2+2*3" };
        if (_platform.ReadAnswers().BatteryPercent is not null)
        {
            examples.Add(L.Get("commandBar.answerBatteryLabel").ToLower(Localizer.Current.Culture));
        }

        examples.Add("fire");
        foreach (var example in examples)
        {
            var text = example;
            var chip = new Button
            {
                Content = new TextBlock { Text = text, Classes = { "mono" }, FontSize = 11 },
                Padding = new Thickness(8, 2),
                Margin = new Thickness(0, 0, 6, 4),
                CornerRadius = new CornerRadius(9),
                MinHeight = 0,
                BorderThickness = new Thickness(0),
            };
            chip.Click += (_, _) =>
            {
                _controller.SetQuery(text);
                _field.Focus();
            };
            _try.Children.Add(chip);
        }
    }

    private void RenderCard(CommandBarSession session)
    {
        var row = session.ModeRow;
        Control? card = session.Mode switch
        {
            CommandBarMode.Argument when row is not null => ModeCard(row, L.Get("commandBar.argumentHint"), null),
            CommandBarMode.Confirm when row is not null => ConfirmCard(session),
            CommandBarMode.Naming when row is not null => ModeCard(row, L.Get("commandBar.aliasPlaceholder"), null),
            CommandBarMode.CapturingShortcut when row is not null => ModeCard(row, L.Get("commandBar.shortcutCaptureHint"),
                _preferences.RowShortcuts.TryGetValue(row.StableKey, out var chord) ? _controller.ChordText(chord) : null),
            _ => null,
        };
        _card.Content = card;
        _card.IsVisible = card is not null;
    }

    private Control ModeCard(CommandRow row, string hint, string? binding)
    {
        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { RowIcon(row), new TextBlock { Text = row.Title, FontSize = 14, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center } } };
        var stack = new StackPanel { Spacing = 6, Children = { top } };
        if (binding is not null)
        {
            stack.Children.Add(new Border { Classes = { "pill" }, HorizontalAlignment = HorizontalAlignment.Left, Child = new TextBlock { Text = binding, FontSize = 12 } });
        }

        stack.Children.Add(new TextBlock { Text = hint, Classes = { "caption" }, TextWrapping = TextWrapping.Wrap });
        return new Border { Classes = { "card" }, Padding = new Thickness(12, 10), Child = stack };
    }

    private Control ConfirmCard(CommandBarSession session)
    {
        var cancel = new Button { Content = L.Get("clipboard.cancel") };
        cancel.Click += (_, _) =>
        {
            session.Escape();
            _controller.Refresh();
            _field.Focus();
        };
        var confirm = new Button { Content = L.Get("commandBar.confirmButton"), Classes = { "accent" } };
        confirm.Click += (_, _) => _controller.Submit();
        var danger = Brush("DangerBrush", Brushes.IndianRed);
        var tint = danger is ISolidColorBrush solid ? new SolidColorBrush(solid.Color, 0.12) : (IBrush)Brushes.Transparent;
        return new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = tint,
            BorderBrush = danger,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 10),
            Child = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = session.ConfirmPrompt, FontSize = 14, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = L.Get("commandBar.confirmHint"), Classes = { "caption" } },
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, confirm } },
                },
            },
        };
    }

    private void RenderList(CommandBarSession session)
    {
        _list.Children.Clear();
        _rowButtons.Clear();
        _actionButtons.Clear();
        if (session.Mode == CommandBarMode.Actions)
        {
            _scroller.IsVisible = true;
            _empty.IsVisible = false;
            _list.Children.Add(Heading(L.Get("commandBar.actionsTitle")));
            for (var i = 0; i < session.Actions.Count; i++)
            {
                _list.Children.Add(ActionLine(session.Actions[i], i, i == session.Selected));
            }

            return;
        }

        var listing = session.Mode == CommandBarMode.Search && session.ShowsList;
        _empty.IsVisible = listing && session.IsEmptyResult;
        _scroller.IsVisible = listing && !session.IsEmptyResult && session.Items.Count > 0;
        if (!listing)
        {
            return;
        }

        var query = session.Category is null ? session.Query : session.Query;
        var rowNumber = 0;
        var lines = 0;
        for (var i = 0; i < session.Items.Count && lines < HomeLineCap; i++, lines++)
        {
            var item = session.Items[i];
            if (item.Header is { } header)
            {
                _list.Children.Add(Heading(header));
                continue;
            }

            if (item.Row is { } row)
            {
                var button = RowLine(row, query, i == session.Selected, rowNumber);
                _rowButtons.Add((row, button));
                _list.Children.Add(button);
                rowNumber++;
            }
        }

        if (session.Selected >= 0)
        {
            var selected = _rowButtons.FirstOrDefault(r => ReferenceEquals(r.Row, session.SelectedRow)).Button;
            selected?.BringIntoView();
        }
    }

    private static TextBlock Heading(string text) => new()
    {
        Text = text.ToUpper(Localizer.Current.Culture),
        Classes = { "sectionTitle" },
        FontSize = 10,
        Margin = new Thickness(10, 8, 10, 3),
    };

    private Control RowIcon(CommandRow row)
    {
        Control icon;
        if (row.Glyph is { Length: > 0 } glyph)
        {
            icon = new Border
            {
                Width = 28,
                Height = 28,
                Child = new TextBlock { Text = glyph, FontSize = 21, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
        }
        else if (row.Swatch is { } argb)
        {
            icon = new Border
            {
                Width = 28,
                Height = 28,
                CornerRadius = new CornerRadius(7),
                Background = new SolidColorBrush(Color.FromUInt32(argb)),
                BorderBrush = Brush("PanelBorderBrush", Brushes.Gray),
                BorderThickness = new Thickness(1),
            };
        }
        else if (row.ImagePath is { } imagePath)
        {
            var image = new Image { Width = 28, Height = 28, Stretch = Stretch.UniformToFill };
            ClipboardUi.LoadThumbnail(image, imagePath, 64);
            icon = new Border { CornerRadius = new CornerRadius(5), ClipToBounds = true, Child = image };
        }
        else if (row.IconPath is { Length: > 0 } iconPath)
        {
            var image = new Image { Width = 28, Height = 28 };
            var plate = Plate(row);
            var grid = new Grid { Width = 28, Height = 28, Children = { plate, image } };
            LoadShellIcon(image, plate, iconPath);
            icon = grid;
        }
        else
        {
            icon = Plate(row);
        }

        if (!row.IsLive)
        {
            return icon;
        }

        var dot = new Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = Brush("SuccessBrush", Brushes.LimeGreen),
            Stroke = Brush("PanelBackgroundBrush", Brushes.White),
            StrokeThickness = 1.5,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -2, -2, 0),
        };
        return new Grid { Width = 28, Height = 28, Children = { icon, dot } };
    }

    private Border Plate(CommandRow row) => new()
    {
        Width = 28,
        Height = 28,
        CornerRadius = new CornerRadius(7),
        Classes = { "iconTile" },
        Child = ClipboardUi.Icon(row.Icon ?? "Apps", 15, row.SetupHint is null ? "AccentBrush" : "WarningBrush"),
    };

    /// <summary>App and file icons load once per path, off the UI thread (Windows only; the glyph stays otherwise).</summary>
    private void LoadShellIcon(Image image, Border plate, string path)
    {
        if (IconCache.TryGetValue(path, out var cached))
        {
            if (cached is not null)
            {
                image.Source = cached;
                plate.IsVisible = false;
            }

            return;
        }

        if (IconWaiters.TryGetValue(path, out var waiting))
        {
            waiting.Add(image);
            image.Tag = plate;
            return;
        }

        IconWaiters[path] = [image];
        image.Tag = plate;
        var scale = RenderScaling;
        _ = Task.Run(async () =>
        {
            Rivet.Core.Platform.PixelBuffer? pixels = null;
            try
            {
                pixels = await _platform.LoadIconAsync(path, (int)Math.Ceiling(28 * scale), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Rivet.Core.Diagnostics.Log.Info("commandBar", $"Icon load failed ({ex.GetType().Name}).");
            }

            Dispatcher.UIThread.Post(() =>
            {
                Bitmap? bitmap = pixels is null ? null : ImageInterop.ToBitmap(pixels);
                IconCache[path] = bitmap;
                if (IconWaiters.Remove(path, out var images) && bitmap is not null)
                {
                    foreach (var waiter in images)
                    {
                        waiter.Source = bitmap;
                        if (waiter.Tag is Border waiterPlate)
                        {
                            waiterPlate.IsVisible = false;
                        }
                    }
                }
            });
        });
    }

    private Button RowLine(CommandRow row, string query, bool selected, int number)
    {
        var answer = row.Source == CommandSource.Calculator || row.Id.StartsWith("scriptAnswer.", StringComparison.Ordinal);
        var title = new TextBlock
        {
            FontSize = answer ? 17 : 13.5,
            FontWeight = answer ? FontWeight.SemiBold : FontWeight.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = answer ? 3 : 1,
            TextWrapping = answer ? TextWrapping.Wrap : TextWrapping.NoWrap,
        };
        FillTitle(title, row.Title, answer ? string.Empty : query);
        var text = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center, Children = { title } };
        var subtitleText = row.SetupHint ?? row.Subtitle;
        if (!string.IsNullOrEmpty(subtitleText))
        {
            var subtitle = new TextBlock { Text = subtitleText, Classes = { "caption" }, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
            if (row.SetupHint is not null)
            {
                subtitle.Foreground = Brush("WarningBrush", Brushes.Orange);
            }

            text.Children.Add(subtitle);
        }

        Control? trailing = null;
        if (_ctrlHeld && number < 9)
        {
            trailing = new TextBlock { Text = $"Ctrl+{number + 1}", Classes = { "caption", "tertiary" }, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        }
        else if (row.Value is { } value)
        {
            trailing = new Border { Classes = { "pill" }, VerticalAlignment = VerticalAlignment.Center, Child = new TextBlock { Text = value, FontSize = 12, FontWeight = FontWeight.SemiBold } };
        }
        else if ((_preferences.RowShortcuts.TryGetValue(row.StableKey, out var chord) ? _controller.ChordText(chord) : row.ShortcutText) is { } shortcut)
        {
            trailing = new TextBlock { Text = shortcut, Classes = { "caption", "tertiary" }, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        }

        var enter = new TextBlock { Text = "↩", Classes = { "tertiary" }, FontSize = 14, Width = 16, VerticalAlignment = VerticalAlignment.Center, Opacity = selected ? 1 : 0 };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), ColumnSpacing = 10 };
        grid.Children.Add(RowIcon(row));
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        if (trailing is not null)
        {
            Grid.SetColumn(trailing, 2);
            grid.Children.Add(trailing);
        }

        Grid.SetColumn(enter, 3);
        grid.Children.Add(enter);
        var button = new Button
        {
            Classes = { "row" },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(8, 5),
            Content = grid,
            Background = selected ? Brush("AccentFaintBrush", Brushes.LightBlue) : Brushes.Transparent,
            Focusable = false,
        };
        AutomationProperties.SetName(button, row.Title);
        if (!string.IsNullOrEmpty(subtitleText))
        {
            AutomationProperties.SetHelpText(button, subtitleText);
        }

        button.Click += (_, _) => _controller.RunClicked(row);
        button.PointerMoved += (_, e) =>
        {
            // Hover selects only after the pointer really moved (a row sliding under a resting pointer does not).
            var position = e.GetPosition(this);
            if (_lastPointer is { } last && last == position)
            {
                return;
            }

            _lastPointer = position;
            if (!ReferenceEquals(_controller.Session.SelectedRow, row) && _controller.Session.Select(row))
            {
                UpdateRowSelection();
            }
        };
        return button;
    }

    /// <summary>Repaints only the selection (hover), without rebuilding the list.</summary>
    private void UpdateRowSelection()
    {
        var selectedRow = _controller.Session.SelectedRow;
        foreach (var (row, button) in _rowButtons)
        {
            var selected = ReferenceEquals(row, selectedRow);
            button.Background = selected ? Brush("AccentFaintBrush", Brushes.LightBlue) : Brushes.Transparent;
            if (button.Content is Grid grid && grid.Children.OfType<TextBlock>().LastOrDefault(t => t.Text == "↩") is { } enter)
            {
                enter.Opacity = selected ? 1 : 0;
            }
        }

        RenderFooter(_controller.Session);
    }

    private void FillTitle(TextBlock block, string title, string query)
    {
        block.Inlines?.Clear();
        var ranges = query.Trim().Length == 0 ? [] : CommandBarSearch.Highlights(title, query);
        if (ranges.Count == 0)
        {
            block.Text = title;
            return;
        }

        var accent = Brush("AccentBrush", Brushes.DodgerBlue);
        var inlines = new InlineCollection();
        var position = 0;
        foreach (var (start, length) in ranges)
        {
            if (start > position)
            {
                inlines.Add(new Run(title[position..start]));
            }

            inlines.Add(new Run(title.Substring(start, length)) { Foreground = accent, FontWeight = FontWeight.Bold });
            position = start + length;
        }

        if (position < title.Length)
        {
            inlines.Add(new Run(title[position..]));
        }

        block.Inlines = inlines;
    }

    private Button ActionLine(CommandRowAction action, int index, bool selected)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 10 };
        grid.Children.Add(new Border
        {
            Width = 26,
            Height = 26,
            CornerRadius = new CornerRadius(7),
            Classes = { "iconTile" },
            Child = ClipboardUi.Icon(action.Icon, 14, action.Destructive ? "DangerBrush" : "AccentBrush"),
        });
        var title = new TextBlock { Text = action.Title, FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(title, 1);
        grid.Children.Add(title);
        var button = new Button
        {
            Classes = { "row" },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(8, 5),
            Content = grid,
            Background = selected ? Brush("AccentFaintBrush", Brushes.LightBlue) : Brushes.Transparent,
            Focusable = false,
        };
        AutomationProperties.SetName(button, action.Title);
        button.Click += (_, _) =>
        {
            _controller.Session.SelectAction(index);
            _controller.Submit();
        };
        _actionButtons.Add(button);
        return button;
    }

    private void RenderFooter(CommandBarSession session)
    {
        _footerShortcut.Text = _controller.OwnShortcutText ?? L.Get("commandBar.pageTitle");
        var hints = new List<string>();
        if (session.Mode == CommandBarMode.Search && session.SelectedRow is { } row && row.Source is not (CommandSource.Answers or CommandSource.Calculator))
        {
            hints.Add($"Ctrl+K {L.Get("commandBar.actionsHint")}");
        }

        hints.Add("↑↓");
        if (session.ShowsChips && session.Query.Length == 0)
        {
            hints.Add("←→");
        }

        if (session.SelectedRow?.Id == "math.result")
        {
            hints.Add($"Tab {L.Get("commandBar.reuseHint")}");
        }

        hints.Add("↩");
        hints.Add("Esc");
        _footerHints.Text = string.Join("   ", hints);
    }

    // ── Keys ───────────────────────────────────────────────────────────

    protected override void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.ImeProcessed)
        {
            return; // IME composition always wins.
        }

        var session = _controller.Session;
        var ctrl = e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control);
        var alt = e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Alt);
        if (e.Key is Key.LeftCtrl or Key.RightCtrl)
        {
            if (!_ctrlHeld)
            {
                _ctrlHeld = true;
                Render();
            }

            return;
        }

        if (session.Mode == CommandBarMode.CapturingShortcut)
        {
            HandleCapture(e);
            return;
        }

        switch (e.Key)
        {
            case Key.Escape:
                if (session.Escape() == EscapeOutcome.Close)
                {
                    _controller.Close(FloatingCloseReason.Escape);
                }
                else
                {
                    _controller.Refresh();
                }

                break;
            case Key.Enter:
                _controller.Submit(reveal: ctrl);
                break;
            case Key.Up:
            case Key.P when ctrl && !alt:
                session.Move(-1);
                Render();
                break;
            case Key.Down:
            case Key.N when ctrl && !alt:
                session.Move(1);
                Render();
                break;
            case Key.Left when session.Query.Length == 0 && session.WalkCategory(-1):
            case Key.Right when session.Query.Length == 0 && session.WalkCategory(1):
                Render();
                break;
            case Key.Tab:
                if (session.Complete())
                {
                    _controller.Refresh();
                }

                break;
            case Key.K when ctrl:
                _controller.OpenActions();
                break;
            case Key.P when alt:
                _controller.TogglePinSelected();
                break;
            case Key.OemComma when ctrl:
                _controller.Close(FloatingCloseReason.Action);
                _controller.Services.GetService<IAppShell>()?.OpenSettings(CommandBarModule.PageId);
                break;
            case >= Key.D1 and <= Key.D9 when ctrl:
                _controller.RunNth(e.Key - Key.D1);
                break;
            case >= Key.NumPad1 and <= Key.NumPad9 when ctrl:
                _controller.RunNth(e.Key - Key.NumPad1);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void HandleCapture(KeyEventArgs e)
    {
        e.Handled = true;
        switch (e.Key)
        {
            case Key.Escape:
                _controller.Session.Escape();
                _controller.Refresh();
                return;
            case Key.Delete or Key.Back when e.KeyModifiers == Avalonia.Input.KeyModifiers.None:
                _controller.CaptureShortcut(default, clear: true);
                return;
            case Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.System:
                return;
        }

        var modifiers = Core.Shortcuts.KeyModifiers.None;
        if (e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control))
        {
            modifiers |= Core.Shortcuts.KeyModifiers.Control;
        }

        if (e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Alt))
        {
            modifiers |= Core.Shortcuts.KeyModifiers.Alt;
        }

        if (e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Shift))
        {
            modifiers |= Core.Shortcuts.KeyModifiers.Shift;
        }

        if (e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Meta))
        {
            modifiers |= Core.Shortcuts.KeyModifiers.Win;
        }

        var virtualKey = AvaloniaKeyMap.ToVirtualKey(e.Key);
        if (virtualKey == 0)
        {
            return;
        }

        _controller.CaptureShortcut(new KeyChord(modifiers, virtualKey), clear: false);
    }
}
