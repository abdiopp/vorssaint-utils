// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Rivet.Core.Agents;
using Rivet.Core.Localization;
using Rivet.Core.Settings;

namespace Rivet.App.Features.Agents;

/// <summary>
/// The "AI Agents" tab of the tray panel (the macOS app shows these cards in
/// its Dynamic Island): limits, spending, work in progress, trend, models,
/// projects and the 13-week activity, in the order and visibility chosen in
/// Settings. Redraws every 15 s, every second while a task runs.
/// </summary>
public sealed class AgentUsageSection : UserControl
{
    private readonly AgentUsageService? _service;
    private readonly ISettingsStore _settings;
    private readonly DispatcherTimer _timer = new();
    private readonly List<IDisposable> _observers = [];
    private readonly Func<AgentUsageSnapshot>? _snapshotOverride;

    public AgentUsageSection(IServiceProvider services, Func<AgentUsageSnapshot>? snapshot = null)
    {
        _service = services.GetService<AgentUsageService>();
        _settings = services.GetRequiredService<ISettingsStore>();
        _snapshotOverride = snapshot;
        _timer.Tick += (_, _) => Rebuild();
        Rebuild();
    }

    private AgentUsageSnapshot Current => _snapshotOverride?.Invoke() ?? _service?.Snapshot ?? AgentUsageSnapshot.Loading;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_service is not null)
        {
            _service.SnapshotChanged += OnSnapshotChanged;
        }

        _observers.Add(_settings.Observe(() => Dispatcher.UIThread.Post(Rebuild),
            AgentUsageSettings.Period, AgentUsageSettings.LimitDisplay, AgentUsageSettings.CardOrder, AgentUsageSettings.HiddenCards));
        Rebuild();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_service is not null)
        {
            _service.SnapshotChanged -= OnSnapshotChanged;
        }

        foreach (var observer in _observers)
        {
            observer.Dispose();
        }

        _observers.Clear();
        _timer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnSnapshotChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(Rebuild);

    private void Rebuild()
    {
        var snapshot = Current;
        Content = AgentUsageCards.Build(snapshot, _settings, DateTimeOffset.UtcNow);
        _timer.Interval = snapshot.Live.Count > 0 ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(15);
        if (VisualRoot is not null)
        {
            _timer.Start();
        }
    }
}

/// <summary>Builds the AI agents cards from a snapshot (shared by the panel tab and snapshot tests).</summary>
public static class AgentUsageCards
{
    public static Control Build(AgentUsageSnapshot snapshot, ISettingsStore settings, DateTimeOffset now)
    {
        var root = new StackPanel { Spacing = 10 };
        if (!snapshot.Loaded)
        {
            root.Children.Add(Caption(L.Get("notchAgents.loading")));
            return root;
        }

        if (!snapshot.AnySeen)
        {
            root.Children.Add(Card(Caption(L.Get("notchAgents.empty"))));
            return root;
        }

        var hidden = AgentUsageSettings.ParseHidden(settings.Get(AgentUsageSettings.HiddenCards));
        var order = AgentUsageSettings.OrderedCards(settings.Get(AgentUsageSettings.CardOrder)).Where(c => !hidden.Contains(c)).ToList();
        if (order.Count == 0)
        {
            root.Children.Add(Caption(L.Get("notchAgents.noCards")));
            return root;
        }

        var period = settings.Get(AgentUsageSettings.Period) switch
        {
            "week" => AgentPeriod.Week,
            "month" => AgentPeriod.Month,
            _ => AgentPeriod.Today,
        };
        var showUsed = settings.Get(AgentUsageSettings.LimitDisplay) == "used";
        foreach (var card in order)
        {
            switch (card)
            {
                case "limits":
                    foreach (var provider in snapshot.Providers.Where(p => p.Enabled && p.Seen && p.Provider != AgentProvider.Copilot))
                    {
                        root.Children.Add(LimitsCard(snapshot, provider, showUsed, now));
                    }

                    break;
                case "spend":
                    root.Children.Add(SpendCard(snapshot, period, settings));
                    break;
                case "live":
                    root.Children.Add(LiveCard(snapshot, now));
                    break;
                case "trend":
                    root.Children.Add(TrendCard(snapshot, period));
                    break;
                case "models":
                    root.Children.Add(TopCard(L.Get("notchAgents.modelsCard"), snapshot.For(period)?.Models ?? [], snapshot.For(period)?.Totals.AllPriced ?? true));
                    break;
                case "projects":
                    root.Children.Add(TopCard(L.Get("notchAgents.projectsCard"), snapshot.For(period)?.Projects ?? [], snapshot.For(period)?.Totals.AllPriced ?? true));
                    break;
                case "activity":
                    root.Children.Add(ActivityCard(snapshot));
                    break;
            }
        }

        return root;
    }

