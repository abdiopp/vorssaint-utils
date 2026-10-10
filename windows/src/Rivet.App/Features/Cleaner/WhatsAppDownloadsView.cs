// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Rivet.Core.Localization;
using Rivet.Core.Maintenance.Cleaner;
using Rivet.Core.Platform;
using Rivet.Core.Settings;
using static Rivet.App.Features.Maintenance.MaintenanceUi;

namespace Rivet.App.Features.Cleaner;

/// <summary>
/// WhatsApp downloads, redesigned for Windows (spec §3.7.7): review only.
/// Files whose Mark of the Web names a WhatsApp host are "confirmed" and may
/// be pre-selected by the rules; files that only look like WhatsApp's by
/// name are listed unticked. Nothing runs automatically and every removal
/// goes to the Recycle Bin.
/// </summary>
public sealed class WhatsAppDownloadsView : UserControl
{
    private readonly WhatsAppDownloadsService _service;
    private readonly ISettingsStore _settings;
    private readonly IShellService _shell;
    private IDisposable? _subscription;

    public WhatsAppDownloadsView(IServiceProvider services)
    {
        _service = services.GetRequiredService<WhatsAppDownloadsService>();
        _settings = services.GetRequiredService<ISettingsStore>();
        _shell = services.GetRequiredService<IShellService>();
        Build();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _service.Changed += OnChanged;
        _subscription = _settings.Observe(() => Dispatcher.UIThread.Post(Build), WhatsAppSettings.Enabled, WhatsAppSettings.Categories, WhatsAppSettings.RetentionDays, WhatsAppSettings.Exclusions);
        Build();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _service.Changed -= OnChanged;
        _subscription?.Dispose();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(Build);

    private void Build()
    {
        var enabled = _settings.Get(WhatsAppSettings.Enabled);
        var toggle = new ToggleSwitch { Classes = { "compact" }, IsChecked = enabled };
        Avalonia.Automation.AutomationProperties.SetName(toggle, L.Get("win.cleaner.whatsAppTitle"));
        toggle.IsCheckedChanged += (_, _) => _settings.Set(WhatsAppSettings.Enabled, toggle.IsChecked == true);
        var header = Columns("Auto,*,Auto", 10, Icon("Chat", 18),
            VStack(1, Text(L.Get("win.cleaner.whatsAppTitle"), 14, FontWeight.SemiBold), Caption(L.Get("win.cleaner.whatsAppToggleCaption"))),
            toggle);
        if (!enabled)
        {
            Content = header;
            return;
        }

        var stack = VStack(10, header, Caption(L.Get("win.cleaner.whatsAppIntro")));
        stack.Children.Add(Columns("Auto,*,Auto", 8, Caption(L.Get("whatsAppDownloads.folder")), PathText(_service.DownloadsFolder),
            IconButton("FolderOpen", L.Get("Strings.cleanerRevealInFinder"), () => _shell.OpenFile(_service.DownloadsFolder))));
        stack.Children.Add(TypesRow());
        stack.Children.Add(RetentionRow());

        var scan = Button(_service.IsScanning ? L.Get("Strings.cleanerScanning") : L.Get("Strings.cleanerScan"), () => _ = _service.ScanAsync(), "Search");
        scan.IsEnabled = !_service.IsScanning && !_service.IsCleaning;
        stack.Children.Add(HStack(8, scan, _service.IsScanning ? Progress(null, 80) : null));

        if (_service.LastResult is { } last)
        {
            stack.Children.Add(Caption(L.Format("win.cleaner.whatsAppResultFormat", last.Count, Size(last.Bytes), last.Failed)));
        }

        if (_service.ScanFailed)
        {
            stack.Children.Add(Colored(L.Get("whatsAppDownloads.scanFailed"), "WarningBrush"));
        }
        else if (_service.HasScanned && _service.Candidates.Count == 0)
        {
            stack.Children.Add(Caption(L.Get("whatsAppDownloads.noFiles")));
        }
        else if (_service.Candidates.Count > 0)
        {
            stack.Children.Add(Results());
        }

        Content = stack;
    }

    private Control TypesRow()
    {
        var enabled = WhatsAppRules.ParseCategories(_settings.Get(WhatsAppSettings.Categories)).ToHashSet();
        var panel = new WrapPanel { Orientation = Orientation.Horizontal };
        var all = new CheckBox { Content = L.Get("whatsAppDownloads.allTypes"), IsChecked = enabled.Count == 6, Margin = new Thickness(0, 0, 12, 0) };
        all.IsCheckedChanged += (_, _) => _settings.Set(WhatsAppSettings.Categories, all.IsChecked == true ? WhatsAppRules.FormatCategories(Enum.GetValues<WhatsAppCategory>()) : string.Empty);
        panel.Children.Add(all);
        foreach (var category in Enum.GetValues<WhatsAppCategory>())
        {
            var box = new CheckBox { Content = CategoryName(category), IsChecked = enabled.Contains(category), Margin = new Thickness(0, 0, 12, 0) };
            box.IsCheckedChanged += (_, _) =>
            {
                var set = WhatsAppRules.ParseCategories(_settings.Get(WhatsAppSettings.Categories)).ToHashSet();
                if (box.IsChecked == true)
                {
                    set.Add(category);
                }
                else
                {
                    set.Remove(category);
                }

                _settings.Set(WhatsAppSettings.Categories, WhatsAppRules.FormatCategories(set));
            };
            panel.Children.Add(box);
        }

        return VStack(4, Caption(L.Get("whatsAppDownloads.fileTypes")), panel);
    }

    private Control RetentionRow()
    {
        var days = _settings.Get(WhatsAppSettings.RetentionDays);
        var options = WhatsAppRules.RetentionOptions.ToList();
        var combo = new ComboBox { ItemsSource = options.Select(d => L.Format("whatsAppDownloads.daysFormat", d)).ToList(), SelectedIndex = Math.Max(0, options.IndexOf(days)) };
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedIndex >= 0)
            {
                _settings.Set(WhatsAppSettings.RetentionDays, options[combo.SelectedIndex]);
            }
        };
        return VStack(2, Columns("*,Auto", 8, Text(L.Get("whatsAppDownloads.retention")), combo), Caption(L.Get("whatsAppDownloads.retentionCaption")));
    }

    private Control Results()
    {
        var candidates = _service.Candidates;
        var stack = VStack(6);
        stack.Children.Add(Columns("*,Auto", 8,
            Text(L.Format("whatsAppDownloads.resultsFormat", candidates.Count, Size(candidates.Sum(c => c.Size))), 13, FontWeight.SemiBold),
            Button(L.Get("whatsAppDownloads.selectRules"), _service.SelectByRules)));
        stack.Children.Add(Caption(L.Get("win.cleaner.whatsAppSelectionNote")));

        var list = VStack(2);
        foreach (var candidate in candidates)
        {
            list.Children.Add(Row(candidate));
        }

        stack.Children.Add(new ScrollViewer { MaxHeight = 320, Content = list, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        stack.Children.Add(Caption(L.Get("whatsAppDownloads.trashNote")));
        var clean = Button(L.Format("whatsAppDownloads.cleanSelectedFormat", _service.SelectedCount, Size(_service.SelectedBytes)), () => _ = CleanAsync(), accent: true);
        clean.IsEnabled = _service.SelectedCount > 0 && !_service.IsCleaning;
        stack.Children.Add(clean);
        return stack;
    }

    private Control Row(WhatsAppCandidate candidate)
    {
        var kept = _service.IsExcluded(candidate);
        var box = CheckBox(_service.IsSelected(candidate) && !kept, selected => _service.SetSelected(candidate, selected), candidate.Name);
        box.IsEnabled = !kept;
        var evidence = candidate.Evidence == WhatsAppEvidence.WebMark
            ? Pill(L.Get("win.cleaner.whatsAppConfirmed"), "AccentFaintBrush", "AccentBrush")
            : Pill(L.Get("win.cleaner.whatsAppNameOnly"), "WarningSoftBrush", "WarningBrush");
        if (candidate.Evidence == WhatsAppEvidence.NameOnly)
        {
            ToolTip.SetTip(evidence, L.Get("win.cleaner.whatsAppNameOnlyHint"));
        }

        var detail = $"{CategoryName(candidate.Category)} · {When(candidate.DownloadedUtc)}";
        var action = kept
            ? LinkButton(L.Get("whatsAppDownloads.manageAgain"), () => _service.SetKept(candidate, false))
            : LinkButton(L.Get("whatsAppDownloads.keep"), () => _service.SetKept(candidate, true));
        var row = Columns("Auto,Auto,*,Auto,Auto", 6, box, Icon(CategoryIcon(candidate.Category), 16),
            VStack(0, HStack(6, Text(candidate.Name, 12, wrap: false), evidence), Caption(kept ? detail + " · " + L.Get("win.cleaner.whatsAppKept") : detail, wrap: false)),
            Caption(Size(candidate.Size), wrap: false),
            action);
        row.ContextMenu = Menu((L.Get("Strings.cleanerRevealInFinder"), () => _shell.RevealInExplorer(candidate.Path), true));
        return row;
    }

    private async Task CleanAsync()
    {
        var nameOnly = _service.Candidates.Count(c => _service.IsSelected(c) && !_service.IsExcluded(c) && c.Evidence == WhatsAppEvidence.NameOnly);
        if (nameOnly > 0 && !await ConfirmAsync(this, L.Get("win.cleaner.whatsAppConfirmTitle"), L.Format("win.cleaner.whatsAppConfirmNameOnlyFormat", nameOnly), L.Format("whatsAppDownloads.cleanSelectedFormat", _service.SelectedCount, Size(_service.SelectedBytes))))
        {
            return;
        }

        await _service.CleanSelectedAsync();
    }

    private static string CategoryName(WhatsAppCategory category) => L.Get(category switch
    {
        WhatsAppCategory.Image => "whatsAppDownloads.image",
        WhatsAppCategory.Video => "whatsAppDownloads.video",
        WhatsAppCategory.Audio => "whatsAppDownloads.audio",
        WhatsAppCategory.Document => "whatsAppDownloads.document",
        WhatsAppCategory.Archive => "whatsAppDownloads.archive",
        _ => "whatsAppDownloads.other",
    });

    private static string CategoryIcon(WhatsAppCategory category) => category switch
    {
        WhatsAppCategory.Image => "Image",
        WhatsAppCategory.Video => "Video",
        WhatsAppCategory.Audio => "MusicNote2",
        WhatsAppCategory.Document => "Document",
        WhatsAppCategory.Archive => "FolderZip",
        _ => "DocumentQuestionMark",
    };
}
