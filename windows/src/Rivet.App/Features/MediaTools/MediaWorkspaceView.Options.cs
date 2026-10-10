// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using FluentIcons.Avalonia;
using FluentIcons.Common;
using Rivet.App.Controls;
using Rivet.Core.Localization;
using Rivet.Core.Modules.MediaTools;
using Rivet.Core.Settings;
using Rivet.Imaging.MediaTools;

namespace Rivet.App.Features.MediaTools;

/// <summary>The per-tool options card, the image preview and the profile row (spec 07 §3.6.4–§3.6.7).</summary>
public sealed partial class MediaWorkspaceView
{
    private static readonly TimeSpan PreviewDelay = TimeSpan.FromMilliseconds(180);

    private bool _moreOptionsOpen;
    private CancellationTokenSource? _previewCancel;
    private readonly Image _previewImage = new() { Stretch = Stretch.Uniform };
    private readonly Border _previewFrame = new() { CornerRadius = new CornerRadius(6), ClipToBounds = true };
    private readonly TextBlock _previewName = new() { Classes = { "caption" }, TextTrimming = TextTrimming.CharacterEllipsis };
    private TextBlock? _profileModified;
    private Button? _profileDelete;
    private TextBox? _profileName;

    private Control? BuildOptions() => _service.Tool switch
    {
        MediaTool.Video => BuildVideoOptions(),
        MediaTool.Gif => BuildGifOptions(),
        MediaTool.Image => BuildImageOptions(),
        _ => BuildTextOptions(),
    };

    /// <summary>Settings that change the layout are written through here so the card rebuilds at once.</summary>
    private void ForceRebuild()
    {
        _layoutKey = null;
        Rebuild();
    }

    // ── Video ───────────────────────────────────────────────────────────

    private Control BuildVideoOptions()
    {
        var stack = OptionsStack();
        if (!_service.VideoAvailable)
        {
            stack.Children.Add(Caption(L.Get("win.mediaTools.videoUnavailable"), "WarningBrush"));
        }

        stack.Children.Add(TrimRow(MediaSettings.VideoStart, MediaSettings.VideoEnd));
        var sizing = MediaSettings.ParseSizing(_settings.Get(MediaSettings.VideoSizing));
        stack.Children.Add(SizingPicker(MediaSettings.VideoSizing, sizing));
        if (sizing == MediaSizingMode.Resolution)
        {
            stack.Children.Add(CompressionControl(MediaSettings.VideoQuality));
            stack.Children.Add(LabeledRow(L.Get("Strings.mediaMaxSize"), IntField(MediaSettings.VideoMaxDimension, 640, 3840, 320, " px")));
        }
        else
        {
            stack.Children.Add(TargetSizeRow(MediaSettings.VideoTargetMegabytes));
        }

        return OptionsCard(stack);
    }

    // ── GIF ─────────────────────────────────────────────────────────────

    private Control BuildGifOptions()
    {
        var stack = OptionsStack();
        if (!_service.GifAvailable)
        {
            stack.Children.Add(Caption(L.Get("win.mediaTools.videoUnavailable"), "WarningBrush"));
        }

        stack.Children.Add(TrimRow(MediaSettings.GifStart, MediaSettings.GifEnd));
        var sizing = MediaSettings.ParseSizing(_settings.Get(MediaSettings.GifSizing));
        stack.Children.Add(SizingPicker(MediaSettings.GifSizing, sizing));
        if (sizing == MediaSizingMode.Resolution)
        {
            stack.Children.Add(LabeledRow(L.Get("Strings.mediaFPS"), FpsSlider()));
            stack.Children.Add(LabeledRow(L.Get("Strings.mediaWidth"), IntField(MediaSettings.GifWidth, 160, 1600, 80, " px")));
        }
        else
        {
            stack.Children.Add(TargetSizeRow(MediaSettings.GifTargetMegabytes));
        }

        stack.Children.Add(Check(MediaSettings.GifLoops, L.Get("Strings.mediaLoopGIF")));
        return OptionsCard(stack);
    }

