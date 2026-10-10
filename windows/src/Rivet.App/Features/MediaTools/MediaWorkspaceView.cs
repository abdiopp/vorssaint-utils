// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentIcons.Avalonia;
using FluentIcons.Common;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Controls;
using Rivet.Core.Localization;
using Rivet.Core.Modules.MediaTools;
using Rivet.Core.Settings;

namespace Rivet.App.Features.MediaTools;

/// <summary>Where the workspace is shown; it sets the size, the header and the scrolling.</summary>
public enum MediaHost
{
    /// <summary>The tray panel's Utilities tab: compact, scrolls inside a 430 DIP cap; the panel shows the title.</summary>
    Panel,

    /// <summary>Settings › Media: full size; the page header shows the title and the window scrolls.</summary>
    Settings,

    /// <summary>The Media window: full size with its own header, scrolling to fill.</summary>
    Window,
}

/// <summary>
/// The Media workspace (spec 07 §3.6.2): the tool picker, the file card
/// (input button and drop target, output row, the batch subfolder switch),
/// the options card of the current tool, the action row and the status card.
/// The same view serves every host; it rebuilds its options only when the
/// tool or a layout-changing option changes, so typing never loses focus.
/// </summary>
public sealed partial class MediaWorkspaceView : UserControl
{
    private readonly MediaToolsService _service;
    private readonly ISettingsStore _settings;
    private readonly MediaHost _host;
    private readonly bool _compact;
    private readonly StackPanel _body;
    private readonly List<IDisposable> _observers = [];
    private readonly UniformGrid _toolPicker = new() { Rows = 1 };
    private string? _layoutKey;

    // File card
    private readonly Border _inputButton = new() { Classes = { "card" }, Cursor = new Cursor(StandardCursorType.Hand) };
    private readonly TextBlock _inputTitle = new() { FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly SymbolIcon _inputIcon = new() { FontSize = 22, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _clearInput = new() { Classes = { "icon" }, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _outputName = new() { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _destination = new();
    private readonly CheckBox _subfolder = new();

    // Action row and status
    private readonly Button _primary = new() { Classes = { "accent" }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
    private readonly Button _cancel = new();
    private readonly Button _edit = new();
    private readonly Border _status = new() { Classes = { "card" }, IsVisible = false };
    private Control? _options;

    public MediaWorkspaceView(IServiceProvider services, MediaHost host)
    {
        _service = services.GetRequiredService<MediaToolsService>();
        _settings = services.GetRequiredService<ISettingsStore>();
        _host = host;
        _compact = host == MediaHost.Panel;
        _body = new StackPanel { Spacing = _compact ? 9 : 12 };

        var root = new StackPanel { Spacing = _compact ? 10 : 14 };
        if (host == MediaHost.Window)
        {
            root.Children.Add(new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new TextBlock { Text = L.Get("Strings.mediaName"), FontSize = 16, FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = L.Get("Strings.mediaLocalNote"), Classes = { "caption" } },
                },
            });
        }
        else if (host == MediaHost.Panel)
        {
            root.Children.Add(new TextBlock { Text = L.Get("Strings.mediaLocalNote"), Classes = { "caption" } });
        }

        root.Children.Add(BuildToolPicker());
        if (host == MediaHost.Settings)
        {
            root.Children.Add(_body);
            Content = root;
        }
        else
        {
            var scroll = new ScrollViewer { Content = _body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalAlignment = VerticalAlignment.Top };
            if (_compact)
            {
                scroll.MaxHeight = 430;
            }

            // A grid (not the stack) so the scroller gets a bounded height and actually scrolls.
            var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), RowSpacing = _compact ? 10 : 14 };
            layout.Children.Add(root);
            Grid.SetRow(scroll, 1);
            layout.Children.Add(scroll);
            Content = layout;
        }

        BuildFileCardParts();
        BuildActionParts();
        Rebuild();
    }

    public MediaToolsService Service => _service;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _service.Changed += OnServiceChanged;
        _service.JobChanged += OnJobChanged;
        _observers.Add(_settings.Observe(() => Dispatcher.UIThread.Post(OnSettingsChanged), LayoutSettings));
        _observers.Add(_settings.Observe(() => Dispatcher.UIThread.Post(OnImageSettingsChanged), ImageSettings));
        Rebuild();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        // Closing a host cancels nothing but its own observers; a running export continues.
        _service.Changed -= OnServiceChanged;
        _service.JobChanged -= OnJobChanged;
        foreach (var observer in _observers)
        {
            observer.Dispose();
        }

        _observers.Clear();
        _previewCancel?.Cancel();
        base.OnDetachedFromVisualTree(e);
    }