    // ── Limits ──────────────────────────────────────────────────────────
    private static Control LimitsCard(AgentUsageSnapshot snapshot, AgentProviderStatus status, bool showUsed, DateTimeOffset now)
    {
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(Header(status.Provider, status.Plan?.Name, status.Working));
        var windows = status.Windows.Where(w => w.ResetsAt is null || w.ResetsAt > now).ToList();
        if (windows.Count > 0)
        {
            var session = windows.FirstOrDefault(w => w.Kind == LimitWindowKind.Session);
            var other = windows.Where(w => w != session).OrderByDescending(w => w.UsedPercent).FirstOrDefault();
            foreach (var window in new[] { session, other }.OfType<LimitWindow>())
            {
                body.Children.Add(LimitRow(status.Provider, window, showUsed, now));
            }

            if (windows.Count == 1 && status.LimitsObservedAt is { } observed && now - observed > TimeSpan.FromMinutes(10))
            {
                body.Children.Add(Caption(L.Format("notchAgents.updatedFormat", Ago(now - observed))));
            }
        }
        else if (status.Provider == AgentProvider.Claude)
        {
            if (status.Estimate is { } estimate)
            {
                var elapsed = Math.Clamp((now - estimate.Start) / ClaudeLimits.SessionLength, 0, 1);
                var row = Row(L.Get("notchAgents.session"), AgentFormat.Countdown(estimate.End - now), (estimate.PartlyUnpriced ? "≥ " : string.Empty) + AgentFormat.Cost(estimate.Cost));
                var meter = Meter(elapsed, null, Tint(status.Provider), dimmed: true);
                var stack = new StackPanel { Spacing = 4, Children = { row, meter } };
                ToolTip.SetTip(stack, L.Get("notchAgents.estimated"));
                body.Children.Add(stack);
            }
            else
            {
                body.Children.Add(Caption(L.Get("notchAgents.noSession")));
            }
        }
        else if (status.Provider == AgentProvider.OpenCode)
        {
            var today = snapshot.Today?.ByProvider.GetValueOrDefault(AgentProvider.OpenCode) ?? UsageTotals.Empty;
            body.Children.Add(Row(L.Get("notchAgents.today"), L.Format("notchAgents.tokensFormat", AgentFormat.Tokens(today.Tokens)),
                (today.AllPriced ? string.Empty : "≥ ") + AgentFormat.Cost(today.Cost)));
            var details = L.Format("notchAgents.cachedFormat", AgentFormat.Percent(today.CacheRate * 100));
            if (status.LastActivity is { } last)
            {
                details += " · " + L.Format("win.agentUsage.lastUsedFormat", Ago(now - last));
            }

            body.Children.Add(Caption(details));
        }
        else
        {
            body.Children.Add(Caption(L.Get("notchAgents.waitingForLimits")));
        }

        var card = Card(body);
        if (status.LimitsSource == LimitSource.ClaudeApp && status.LimitsObservedAt is { } at && now - at >= ClaudeLimits.Freshness)
        {
            card.Opacity = 0.6;
        }

        return card;
    }

    private static Control LimitRow(AgentProvider provider, LimitWindow window, bool showUsed, DateTimeOffset now)
    {
        var used = window.UsedPercent;
        var percent = showUsed
            ? L.Format("notchAgents.usedFormat", AgentFormat.Percent(used))
            : L.Format("notchAgents.leftFormat", AgentFormat.Percent(100 - used));
        var countdown = window.ResetsAt is { } reset ? AgentFormat.Countdown(reset - now) : string.Empty;
        var tint = used >= 95 ? Color.FromRgb(0xE5, 0x48, 0x3E) : used >= 80 ? Color.FromRgb(0xF7, 0x63, 0x0C) : Tint(provider);
        var fraction = (showUsed ? used : 100 - used) / 100;
        return new StackPanel
        {
            Spacing = 4,
            Children =
            {
                Row(AgentUsageService.WindowLabel(window), countdown, percent),
                Meter(fraction, window.Pace(now) is { } pace ? (showUsed ? pace : 1 - pace) : null, tint, dimmed: false),
            },
        };
    }