    private Control FpsSlider()
    {
        var value = _settings.Get(MediaSettings.GifFps);
        var label = new TextBlock { Text = value.ToString("0", CultureInfo.CurrentCulture), Width = 28, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        var slider = new Slider { Minimum = 1, Maximum = 30, SmallChange = 1, LargeChange = 1, TickFrequency = 1, IsSnapToTickEnabled = true, Value = value, Width = _compact ? 150 : 200 };
        AutomationProperties.SetName(slider, L.Get("Strings.mediaFPS"));
        slider.ValueChanged += (_, e) =>
        {
            label.Text = e.NewValue.ToString("0", CultureInfo.CurrentCulture);
            _settings.Set(MediaSettings.GifFps, e.NewValue);
        };
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { slider, label } };
    }

    // ── Text ────────────────────────────────────────────────────────────

    private Control BuildTextOptions()
    {
        var stack = OptionsStack();
        if (!_service.OcrAvailable)
        {
            stack.Children.Add(Caption(L.Get("win.mediaTools.ocrUnavailable"), "WarningBrush"));
        }

        var accurate = _settings.Get(MediaSettings.TextAccurate);
        stack.Children.Add(LabeledRow(L.Get("Strings.mediaOCRMode"), Segmented(
            [(true, L.Get("Strings.mediaOCRAccurate")), (false, L.Get("Strings.mediaOCRFast"))],
            accurate,
            v => _settings.Set(MediaSettings.TextAccurate, v),
            width: _compact ? 170 : 220)));
        stack.Children.Add(Caption(L.Get("Strings.mediaTextOutputNote")));
        return OptionsCard(stack);
    }

    // ── Image ───────────────────────────────────────────────────────────

    private Control BuildImageOptions()
    {
        var options = _service.CurrentImageOptions();
        var stack = OptionsStack();

        // Quick presets replace every option and turn the watermark off.
        var presets = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 6, LineSpacing = 6 };
        foreach (var (key, preset) in new[]
                 {
                     ("MediaImageConverterStrings.presetWeb", MediaImagePresets.Web),
                     ("MediaImageConverterStrings.presetSocial", MediaImagePresets.Social),
                     ("MediaImageConverterStrings.presetDocs", MediaImagePresets.Docs),
                 })
        {
            presets.Children.Add(SmallButton(Symbol.Sparkle, L.Get(key), () =>
            {
                _service.ApplyImageOptions(preset);
                ForceRebuild();
            }));
        }

        stack.Children.Add(presets);

        if (_service.Inputs.Count > 0)
        {
            stack.Children.Add(BuildPreview());
        }

        // Format; HEIC needs the HEIF extension's encoder (WIC), so it is offered only when present.
        var formats = new List<(ImageOutputFormat, string)> { (ImageOutputFormat.Jpeg, "JPEG"), (ImageOutputFormat.Png, "PNG"), (ImageOutputFormat.WebP, "WebP") };
        if (_service.CanEncodeHeic || options.Format == ImageOutputFormat.Heic)
        {
            formats.Add((ImageOutputFormat.Heic, "HEIC"));
        }

        formats.Add((ImageOutputFormat.Pdf, "PDF"));
        stack.Children.Add(LabeledRow(L.Get("Strings.mediaFormat"), Combo(formats, options.Format, v => _settings.Set(MediaSettings.ImageFormat, MediaImageFormats.StorageValue(v)))));
        if (!_service.CanEncodeHeic)
        {
            stack.Children.Add(Caption(L.Get(options.Format == ImageOutputFormat.Heic ? "win.mediaTools.heicMissing" : "win.mediaTools.heicHint"),
                options.Format == ImageOutputFormat.Heic ? "WarningBrush" : null));
        }