    private static readonly SettingDefinition[] LayoutSettings =
    [
        MediaSettings.LastTool, MediaSettings.VideoSizing, MediaSettings.GifSizing, MediaSettings.ImageFormat,
        MediaSettings.ImageResizeKind, MediaSettings.WatermarkKind, MediaSettings.ImageSelectedProfileId, MediaSettings.ImageProfiles,
    ];

    private static readonly SettingDefinition[] ImageSettings =
    [
        MediaSettings.ImageQuality, MediaSettings.ImageMaxDimension, MediaSettings.ImageResizeWidth, MediaSettings.ImageResizeHeight,
        MediaSettings.ImageExactMode, MediaSettings.ImageStripMetadata, MediaSettings.WatermarkText, MediaSettings.WatermarkLogoPath,
        MediaSettings.WatermarkPosition, MediaSettings.WatermarkOpacity, MediaSettings.WatermarkMargin, MediaSettings.WatermarkScale,
        MediaSettings.ImageRenamePattern, MediaSettings.ImageBackground, MediaSettings.ImagePreserveModificationDate, MediaSettings.ImageSaveInSubfolder,
    ];

    private void OnServiceChanged(object? sender, EventArgs e) => Rebuild();

    private void OnJobChanged(object? sender, EventArgs e) => RefreshJob();

    private void OnSettingsChanged()
    {
        if (LayoutKey() != _layoutKey)
        {
            Rebuild();
        }
        else
        {
            RefreshProfileState();
        }
    }

    private void OnImageSettingsChanged()
    {
        _service.OnImageOptionsChanged();
        RefreshProfileState();
        SchedulePreview();
    }

    /// <summary>What decides the shape of the options card.</summary>
    private string LayoutKey() => string.Join('|',
        _service.Tool,
        _service.Inputs.Count > 1,
        _service.Inputs.Count > 0,
        _settings.Get(MediaSettings.VideoSizing),
        _settings.Get(MediaSettings.GifSizing),
        _settings.Get(MediaSettings.ImageFormat),
        _settings.Get(MediaSettings.ImageResizeKind),
        _settings.Get(MediaSettings.WatermarkKind),
        _settings.Get(MediaSettings.ImageSelectedProfileId),
        _settings.Get(MediaSettings.ImageProfiles).Length,
        _moreOptionsOpen);

    /// <summary>Rebuilds the tool picker state, the file card, the options and the status.</summary>
    private void Rebuild()
    {
        var key = LayoutKey();
        if (key != _layoutKey || _options is null)
        {
            _layoutKey = key;
            _options = BuildOptions();
        }

        foreach (var child in _toolPicker.Children.OfType<RadioButton>())
        {
            child.IsChecked = Equals(child.Tag, _service.Tool);
        }

        _body.Children.Clear();
        _body.Children.Add(BuildFileCard());
        if (_options is not null)
        {
            _body.Children.Add(_options);
        }

        _body.Children.Add(BuildActionRow());
        _body.Children.Add(_status);
        RefreshFileCard();
        RefreshJob();
        RefreshProfileState();
        SchedulePreview();
    }

    // ── Tool picker ─────────────────────────────────────────────────────