    // ── Spending ────────────────────────────────────────────────────────
    private static Control SpendCard(AgentUsageSnapshot snapshot, AgentPeriod period, ISettingsStore settings)
    {
        var summary = snapshot.For(period);
        var totals = summary?.Totals ?? UsageTotals.Empty;
        var body = new StackPanel { Spacing = 6 };
        var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var (value, key) in new[] { ("today", "notchAgents.today"), ("week", "notchAgents.week"), ("month", "notchAgents.month") })
        {
            var selected = settings.Get(AgentUsageSettings.Period) == value;
            var chip = new Button
            {
                Content = new TextBlock { Text = L.Get(key), FontSize = 11 },
                Padding = new Thickness(8, 2),
                MinHeight = 0,
                Classes = { selected ? "accent" : "chip" },
            };
            chip.Click += (_, _) => settings.Set(AgentUsageSettings.Period, value);
            chips.Children.Add(chip);
        }

        var title = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        title.Children.Add(new TextBlock { Text = L.Get("notchAgents.spendCard").ToUpperInvariant(), Classes = { "sectionTitle" }, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(chips, 1);
        title.Children.Add(chips);
        body.Children.Add(title);

        var amount = new TextBlock { Text = (totals.AllPriced ? string.Empty : "≥ ") + AgentFormat.Cost(totals.Cost), FontSize = 24, FontWeight = FontWeight.SemiBold };
        var label = Caption(L.Get("notchAgents.apiValue"));
        ToolTip.SetTip(label, L.Get("notchAgents.valueNote") + (totals.AllPriced ? string.Empty : "\n" + L.Get("notchAgents.unpriced")));
        body.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { amount, new Border { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 5), Child = label } } });

        if (period == AgentPeriod.Month && summary is not null)
        {
            var multiples = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var status in snapshot.Providers.Where(p => p.Plan?.Monthly is > 0))
            {
                var cost = summary.ByProvider.GetValueOrDefault(status.Provider)?.Cost ?? 0;
                var multiple = cost / status.Plan!.Monthly!.Value;
                var text = multiple < 10 ? $"{multiple:0.0}×" : $"{multiple:0}×";
                var pill = new Border { Classes = { "pill" }, Margin = new Thickness(0, 0, 6, 0), Child = new TextBlock { Text = $"{status.Provider.DisplayName()} {text}" } };
                ToolTip.SetTip(pill, L.Format("notchAgents.planMultipleFormat", text, $"{status.Plan.Name} ({AgentFormat.Cost(status.Plan.Monthly.Value)})"));
                multiples.Children.Add(pill);
            }

            if (multiples.Children.Count > 0)
            {
                body.Children.Add(multiples);
            }
        }

        if (summary is not null && totals.Tokens > 0)
        {
            body.Children.Add(SplitBar(summary.ByProvider, totals.AllPriced));
        }

        var footer = Caption($"{L.Format("notchAgents.tokensFormat", AgentFormat.Tokens(totals.Tokens))} · {L.Format("notchAgents.cachedFormat", AgentFormat.Percent(totals.CacheRate * 100))}");
        ToolTip.SetTip(footer, L.Format("notchAgents.savedFormat", AgentFormat.Cost(totals.Savings)));
        body.Children.Add(footer);
        return Card(body);
    }

    private static Control SplitBar(IReadOnlyDictionary<AgentProvider, UsageTotals> byProvider, bool byCost)
    {
        var grid = new Grid { Height = 6, ClipToBounds = true };
        var total = byProvider.Values.Sum(t => byCost ? t.Cost : t.Tokens);
        if (total <= 0)
        {
            return grid;
        }

        var column = 0;
        foreach (var provider in AgentProviders.All)
        {
            var value = byProvider.TryGetValue(provider, out var t) ? (byCost ? t.Cost : t.Tokens) : 0;
            if (value <= 0)
            {
                continue;
            }

            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(value / total, GridUnitType.Star)));
            var segment = new Border { Background = new SolidColorBrush(Tint(provider)), Margin = new Thickness(0, 0, 1, 0) };
            ToolTip.SetTip(segment, provider.DisplayName());
            Grid.SetColumn(segment, column++);
            grid.Children.Add(segment);
        }

        return new Border { CornerRadius = new CornerRadius(3), ClipToBounds = true, Child = grid };
    }

    // ── Now ─────────────────────────────────────────────────────────────
    private static Control LiveCard(AgentUsageSnapshot snapshot, DateTimeOffset now)
    {
        var body = new StackPanel { Spacing = 6 };
        body.Children.Add(new TextBlock { Text = L.Get("notchAgents.liveCard").ToUpperInvariant(), Classes = { "sectionTitle" } });
        if (snapshot.Live.Count == 0)
        {
            var any = false;
            foreach (var status in snapshot.Providers.Where(p => p.Enabled && p.Seen && p.LastActivity is not null))
            {
                any = true;
                body.Children.Add(Row(status.Provider.DisplayName(), string.Empty, Ago(now - status.LastActivity!.Value), Tint(status.Provider)));
            }

            if (!any)
            {
                body.Children.Add(Caption(L.Get("notchAgents.idle")));
            }

            return Card(body);
        }

        foreach (var turn in snapshot.Live.Take(2))
        {
            var detail = string.Join(" · ", new[]
            {
                string.IsNullOrEmpty(turn.Model) ? null : AgentFormat.ModelDisplayName(turn.Model),
                turn.OutputTokens > 0 ? L.Format("notchAgents.writtenFormat", AgentFormat.Tokens(turn.OutputTokens)) : null,
                turn.Cost > 0 ? AgentFormat.Cost(turn.Cost) : null,
            }.Where(s => s is not null));
            var title = string.IsNullOrEmpty(turn.Project) ? turn.Provider.DisplayName() : turn.Project;
            body.Children.Add(new StackPanel
            {
                Spacing = 1,
                Children =
                {
                    Row(title, string.Empty, AgentFormat.Clock(now - turn.Started), Tint(turn.Provider), pulse: true),
                    Caption(detail.Length > 0 ? detail : turn.Provider.DisplayName(), indent: 16),
                },
            });
        }

        if (snapshot.Live.Count > 2)
        {
            body.Children.Add(new Border { Classes = { "pill" }, HorizontalAlignment = HorizontalAlignment.Left, Child = new TextBlock { Text = $"+{snapshot.Live.Count - 2}" } });
        }

        return Card(body);
    }

    // ── Trend ───────────────────────────────────────────────────────────
    private static Control TrendCard(AgentUsageSnapshot snapshot, AgentPeriod period)
    {
        var buckets = period switch
        {
            AgentPeriod.Week => snapshot.Days.TakeLast(7).ToList(),
            AgentPeriod.Month => snapshot.Days.TakeLast(30).ToList(),
            _ => snapshot.Hours.ToList(),
        };
        var byCost = buckets.All(b => b.Total.AllPriced);
        double Value(UsageTotals t) => byCost ? t.Cost : t.Tokens;
        var max = buckets.Count == 0 ? 0 : buckets.Max(b => Value(b.Total));
        const double height = 64;
        var bars = new Grid { Height = height, ColumnSpacing = buckets.Count > 24 ? 1 : 2 };
        for (var i = 0; i < buckets.Count; i++)
        {
            bars.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom };
            foreach (var provider in AgentProviders.All.Reverse())
            {
                if (!buckets[i].ByProvider.TryGetValue(provider, out var totals) || max <= 0)
                {
                    continue;
                }

                var h = Value(totals) / max * height;
                if (h >= 0.5)
                {
                    stack.Children.Add(new Border { Height = h, Background = new SolidColorBrush(Tint(provider)), CornerRadius = new CornerRadius(1) });
                }
            }

            if (stack.Children.Count == 0)
            {
                var empty = new Border { Height = 2, CornerRadius = new CornerRadius(1) };
                empty.Bind(Border.BackgroundProperty, empty.GetResourceObservable("PanelControlBrush").ToBinding());
                stack.Children.Add(empty);
            }

            ToolTip.SetTip(stack, $"{(period == AgentPeriod.Today ? buckets[i].Start.ToString("HH:00") : buckets[i].Start.ToString("d"))} · {(byCost ? AgentFormat.Cost(buckets[i].Total.Cost) : AgentFormat.Tokens(buckets[i].Total.Tokens))}");
            Grid.SetColumn(stack, i);
            bars.Children.Add(stack);
        }

        return Card(new StackPanel { Spacing = 6, Children = { new TextBlock { Text = L.Get("notchAgents.trendCard").ToUpperInvariant(), Classes = { "sectionTitle" } }, bars } });
    }

    // ── Models and projects ─────────────────────────────────────────────
    private static Control TopCard(string title, IReadOnlyList<NamedTotals> items, bool byCost)
    {
        var body = new StackPanel { Spacing = 5 };
        body.Children.Add(new TextBlock { Text = title.ToUpperInvariant(), Classes = { "sectionTitle" } });
        if (items.Count == 0)
        {
            body.Children.Add(Caption(L.Get("notchAgents.noActivity")));
        }

        foreach (var item in items.Take(3))
        {
            var value = byCost ? AgentFormat.Cost(item.Totals.Cost) : L.Format("notchAgents.tokensFormat", AgentFormat.Tokens(item.Totals.Tokens));
            body.Children.Add(Row(item.Name, string.Empty, value, item.Provider is { } provider ? Tint(provider) : null));
        }

        return Card(body);
    }

    // ── Activity ────────────────────────────────────────────────────────
    private static Control ActivityCard(AgentUsageSnapshot snapshot)
    {
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(new TextBlock { Text = L.Get("notchAgents.activityCard").ToUpperInvariant(), Classes = { "sectionTitle" } });
        var days = snapshot.Days;
        var stats = snapshot.Activity;
        if (days.Count == 0 || stats is null)
        {
            body.Children.Add(Caption(L.Get("notchAgents.noActivity")));
            return Card(body);
        }

        // Columns are weeks starting on the locale's first weekday; today is outlined.
        var first = AgentUsageSummary.FirstDayOfWeek();
        var offset = ((int)days[0].Start.DayOfWeek - (int)first + 7) % 7;
        var columns = (int)Math.Ceiling((offset + days.Count) / 7.0);
        const double cell = 13;
        const double gap = 3;
        var canvas = new Canvas { Width = (columns * (cell + gap)) - gap, Height = (7 * (cell + gap)) - gap, HorizontalAlignment = HorizontalAlignment.Left };
        var accent = Color.FromRgb(0xD9, 0x78, 0x57);
        for (var i = 0; i < days.Count; i++)
        {
            var index = offset + i;
            var level = i < stats.Levels.Count ? stats.Levels[i] : 0.07;
            var square = new Border
            {
                Width = cell,
                Height = cell,
                CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush(level <= 0.07 ? Color.FromArgb(0x24, 0x80, 0x80, 0x80) : accent, level <= 0.07 ? 1 : level),
            };
            if (i == days.Count - 1)
            {
                square.BorderThickness = new Thickness(1.5);
                square.Bind(Border.BorderBrushProperty, square.GetResourceObservable("TextPrimaryBrush").ToBinding());
            }

            ToolTip.SetTip(square, $"{days[i].Start:d} · {AgentFormat.Cost(days[i].Total.Cost)} · {AgentFormat.Tokens(days[i].Total.Tokens)}");
            Canvas.SetLeft(square, index / 7 * (cell + gap));
            Canvas.SetTop(square, index % 7 * (cell + gap));
            canvas.Children.Add(square);
        }

        body.Children.Add(canvas);
        var figures = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*"), ColumnSpacing = 8 };
        void Figure(int column, string value, string label)
        {
            var stack = new StackPanel { Spacing = 0, Children = { new TextBlock { Text = value, FontWeight = FontWeight.SemiBold, FontSize = 13 }, Caption(label) } };
            Grid.SetColumn(stack, column);
            figures.Children.Add(stack);
        }

        Figure(0, stats.Total.AllPriced ? AgentFormat.Cost(stats.Total.Cost) : AgentFormat.Tokens(stats.Total.Tokens), L.Get("win.agentUsage.thirteenWeeks"));
        Figure(1, stats.ActiveDays.ToString(Localizer.Current.Culture), L.Get("notchAgents.activeDays"));
        Figure(2, stats.BusiestDay is { } busiest ? busiest.ToString("d MMM", Localizer.Current.Culture) : "—", L.Get("notchAgents.busiestDay"));
        body.Children.Add(figures);
        if (stats.Streak > 1)
        {
            var flame = new FluentIcons.Avalonia.SymbolIcon { Symbol = FluentIcons.Common.Symbol.Fire, FontSize = 14, Foreground = new SolidColorBrush(Color.FromRgb(0xF7, 0x63, 0x0C)), VerticalAlignment = VerticalAlignment.Center };
            body.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children = { flame, new TextBlock { Text = $"{L.Get("notchAgents.streak")}: {stats.Streak}", FontSize = 12, VerticalAlignment = VerticalAlignment.Center } },
            });
        }

        return Card(body);
    }

    // ── Pieces ──────────────────────────────────────────────────────────
    public static Color Tint(AgentProvider provider)
    {
        var (r, g, b) = provider.Tint();
        return Color.FromRgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
    }

    public static string Ago(TimeSpan elapsed) =>
        elapsed < TimeSpan.FromMinutes(1) ? L.Get("win.agentUsage.justNow") : L.Format("win.agentUsage.agoFormat", AgentFormat.Duration(elapsed));

    private static Border Card(Control child) => new() { Classes = { "card" }, Child = child };

    private static TextBlock Caption(string text, double indent = 0) =>
        new() { Text = text, Classes = { "caption" }, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(indent, 0, 0, 0) };

    private static Control Header(AgentProvider provider, string? plan, bool working)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(new Ellipse { Width = 9, Height = 9, Fill = new SolidColorBrush(Tint(provider)), VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock { Text = provider.DisplayName(), Classes = { "rowTitle" }, VerticalAlignment = VerticalAlignment.Center });
        if (!string.IsNullOrEmpty(plan))
        {
            row.Children.Add(new Border { Classes = { "pill" }, Child = new TextBlock { Text = plan } });
        }

        if (working)
        {
            var pulse = new Ellipse { Width = 6, Height = 6, VerticalAlignment = VerticalAlignment.Center };
            pulse.Bind(Shape.FillProperty, pulse.GetResourceObservable("AccentBrush").ToBinding());
            row.Children.Add(pulse);
        }

        return row;
    }

    private static Control Row(string label, string middle, string value, Color? dot = null, bool pulse = false)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), ColumnSpacing = 6 };
        if (dot is { } color)
        {
            var mark = new Ellipse { Width = pulse ? 8 : 7, Height = pulse ? 8 : 7, Fill = new SolidColorBrush(color), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 2, 0) };
            grid.Children.Add(mark);
        }

        var title = new TextBlock { Text = label, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(title, 1);
        grid.Children.Add(title);
        var mid = new TextBlock { Text = middle, Classes = { "caption" }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };
        Grid.SetColumn(mid, 2);
        grid.Children.Add(mid);
        var right = new TextBlock { Text = value, FontSize = 12.5, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(right, 3);
        grid.Children.Add(right);
        return grid;
    }

    private static Control Meter(double fraction, double? pace, Color tint, bool dimmed)
    {
        var track = new Grid { Height = 5 };
        var background = new Border { CornerRadius = new CornerRadius(2.5) };
        background.Bind(Border.BackgroundProperty, background.GetResourceObservable("PanelControlBrush").ToBinding());
        track.Children.Add(background);
        var value = Math.Clamp(fraction, 0, 1);
        var fill = new Grid { ColumnDefinitions = new ColumnDefinitions($"{value.ToString(System.Globalization.CultureInfo.InvariantCulture)}*,{(1 - value).ToString(System.Globalization.CultureInfo.InvariantCulture)}*") };
        fill.Children.Add(new Border { CornerRadius = new CornerRadius(2.5), Background = new SolidColorBrush(tint, dimmed ? 0.45 : 1) });
        track.Children.Add(fill);
        if (pace is { } p)
        {
            var tick = new Grid { ColumnDefinitions = new ColumnDefinitions($"{Math.Clamp(p, 0, 1).ToString(System.Globalization.CultureInfo.InvariantCulture)}*,Auto,{(1 - Math.Clamp(p, 0, 1)).ToString(System.Globalization.CultureInfo.InvariantCulture)}*") };
            var line = new Border { Width = 2, Margin = new Thickness(0, -2) };
            line.Bind(Border.BackgroundProperty, line.GetResourceObservable("TextPrimaryBrush").ToBinding());
            Grid.SetColumn(line, 1);
            tick.Children.Add(line);
            track.Children.Add(tick);
        }

        return track;
    }
}