        if (options.Format != ImageOutputFormat.Png)
        {
            stack.Children.Add(CompressionControl(MediaSettings.ImageQuality));
        }

        // Resize
        var kinds = new List<(ImageResizeKind, string)>
        {
            (ImageResizeKind.None, L.Get("MediaImageConverterStrings.resizeNone")),
            (ImageResizeKind.MaxDimension, L.Get("MediaImageConverterStrings.resizeMax")),
            (ImageResizeKind.Width, L.Get("MediaImageConverterStrings.resizeWidth")),
            (ImageResizeKind.Height, L.Get("MediaImageConverterStrings.resizeHeight")),
            (ImageResizeKind.Exact, L.Get("MediaImageConverterStrings.resizeExact")),
        };
        stack.Children.Add(LabeledRow(L.Get("MediaImageConverterStrings.resize"), Combo(kinds, options.Resize.Kind, v => _settings.Set(MediaSettings.ImageResizeKind, MediaImageFormats.ResizeStorage(v)))));
        switch (options.Resize.Kind)
        {
            case ImageResizeKind.MaxDimension:
                stack.Children.Add(LabeledRow(L.Get("Strings.mediaMaxSize"), IntField(MediaSettings.ImageMaxDimension, 64, 20_000, 128, " px")));
                break;
            case ImageResizeKind.Width:
                stack.Children.Add(LabeledRow(L.Get("Strings.mediaWidth"), IntField(MediaSettings.ImageResizeWidth, 1, 20_000, 64, " px")));
                break;
            case ImageResizeKind.Height:
                stack.Children.Add(LabeledRow(L.Get("MediaImageConverterStrings.height"), IntField(MediaSettings.ImageResizeHeight, 1, 20_000, 64, " px")));
                break;
            case ImageResizeKind.Exact:
                stack.Children.Add(LabeledRow(L.Get("Strings.mediaWidth"), IntField(MediaSettings.ImageResizeWidth, 1, 20_000, 64, " px")));
                stack.Children.Add(LabeledRow(L.Get("MediaImageConverterStrings.height"), IntField(MediaSettings.ImageResizeHeight, 1, 20_000, 64, " px")));
                stack.Children.Add(LabeledRow(string.Empty, Segmented(
                    [
                        (ExactResizeMode.Stretch, L.Get("MediaImageConverterStrings.exactStretch")),
                        (ExactResizeMode.Fit, L.Get("MediaImageConverterStrings.exactFit")),
                        (ExactResizeMode.Fill, L.Get("MediaImageConverterStrings.exactFill")),
                    ],
                    options.Resize.ExactMode,
                    v => _settings.Set(MediaSettings.ImageExactMode, MediaImageFormats.ExactStorage(v)),
                    width: _compact ? 200 : 260)));
                break;
        }

        stack.Children.Add(LabeledRow(L.Get("MediaImageConverterStrings.background"), Combo(
            [
                (ImageBackground.Transparent, L.Get("MediaImageConverterStrings.backgroundTransparent")),
                (ImageBackground.White, L.Get("MediaImageConverterStrings.backgroundWhite")),
                (ImageBackground.Black, L.Get("MediaImageConverterStrings.backgroundBlack")),
            ],
            options.Background,
            v => _settings.Set(MediaSettings.ImageBackground, MediaImageFormats.BackgroundStorage(v)))));

        if (options.Format != ImageOutputFormat.Pdf)
        {
            stack.Children.Add(Check(MediaSettings.ImageStripMetadata, L.Get("Strings.mediaStripMetadata")));
        }