    private Control BuildToolPicker()
    {
        var grid = _toolPicker;
        grid.Children.Clear();
        foreach (var (tool, key, icon) in new[]
                 {
                     (MediaTool.Video, "Strings.mediaToolVideo", Symbol.Video),
                     (MediaTool.Gif, "Strings.mediaToolGIF", Symbol.Gif),
                     (MediaTool.Image, "Strings.mediaToolImage", Symbol.Image),
                     (MediaTool.Text, "Strings.mediaToolText", Symbol.TextT),
                 })
        {
            var button = new RadioButton
            {
                Classes = { "tab" },
                GroupName = "mediaTool" + GetHashCode(),
                Tag = tool,
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    Children =
                    {
                        new SymbolIcon { Symbol = icon, FontSize = 14, VerticalAlignment = VerticalAlignment.Center },
                        new TextBlock { Text = L.Get(key), VerticalAlignment = VerticalAlignment.Center, FontSize = _compact ? 12 : 13 },
                    },
                },
            };
            button.IsCheckedChanged += (_, _) =>
            {
                if (button.IsChecked == true)
                {
                    _service.SetTool(tool);
                }
            };
            grid.Children.Add(button);
        }

        return new Border { Classes = { "card" }, Padding = new Thickness(3), Child = grid };
    }


    // ── File card ───────────────────────────────────────────────────────

    private void BuildFileCardParts()
    {
        _clearInput.Content = new SymbolIcon { Symbol = Symbol.Dismiss, FontSize = 13 };
        ToolTip.SetTip(_clearInput, L.Get("Strings.mediaCancel"));
        _clearInput.Click += (_, _) => _service.ClearInputs();

        var hint = new TextBlock { Text = L.Get("Strings.mediaDropHint"), Classes = { "caption" }, TextWrapping = TextWrapping.Wrap };
        var texts = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Children = { _inputTitle, hint } };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 10 };
        grid.Children.Add(_inputIcon);
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);
        Grid.SetColumn(_clearInput, 2);
        grid.Children.Add(_clearInput);
        _inputButton.Child = grid;
        _inputButton.MinHeight = _compact ? 52 : 62;
        _inputButton.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(_inputButton).Properties.IsLeftButtonPressed && e.Source is Visual v && v.FindAncestorOfType<Button>() is null && v is not Button)
            {
                e.Handled = true;
                _ = ChooseInputAsync();
            }
        };
        DragDrop.SetAllowDrop(_inputButton, true);
        _inputButton.AddHandler(DragDrop.DragEnterEvent, OnDragEnter);
        _inputButton.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        _inputButton.AddHandler(DragDrop.DragLeaveEvent, (_, _) => SetDropHighlight(false));
        _inputButton.AddHandler(DragDrop.DropEvent, OnDrop);

        _destination.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children =
            {
                new SymbolIcon { Symbol = Symbol.Folder, FontSize = 14, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = L.Get("Strings.mediaChooseOutput"), VerticalAlignment = VerticalAlignment.Center },
            },
        };
        _destination.Click += (_, _) => _ = ChooseDestinationAsync();
        _subfolder.Content = L.Get("MediaImageConverterStrings.saveInSubfolder");
        _subfolder.IsCheckedChanged += (_, _) =>
        {
            var value = _subfolder.IsChecked == true;
            if (_settings.Get(MediaSettings.ImageSaveInSubfolder) != value)
            {
                _settings.Set(MediaSettings.ImageSaveInSubfolder, value);
            }
        };
    }

    private Control BuildFileCard()
    {
        Detach(_inputButton, _outputName, _destination, _subfolder);
        var outputRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 8 };
        var label = new TextBlock { Text = L.Get("Strings.mediaOutput"), Classes = { "caption" }, VerticalAlignment = VerticalAlignment.Center };
        outputRow.Children.Add(label);
        Grid.SetColumn(_outputName, 1);
        outputRow.Children.Add(_outputName);
        Grid.SetColumn(_destination, 2);
        outputRow.Children.Add(_destination);
        var card = new StackPanel { Spacing = 8, Children = { _inputButton, outputRow } };
        if (_service.IsBatch)
        {
            card.Children.Add(_subfolder);
        }

        return new Border { Classes = { "card" }, Child = card };
    }

    private void RefreshFileCard()
    {
        var inputs = _service.Inputs;
        var tool = _service.Tool;
        _inputIcon.Symbol = tool switch
        {
            MediaTool.Video => Symbol.Video,
            MediaTool.Gif => Symbol.Gif,
            MediaTool.Text => Symbol.ScanText,
            _ => Symbol.Image,
        };
        _inputTitle.Text = inputs.Count switch
        {
            0 => L.Get("Strings.mediaSelectFile"),
            1 => Path.GetFileName(inputs[0]),
            _ => L.Format("MediaImageConverterStrings.filesSelectedFormat", inputs.Count),
        };
        _clearInput.IsVisible = inputs.Count > 0;
        var output = _service.Output;
        _outputName.Text = output is null ? L.Get("Strings.mediaOutputAutomatic") : Path.GetFileName(output.TrimEnd('\\', '/'));
        ToolTip.SetTip(_outputName, output);
        var running = _service.Job.IsRunning;
        _destination.IsEnabled = inputs.Count > 0 && !running;
        _subfolder.IsChecked = _settings.Get(MediaSettings.ImageSaveInSubfolder);
        _subfolder.IsEnabled = !running && _service.ManualOutput is null;
    }

    private void SetDropHighlight(bool on)
    {
        if (on)
        {
            var accent = this.FindResource(ActualThemeVariant, "AccentBrush") as ISolidColorBrush;
            var color = accent?.Color ?? Colors.DodgerBlue;
            _inputButton.Background = new SolidColorBrush(color, 0.16);
            _inputButton.BorderBrush = new SolidColorBrush(color, 0.70);
        }
        else
        {
            _inputButton.ClearValue(Border.BackgroundProperty);
            _inputButton.ClearValue(Border.BorderBrushProperty);
        }
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        var files = e.DataTransfer.Contains(DataFormat.File);
        e.DragEffects = files ? DragDropEffects.Copy : DragDropEffects.None;
        SetDropHighlight(files);
    }

    private void OnDragOver(object? sender, DragEventArgs e) =>
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;

    private void OnDrop(object? sender, DragEventArgs e)
    {
        SetDropHighlight(false);
        var paths = e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>().ToList() ?? [];
        if (paths.Count > 0)
        {
            _service.SetInputs(paths);
        }
    }

    private async Task ChooseInputAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null)
        {
            return;
        }

        var tool = _service.Tool;
        var images = tool is MediaTool.Image or MediaTool.Text;
        var patterns = (images ? MediaImageFormats.ImageInputExtensions : MediaImageFormats.VideoInputExtensions).Select(x => "*." + x).ToList();
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = tool == MediaTool.Image,
            Title = L.Get("Strings.mediaSelectFile"),
            FileTypeFilter = [new FilePickerFileType(images ? L.Get("Strings.mediaToolImage") : L.Get("Strings.mediaToolVideo")) { Patterns = patterns }],
        }).ConfigureAwait(true);
        var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        if (paths.Count > 0)
        {
            _service.SetInputs(paths);
        }
    }

    private async Task ChooseDestinationAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null || _service.Inputs.Count == 0)
        {
            return;
        }

        var current = _service.Output;
        var startFolder = current is null ? null : await top.StorageProvider.TryGetFolderFromPathAsync(_service.IsBatch ? current : Path.GetDirectoryName(current) ?? current).ConfigureAwait(true);
        if (_service.IsBatch)
        {
            var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = L.Get("Strings.mediaChooseOutput"),
                SuggestedStartLocation = startFolder,
            }).ConfigureAwait(true);
            if (folders.FirstOrDefault()?.TryGetLocalPath() is { } folder)
            {
                _service.SetManualOutput(folder);
            }

            return;
        }

        var (typeName, extension) = _service.Tool switch
        {
            MediaTool.Video => ("MP4", "mp4"),
            MediaTool.Gif => ("GIF", "gif"),
            MediaTool.Text => (L.Get("win.mediaTools.textFile"), "txt"),
            _ => (MediaImageFormats.StorageValue(_service.CurrentImageOptions().Format).ToUpperInvariant(), _service.CurrentImageOptions().Extension),
        };
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = L.Get("Strings.mediaChooseOutput"),
            SuggestedFileName = current is null ? null : Path.GetFileName(current),
            SuggestedStartLocation = startFolder,
            DefaultExtension = extension,
            ShowOverwritePrompt = true,
            FileTypeChoices = [new FilePickerFileType(typeName) { Patterns = ["*." + extension] }],
        }).ConfigureAwait(true);
        if (file?.TryGetLocalPath() is { } path)
        {
            _service.SetManualOutput(path);
        }
    }

    // ── Action row and status ───────────────────────────────────────────

    private void BuildActionParts()
    {
        _primary.Click += (_, _) => _ = _service.RunAsync();
        _cancel.Content = L.Get("Strings.mediaCancel");
        _cancel.Click += (_, _) => _service.Cancel();
        _edit.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children =
            {
                new SymbolIcon { Symbol = Symbol.Edit, FontSize = 14, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = L.Get("Strings.menuEdit"), VerticalAlignment = VerticalAlignment.Center },
            },
        };
        _edit.Click += async (_, _) =>
        {
            _edit.IsEnabled = false;
            try
            {
                await _service.EditAsync().ConfigureAwait(true);
            }
            finally
            {
                _edit.IsEnabled = true;
            }
        };
    }

    private Control BuildActionRow()
    {
        Detach(_primary, _cancel, _edit);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 8 };
        grid.Children.Add(_primary);
        Grid.SetColumn(_edit, 1);
        grid.Children.Add(_edit);
        Grid.SetColumn(_cancel, 2);
        grid.Children.Add(_cancel);
        return grid;
    }

    private void RefreshJob()
    {
        var job = _service.Job;
        var tool = _service.Tool;
        var running = job.IsRunning;
        _primary.Content = tool switch
        {
            MediaTool.Video => L.Get("Strings.mediaStartVideo"),
            MediaTool.Gif => L.Get("Strings.mediaStartGIF"),
            MediaTool.Text => L.Get("Strings.mediaStartText"),
            _ => _service.CurrentImageOptions().Format == ImageOutputFormat.Pdf ? L.Get("Strings.mediaStartConvertPDF") : L.Get("Strings.mediaStartImage"),
        };
        _primary.IsEnabled = _service.Inputs.Count > 0 && !running;
        _cancel.IsVisible = running;
        _edit.IsVisible = tool == MediaTool.Video && _service.HasEditor;
        _edit.IsEnabled = _service.Inputs.Count == 1 && !running;
        _destination.IsEnabled = _service.Inputs.Count > 0 && !running;
        _status.Child = BuildStatus();
        _status.IsVisible = _status.Child is not null;
    }

    private Control? BuildStatus()
    {
        var job = _service.Job;
        switch (job.Phase)
        {
            case MediaJobPhase.Running:
            {
                var percent = (int)Math.Floor(job.Progress * 100);
                var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
                header.Children.Add(new TextBlock { Text = L.Get("Strings.mediaRunning"), FontWeight = FontWeight.SemiBold });
                var value = new TextBlock { Text = $"{percent}%", Classes = { "caption" } };
                Grid.SetColumn(value, 1);
                header.Children.Add(value);
                return new StackPanel { Spacing = 6, Children = { header, new ProgressBar { Minimum = 0, Maximum = 1, Value = job.Progress, Height = 4 } } };
            }

            case MediaJobPhase.Completed when job.Result is { } result:
                return BuildResult(result);

            case MediaJobPhase.Failed:
                return StatusLine(Symbol.ErrorCircle, job.Error ?? string.Empty, "WarningBrush");

            case MediaJobPhase.Cancelled:
                return StatusLine(Symbol.DismissCircle, L.Get("Strings.mediaCancelled"), "TextSecondaryBrush");

            default:
                return _service.InputError is { } error ? StatusLine(Symbol.ErrorCircle, error, "WarningBrush") : null;
        }
    }

    private Control StatusLine(Symbol icon, string text, string brushKey)
    {
        var brush = this.FindResource(ActualThemeVariant, brushKey) as IBrush ?? Brushes.Orange;
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                new SymbolIcon { Symbol = icon, FontSize = 16, Foreground = brush, VerticalAlignment = VerticalAlignment.Top },
                new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = brush, MaxWidth = _compact ? 260 : 640 },
            },
        };
    }

    private Control BuildResult(MediaJobResult result)
    {
        var panel = new StackPanel { Spacing = 6 };
        var success = this.FindResource(ActualThemeVariant, "SuccessBrush") as IBrush ?? Brushes.Green;
        panel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                new SymbolIcon { Symbol = Symbol.CheckmarkCircle, FontSize = 16, Foreground = success, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = L.Get("Strings.mediaCompleted"), FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center },
            },
        });

        var batch = result.Tool == MediaTool.Image && (result.Outputs.Count + result.Failures.Count) > 1;
        if (batch)
        {
            panel.Children.Add(Caption(result.Failures.Count == 0
                ? L.Format("MediaImageConverterStrings.batchSavedFormat", result.Succeeded)
                : L.Format("MediaImageConverterStrings.batchPartialFormat", result.Succeeded, result.Failures.Count)));
        }
        else if (result.Outputs.FirstOrDefault() is { } output)
        {
            panel.Children.Add(Caption(L.Format("Strings.mediaResultSavedFormat", Path.GetFileName(output))));
        }

        if (result.Tool != MediaTool.Text && result.OutputBytes > 0)
        {
            var (line, grew, delta) = MediaText.SizeLine(result.InputBytes, result.OutputBytes);
            var change = grew
                ? L.Format("MediaImageConverterStrings.grewBytesFormat", MediaText.FormatBytes(-delta))
                : L.Format("MediaImageConverterStrings.savedBytesFormat", MediaText.FormatBytes(delta));
            panel.Children.Add(Caption($"{line} · {change}"));
            if (grew)
            {
                panel.Children.Add(Caption(L.Get("Strings.mediaResultGrewCaption"), "WarningBrush"));
            }
        }

        foreach (var failure in result.Failures.Take(3))
        {
            panel.Children.Add(Caption($"{Path.GetFileName(failure.Input)}: {failure.Message}", "WarningBrush"));
        }

        if (result.Tool == MediaTool.Text)
        {
            var text = result.Text ?? string.Empty;
            var box = new TextBox
            {
                Text = text.Length == 0 ? L.Get("Strings.mediaEmptyText") : text,
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, monospace"),
                FontSize = 12,
                MinHeight = (_compact ? 5 : 8) * 17,
                MaxHeight = (_compact ? 5 : 8) * 17,
            };
            ScrollViewer.SetVerticalScrollBarVisibility(box, ScrollBarVisibility.Auto);
            panel.Children.Add(box);
        }

        var buttons = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 6, LineSpacing = 6 };
        buttons.Children.Add(SmallButton(Symbol.FolderOpen, L.Get("Strings.mediaOpenInFinder"), _service.RevealOutputs));
        if (result.Tool == MediaTool.Text && !string.IsNullOrEmpty(result.Text))
        {
            buttons.Children.Add(SmallButton(Symbol.Copy, L.Get("Strings.mediaCopyText"), _service.CopyText));
        }

        if (batch)
        {
            buttons.Children.Add(SmallButton(Symbol.ClipboardPaste, L.Get("MediaImageConverterStrings.copySummary"), _service.CopySummary));
        }

        buttons.Children.Add(SmallButton(Symbol.ArrowClockwise, L.Get("Strings.mediaRunAgain"), () => _ = _service.RunAsync()));
        panel.Children.Add(buttons);
        return panel;
    }

    private TextBlock Caption(string text, string? brushKey = null)
    {
        var block = new TextBlock { Text = text, Classes = { "caption" }, TextWrapping = TextWrapping.Wrap };
        if (brushKey is not null && this.FindResource(ActualThemeVariant, brushKey) is IBrush brush)
        {
            block.Foreground = brush;
        }

        return block;
    }

    private static Button SmallButton(Symbol icon, string text, Action onClick)
    {
        var button = new Button
        {
            Padding = new Thickness(10, 4),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new SymbolIcon { Symbol = icon, FontSize = 13, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center },
                },
            },
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>Removes reused controls from their previous parent before they are placed again.</summary>
    private static void Detach(params Control[] controls)
    {
        foreach (var control in controls)
        {
            switch (control.Parent)
            {
                case Panel panel:
                    panel.Children.Remove(control);
                    break;
                case Decorator decorator:
                    decorator.Child = null;
                    break;
                case ContentControl content:
                    content.Content = null;
                    break;
            }
        }
    }
}