        stack.Children.Add(Check(MediaSettings.ImagePreserveModificationDate, L.Get("MediaImageConverterStrings.preserveDate")));
        stack.Children.Add(LabeledRow(L.Get("MediaImageConverterStrings.rename"), RenameField()));
        stack.Children.Add(BuildWatermark(options.Watermark));
        stack.Children.Add(BuildMoreOptions());
        return OptionsCard(stack);
    }

    private Control BuildWatermark(WatermarkOptions watermark)
    {
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(new Border { Classes = { "separator" }, Height = 1, Margin = new Thickness(0, 2) });
        stack.Children.Add(LabeledRow(L.Get("MediaImageConverterStrings.watermark"), Combo(
            [
                (WatermarkKind.Off, L.Get("MediaImageConverterStrings.watermarkOff")),
                (WatermarkKind.Text, L.Get("MediaImageConverterStrings.watermarkText")),
                (WatermarkKind.Logo, L.Get("MediaImageConverterStrings.watermarkLogo")),
                (WatermarkKind.TextAndLogo, L.Get("MediaImageConverterStrings.watermarkBoth")),
            ],
            watermark.Kind,
            v => _settings.Set(MediaSettings.WatermarkKind, MediaImageFormats.WatermarkKindStorage(v)))));
        if (watermark.Kind == WatermarkKind.Off)
        {
            return stack;
        }

        if (watermark.Kind is WatermarkKind.Text or WatermarkKind.TextAndLogo)
        {
            var text = new TextBox { Text = watermark.Text, PlaceholderText = L.Get("MediaImageConverterStrings.watermarkTextPlaceholder"), MaxLength = 500 };
            AutomationProperties.SetName(text, L.Get("MediaImageConverterStrings.watermarkTextPlaceholder"));
            text.TextChanged += (_, _) => _settings.Set(MediaSettings.WatermarkText, text.Text ?? string.Empty);
            stack.Children.Add(text);
        }

        if (watermark.Kind is WatermarkKind.Logo or WatermarkKind.TextAndLogo)
        {
            stack.Children.Add(LogoRow(watermark.LogoPath));
        }

        stack.Children.Add(LabeledRow(L.Get("MediaImageConverterStrings.position"), Combo(
            [
                (WatermarkPosition.TopLeft, L.Get("MediaImageConverterStrings.topLeft")),
                (WatermarkPosition.TopRight, L.Get("MediaImageConverterStrings.topRight")),
                (WatermarkPosition.Center, L.Get("MediaImageConverterStrings.center")),
                (WatermarkPosition.BottomLeft, L.Get("MediaImageConverterStrings.bottomLeft")),
                (WatermarkPosition.BottomRight, L.Get("MediaImageConverterStrings.bottomRight")),
            ],
            watermark.Position,
            v => _settings.Set(MediaSettings.WatermarkPosition, MediaImageFormats.PositionStorage(v)))));
        stack.Children.Add(LabeledRow(L.Get("MediaImageConverterStrings.opacity"), PercentSlider(MediaSettings.WatermarkOpacity, 0.1, 1, 0.05)));
        stack.Children.Add(LabeledRow(L.Get("MediaImageConverterStrings.margin"), IntField(MediaSettings.WatermarkMargin, 0, 2000, 8, " px")));
        if (watermark.Kind is WatermarkKind.Logo or WatermarkKind.TextAndLogo)
        {
            stack.Children.Add(LabeledRow(L.Get("MediaImageConverterStrings.scale"), PercentSlider(MediaSettings.WatermarkScale, 0.05, 0.8, 0.01)));
        }

        return stack;
    }

    private Control LogoRow(string path)
    {
        var name = new TextBlock
        {
            Text = path.Length == 0 ? L.Get("MediaImageConverterStrings.noLogo") : Path.GetFileName(path),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(name, path.Length == 0 ? null : path);
        var clear = new Button { Classes = { "icon" }, Content = new SymbolIcon { Symbol = Symbol.Dismiss, FontSize = 12 }, IsVisible = path.Length > 0 };
        ToolTip.SetTip(clear, L.Get("Strings.mediaCancel"));
        AutomationProperties.SetName(clear, L.Get("Strings.mediaCancel"));
        clear.Click += (_, _) =>
        {
            _settings.Set(MediaSettings.WatermarkLogoPath, string.Empty);
            ForceRebuild();
        };
        var choose = SmallButton(Symbol.ImageAdd, L.Get("MediaImageConverterStrings.chooseLogo"), () => _ = ChooseLogoAsync());
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 6 };
        grid.Children.Add(name);
        Grid.SetColumn(clear, 1);
        grid.Children.Add(clear);
        Grid.SetColumn(choose, 2);
        grid.Children.Add(choose);
        return grid;
    }

    private async Task ChooseLogoAsync()
    {
        if (TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            Title = L.Get("MediaImageConverterStrings.chooseLogo"),
            FileTypeFilter = [new FilePickerFileType(L.Get("Strings.mediaToolImage")) { Patterns = MediaImageFormats.ImageInputExtensions.Select(x => "*." + x).ToList() }],
        }).ConfigureAwait(true);
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path)
        {
            _settings.Set(MediaSettings.WatermarkLogoPath, path);
            ForceRebuild();
        }
    }

    private Control RenameField()
    {
        var box = new TextBox { Text = _settings.Get(MediaSettings.ImageRenamePattern), PlaceholderText = "{name}-{index:03}", MaxLength = 255 };
        AutomationProperties.SetName(box, L.Get("MediaImageConverterStrings.rename"));
        box.TextChanged += (_, _) => _settings.Set(MediaSettings.ImageRenamePattern, box.Text ?? string.Empty);
        var tokens = new Button { Content = "{…}", Padding = new Thickness(8, 4) };
        ToolTip.SetTip(tokens, L.Get("win.mediaTools.insertToken"));
        AutomationProperties.SetName(tokens, L.Get("win.mediaTools.insertToken"));
        var flyout = new MenuFlyout();
        foreach (var token in MediaNaming.Tokens)
        {
            var item = new MenuItem { Header = token };
            item.Click += (_, _) =>
            {
                var text = box.Text ?? string.Empty;
                var caret = Math.Clamp(box.CaretIndex, 0, text.Length);
                box.Text = text.Insert(caret, token);
                box.CaretIndex = caret + token.Length;
                box.Focus();
            };
            flyout.Items.Add(item);
        }

        tokens.Flyout = flyout;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 6, MinWidth = _compact ? 170 : 260 };
        grid.Children.Add(box);
        Grid.SetColumn(tokens, 1);
        grid.Children.Add(tokens);
        return grid;
    }

    // ── Profiles ("More options") ───────────────────────────────────────

    private Control BuildMoreOptions()
    {
        var expander = new Expander
        {
            Header = L.Get("MediaImageConverterStrings.moreOptions"),
            IsExpanded = _moreOptionsOpen,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        expander.Expanded += (_, _) => _moreOptionsOpen = true;
        expander.Collapsed += (_, _) => _moreOptionsOpen = false;

        var profiles = _service.Profiles();
        var selected = _service.SelectedProfile();
        var choices = new List<(string?, string)> { (null, L.Get("MediaImageConverterStrings.noProfile")) };
        choices.AddRange(profiles.Select(p => ((string?)p.Id, p.Name)));
        var picker = Combo(choices, selected?.Id, id => _service.SelectProfile(id));
        _profileDelete = new Button { Classes = { "icon" }, Content = new SymbolIcon { Symbol = Symbol.Delete, FontSize = 14 }, IsEnabled = selected is not null };
        ToolTip.SetTip(_profileDelete, L.Get("MediaImageConverterStrings.deleteProfile"));
        AutomationProperties.SetName(_profileDelete, L.Get("MediaImageConverterStrings.deleteProfile"));
        _profileDelete.Click += (_, _) =>
        {
            _service.DeleteSelectedProfile();
            ForceRebuild();
        };
        _profileModified = new TextBlock { Text = L.Get("MediaImageConverterStrings.profileModified"), Classes = { "caption" }, VerticalAlignment = VerticalAlignment.Center, IsVisible = false };
        var pickerRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { picker, _profileDelete, _profileModified } };

        _profileName = new TextBox { PlaceholderText = L.Get("MediaImageConverterStrings.profileName"), MaxLength = 60, MinWidth = 140 };
        AutomationProperties.SetName(_profileName, L.Get("MediaImageConverterStrings.profileName"));
        var update = new Button { Content = L.Get("MediaImageConverterStrings.updateProfile"), IsEnabled = selected is not null };
        update.Click += (_, _) =>
        {
            _service.UpdateSelectedProfile(_profileName.Text);
            ForceRebuild();
        };
        var saveNew = new Button { Content = L.Get("MediaImageConverterStrings.saveAsNew") };
        saveNew.Click += (_, _) =>
        {
            _service.SaveNewProfile(_profileName.Text);
            ForceRebuild();
        };
        var nameRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 6 };
        nameRow.Children.Add(_profileName);
        Grid.SetColumn(update, 1);
        nameRow.Children.Add(update);
        Grid.SetColumn(saveNew, 2);
        nameRow.Children.Add(saveNew);

        expander.Content = new StackPanel
        {
            Spacing = 8,
            Children = { LabeledRow(L.Get("MediaImageConverterStrings.profile"), pickerRow), nameRow },
        };
        return expander;
    }

    private void RefreshProfileState()
    {
        if (_profileModified is null || _service.Tool != MediaTool.Image)
        {
            return;
        }

        var selected = _service.SelectedProfile();
        _profileModified.IsVisible = selected is not null && _service.SelectedProfileModified();
        if (_profileDelete is not null)
        {
            _profileDelete.IsEnabled = selected is not null;
        }
    }

    // ── Preview ─────────────────────────────────────────────────────────

    private Control BuildPreview()
    {
        Detach(_previewFrame, _previewName);
        _previewFrame.Child = _previewImage;
        var label = new TextBlock { Text = L.Get("MediaImageConverterStrings.preview"), FontWeight = FontWeight.SemiBold, FontSize = 12 };
        var texts = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center, Children = { label, _previewName } };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 10 };
        var box = new Border
        {
            Width = _compact ? 108 : 136,
            Height = _compact ? 74 : 92,
            Child = _previewFrame,
        };
        grid.Children.Add(box);
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);
        return grid;
    }

    /// <summary>Re-renders the preview shortly after the last option change, off the UI thread.</summary>
    private void SchedulePreview()
    {
        _previewCancel?.Cancel();
        if (_service.Tool != MediaTool.Image || _service.Inputs.Count == 0)
        {
            _previewImage.Source = null;
            return;
        }

        var cancel = _previewCancel = new CancellationTokenSource();
        var input = _service.Inputs[0];
        var options = _service.CurrentImageOptions();
        var output = _service.Output;
        _previewName.Text = L.Format("win.mediaTools.previewOutputFormat", output is null ? L.Get("Strings.mediaOutputAutomatic") : Path.GetFileName(output.TrimEnd('\\', '/')));
        var background = MediaImageFormats.EffectiveBackground(options.Format, options.Background);
        _previewFrame.Background = background switch
        {
            ImageBackground.White => Brushes.White,
            ImageBackground.Black => Brushes.Black,
            _ => this.FindResource(ActualThemeVariant, "ChipBrush") as IBrush,
        };
        var maxSide = (_compact ? 96 : 128) * 2;
        var hooks = _service.Hooks;
        _ = Task.Run(async () =>
        {
            await Task.Delay(PreviewDelay, cancel.Token).ConfigureAwait(false);
            using var logo = options.Watermark.HasLogo ? MediaImageProcessor.LoadLogo(options.Watermark.LogoPath, hooks) : null;
            using var image = MediaImageProcessor.Preview(input, options, logo, maxSide, hooks);
            if (image is null || cancel.IsCancellationRequested)
            {
                return;
            }

            var bitmap = ImageInterop.ToBitmap(image);
            var aspect = image.Width / (double)Math.Max(1, image.Height);
            Dispatcher.UIThread.Post(() =>
            {
                if (cancel.IsCancellationRequested)
                {
                    bitmap.Dispose();
                    return;
                }

                (_previewImage.Source as Bitmap)?.Dispose();
                _previewImage.Source = bitmap;

                // The frame takes the output's aspect inside the preview box.
                var boxWidth = _compact ? 108.0 : 136.0;
                var boxHeight = _compact ? 74.0 : 92.0;
                var width = Math.Min(boxWidth, boxHeight * aspect);
                _previewFrame.Width = width;
                _previewFrame.Height = width / aspect;
            });
        }, cancel.Token).ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
    }

    // ── Shared pieces ───────────────────────────────────────────────────

    private StackPanel OptionsStack() => new() { Spacing = 9 };

    private static Border OptionsCard(Control content) => new() { Classes = { "card" }, Child = content };

    private Control LabeledRow(string label, Control control)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(_compact ? "86,*" : "130,*"), ColumnSpacing = 8 };
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, FontSize = _compact ? 12 : 13 };
        grid.Children.Add(text);
        control.HorizontalAlignment = control.HorizontalAlignment == HorizontalAlignment.Stretch ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return grid;
    }

    private Control TrimRow(Setting<double> start, Setting<double> end) => new StackPanel
    {
        Spacing = 8,
        Children =
        {
            LabeledRow(L.Get("Strings.mediaStartTime"), TimeField(start, L.Get("Strings.mediaStartTime"))),
            LabeledRow(L.Get("Strings.mediaEndTime"), TimeField(end, L.Get("Strings.mediaEndTime"))),
        },
    };

    /// <summary>Seconds with one decimal, minimum 0 (spec 07 §3.6.4).</summary>
    private Control TimeField(Setting<double> setting, string label)
    {
        var field = new NumericUpDown
        {
            Minimum = 0,
            Maximum = _service.VideoInfo is { } info ? (decimal)Math.Max(0.1, Math.Round(info.Duration, 1)) : 86_400m,
            Increment = 0.1m,
            FormatString = "0.0",
            Value = (decimal)Math.Round(_settings.Get(setting), 1),
            Width = _compact ? 130 : 160,
        };
        AutomationProperties.SetName(field, label);
        field.ValueChanged += (_, e) => _settings.Set(setting, (double)(e.NewValue ?? 0));
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children = { field, new TextBlock { Text = "s", Classes = { "caption" }, VerticalAlignment = VerticalAlignment.Center } },
        };
    }

    private Control SizingPicker(Setting<string> setting, MediaSizingMode current) => Segmented(
        [(MediaSizingMode.Resolution, L.Get("Strings.mediaSizingResolution")), (MediaSizingMode.TargetSize, L.Get("Strings.mediaSizingFileSize"))],
        current,
        mode =>
        {
            _settings.Set(setting, MediaSettings.ToStorage(mode));
            ForceRebuild();
        },
        stretch: true);

    private Control TargetSizeRow(Setting<int> setting)
    {
        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(LabeledRow(L.Get("Strings.mediaTargetSize"), IntField(setting, 1, 512, 1, L.Get("Strings.mediaMegabytesSuffix"))));
        stack.Children.Add(Caption(L.Get("Strings.mediaTargetSizeHint")));
        return stack;
    }

    /// <summary>"Compression": Low / Medium / High set 0.88 / 0.68 / 0.28; the nearest level is highlighted.</summary>
    private Control CompressionControl(Setting<double> setting)
    {
        var current = MediaCompression.Nearest(_settings.Get(setting));
        var description = Caption(L.Get(MediaCompression.DescriptionKey(current)));
        var picker = Segmented(
            MediaCompression.Levels.Select(l => (l, L.Get(MediaCompression.TitleKey(l)))).ToList(),
            current,
            level =>
            {
                _settings.Set(setting, level);
                description.Text = L.Get(MediaCompression.DescriptionKey(level));
            },
            stretch: true,
            height: _compact ? 28 : 32);
        return new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock { Text = L.Get("Strings.mediaQuality"), FontSize = _compact ? 12 : 13 },
                picker,
                description,
            },
        };
    }

    private Control Segmented<T>(IReadOnlyList<(T Value, string Label)> options, T current, Action<T> changed, bool stretch = false, double? width = null, double? height = null)
    {
        var grid = new UniformGrid { Rows = 1 };
        var group = "seg" + Guid.NewGuid().ToString("N");
        foreach (var (value, label) in options)
        {
            var button = new RadioButton
            {
                Classes = { "tab" },
                GroupName = group,
                IsChecked = EqualityComparer<T>.Default.Equals(value, current),
                Content = new TextBlock { Text = label, FontSize = _compact ? 12 : 13, TextTrimming = TextTrimming.CharacterEllipsis },
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
            };
            if (height is { } h)
            {
                button.MinHeight = h;
            }

            button.IsCheckedChanged += (_, _) =>
            {
                if (button.IsChecked == true)
                {
                    changed(value);
                }
            };
            grid.Children.Add(button);
        }

        var frame = new Border { Classes = { "card" }, Padding = new Thickness(2), Child = grid };
        if (width is { } w)
        {
            frame.Width = w;
        }

        frame.HorizontalAlignment = stretch ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        return frame;
    }

    private ComboBox Combo<T>(IReadOnlyList<(T Value, string Label)> options, T current, Action<T> changed)
    {
        var combo = new ComboBox
        {
            ItemsSource = options.Select(o => o.Label).ToList(),
            MinWidth = _compact ? 150 : 200,
            SelectedIndex = Math.Max(0, options.Select(o => o.Value).ToList().FindIndex(v => EqualityComparer<T>.Default.Equals(v, current))),
        };
        combo.SelectionChanged += (_, _) =>
        {
            var index = combo.SelectedIndex;
            if (index >= 0 && index < options.Count && !EqualityComparer<T>.Default.Equals(options[index].Value, current))
            {
                current = options[index].Value;
                changed(current);
            }
        };
        return combo;
    }

    private NumericUpDown IntField(Setting<int> setting, int min, int max, int step, string suffix)
    {
        var field = new NumericUpDown
        {
            Minimum = min,
            Maximum = max,
            Increment = step,
            FormatString = "0",
            Value = _settings.Get(setting),
            Width = _compact ? 130 : 160,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetName(field, suffix.Trim());
        field.ValueChanged += (_, e) =>
        {
            if (e.NewValue is { } value)
            {
                _settings.Set(setting, (int)Math.Round(value));
            }
        };
        ToolTip.SetTip(field, $"{min}–{max}{suffix}");
        return field;
    }

    private Control PercentSlider(Setting<double> setting, double min, double max, double step)
    {
        var value = _settings.Get(setting);
        var label = new TextBlock { Text = $"{value * 100:0}%", Width = 40, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        var slider = new Slider { Minimum = min, Maximum = max, SmallChange = step, LargeChange = step, TickFrequency = step, IsSnapToTickEnabled = true, Value = value, Width = _compact ? 140 : 200 };
        slider.ValueChanged += (_, e) =>
        {
            label.Text = $"{e.NewValue * 100:0}%";
            _settings.Set(setting, e.NewValue);
        };
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { slider, label } };
    }

    private CheckBox Check(Setting<bool> setting, string label)
    {
        var box = new CheckBox { Content = label, IsChecked = _settings.Get(setting) };
        box.IsCheckedChanged += (_, _) => _settings.Set(setting, box.IsChecked == true);
        return box;
    }
}
