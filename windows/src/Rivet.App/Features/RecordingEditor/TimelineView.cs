// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Rivet.Core.Localization;
using Rivet.Core.RecordingEditor;

namespace Rivet.App.Features.RecordingEditor;

/// <summary>
/// The bottom band (spec 02 §3.20.5): the transport row, then labelled rows
/// sharing one horizontal mapping over the whole recording in source time —
/// click ruler + zoom lane, captions, images, blurs, audio tracks and the filmstrip.
/// </summary>
public sealed class TimelineView : Border
{
    // 64 on macOS; "System sound" needs a little more room than "Mac sound".
    private const double LabelWidth = 76;
    private readonly EditorSession _session;
    private readonly Func<double, Task> _addImage;
    private readonly Button _play;
    private readonly TextBlock _time;
    private readonly Button _addText;
    private readonly Button _addImageButton;
    private readonly Button _addBlur;
    private readonly Button _cut;
    private readonly TextBlock _cutLabel;
    private readonly Button _speed;
    private readonly TextBlock _speedLabel;
    private readonly Button _quality;
    private readonly TextBlock _qualityLabel;
    private readonly ClickRuler _ruler;
    private readonly LaneControl _zoomLane;
    private readonly LaneControl _textLane;
    private readonly LaneControl _imageLane;
    private readonly LaneControl _blurLane;
    private readonly Filmstrip _filmstrip;
    private readonly WaveformView? _systemWave;
    private readonly WaveformView? _micWave;
    private readonly Control _textRow;
    private readonly Control _imageRow;
    private readonly Control _blurRow;
    private readonly AudioControls? _systemControls;
    private readonly AudioControls? _micControls;
    private readonly DispatcherTimer _timer;
    private double _lastTime = -1;

    public TimelineView(EditorSession session, Func<double, Task> addImage)
    {
        _session = session;
        _addImage = addImage;
        this.Themed(BackgroundProperty, "EditorBandBrush");
        this.Themed(BorderBrushProperty, "EditorHairlineBrush");
        BorderThickness = new Thickness(0, 1, 0, 0);
        Padding = new Thickness(18, 10, 18, 16);

        _play = EditorUi.Button(null, "Play", "Space", session.TogglePlay);
        _play.Width = 34;
        _time = new TextBlock
        {
            FontSize = 12,
            FontWeight = FontWeight.Medium,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, monospace"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 10, 0),
            MinWidth = 96,
        }.Themed(TextBlock.ForegroundProperty, "EditorSecondaryTextBrush");
        _addText = EditorUi.Button(L.Get("recorder.addTextButton"), "TextAdd", null, () => session.AddTextAt(session.PlayheadSource));
        _addImageButton = EditorUi.Button(L.Get("recorder.addImageButton"), "ImageAdd", null, () => _ = _addImage(session.PlayheadSource));
        _addBlur = EditorUi.Button(L.Get("recorder.addBlurButton"), "Blur", null, () => session.AddBlurAt(session.PlayheadSource));
        _cutLabel = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        _cut = new Button
        {
            Classes = { "editor", "cut" },
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { EditorUi.Icon("Cut", 14), _cutLabel } },
            IsVisible = false,
        };
        _cut.Click += (_, _) => session.CutOut();
        _speedLabel = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        _speed = new Button
        {
            Classes = { "plain" },
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Children = { EditorUi.Icon("Gauge", 14), _speedLabel } },
        };
        ToolTip.SetTip(_speed, L.Get("recorderExport.speed"));
        AutomationProperties.SetName(_speed, L.Get("recorderExport.speed"));
        _speed.Click += (_, _) => SpeedPopover.Show(_speed, session);
        _qualityLabel = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        _quality = new Button
        {
            Classes = { "plain" },
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { _qualityLabel, EditorUi.Icon("ChevronDown", 11) } },
        };
        ToolTip.SetTip(_quality, L.Get("recorder.qualityLabel"));
        AutomationProperties.SetName(_quality, L.Get("recorder.qualityLabel"));
        _quality.Click += (_, _) => ShowQualityMenu();

        var transport = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 4) };
        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { _play, _time, _addText, _addImageButton, _addBlur, _cut } };
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _speed, _quality } };
        DockPanel.SetDock(left, Dock.Left);
        DockPanel.SetDock(right, Dock.Right);
        transport.Children.Add(left);
        transport.Children.Add(right);

        _ruler = new ClickRuler(session);
        _zoomLane = new LaneControl(session, LaneKind.Zoom);
        _textLane = new LaneControl(session, LaneKind.Text);
        _imageLane = new LaneControl(session, LaneKind.Image, addImage);
        _blurLane = new LaneControl(session, LaneKind.Blur);
        _filmstrip = new Filmstrip(session);

        var rows = new StackPanel { Spacing = 6 };
        rows.Children.Add(transport);
        rows.Children.Add(Row(L.Get("recorder.zoomSectionLabel"), new StackPanel { Spacing = 2, Children = { _ruler, _zoomLane } }, labelOffset: 7));
        _textRow = Row(L.Get("recorder.textContentLabel"), _textLane);
        _imageRow = Row(L.Get("recorder.imageLaneLabel"), _imageLane);
        _blurRow = Row(L.Get("recorder.blurLaneLabel"), _blurLane);
        rows.Children.Add(_textRow);
        rows.Children.Add(_imageRow);
        rows.Children.Add(_blurRow);
        if (session.HasSystemAudio)
        {
            _systemWave = new WaveformView(session, microphone: false);
            _systemControls = new AudioControls(session, microphone: false);
            rows.Children.Add(Row(L.Get("recorder.systemAudioTrackLabel"), AudioRow(_systemWave, _systemControls)));
        }

        if (session.HasMicrophone)
        {
            _micWave = new WaveformView(session, microphone: true);
            _micControls = new AudioControls(session, microphone: true);
            rows.Children.Add(Row(L.Get("recorder.microphoneTrackLabel"), AudioRow(_micWave, _micControls)));
        }

        rows.Children.Add(Row(L.Get("recorder.editorTitle"), _filmstrip));
        Child = rows;

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Background, (_, _) => Tick());
        session.Changed += OnChanged;
        session.Playback.StateChanged += Refresh;
        Refresh();
        _timer.Start();
    }

    private static Grid Row(string label, Control content, double labelOffset = 0)
    {
        var text = new TextBlock { Text = label, Classes = { "rowLabel" }, Margin = new Thickness(0, labelOffset, 0, 0) };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions($"{LabelWidth},*"), ColumnSpacing = 10, Children = { text, content } };
        Grid.SetColumn(content, 1);
        return grid;
    }

    private static Grid AudioRow(WaveformView wave, AudioControls controls)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 10, Children = { wave, controls } };
        Grid.SetColumn(controls, 1);
        return grid;
    }

    private void OnChanged(SessionChange change)
    {
        if ((change & SessionChange.Media) != 0)
        {
            _filmstrip.RefreshThumbnails();
            _systemWave?.InvalidateVisual();
            _micWave?.InvalidateVisual();
        }

        if ((change & (SessionChange.Document | SessionChange.Selection | SessionChange.Export | SessionChange.Mode)) != 0)
        {
            Refresh();
        }
    }

    private void Tick()
    {
        var t = _session.Playback.OutputTime;
        if (Math.Abs(t - _lastTime) < 0.0005)
        {
            return;
        }

        _lastTime = t;
        UpdateTime();
        InvalidateLanes();
    }

    private void InvalidateLanes()
    {
        _ruler.InvalidateVisual();
        _zoomLane.InvalidateVisual();
        _textLane.InvalidateVisual();
        _imageLane.InvalidateVisual();
        _blurLane.InvalidateVisual();
        _filmstrip.InvalidateVisual();
        _systemWave?.InvalidateVisual();
        _micWave?.InvalidateVisual();
    }

    private void UpdateTime()
    {
        var output = _session.Timeline.OutputDuration;
        _time.Text = $"{RecordingNames.Elapsed(Math.Floor(_session.Playback.OutputTime))} / {RecordingNames.Elapsed(Math.Round(output, MidpointRounding.AwayFromZero))}";
    }

    public void Refresh()
    {
        var doc = _session.Document;
        var ready = _session.IsReady;
        var exporting = _session.IsExporting;
        _play.Content = EditorUi.Icon(_session.Playback.IsPlaying ? "Pause" : "Play");
        AutomationProperties.SetName(_play, L.Get(_session.Playback.IsPlaying ? "Strings.actionPause" : "Strings.actionPlay"));
        _play.IsEnabled = ready;
        _addText.IsEnabled = ready;
        _addImageButton.IsEnabled = ready;
        _addBlur.IsEnabled = ready;
        _textRow.IsVisible = doc.Texts.Count > 0;
        _imageRow.IsVisible = doc.Images.Count > 0;
        _blurRow.IsVisible = doc.Blurs.Count > 0;

        _cut.IsVisible = _session.CutSelection is not null;
        if (_session.CutSelection is { } selection)
        {
            _cutLabel.Text = $"{L.Get("recorder.cutOutButton")}  {RecordingNames.Elapsed(Math.Round(selection.Length, MidpointRounding.AwayFromZero))}";
            _cut.IsEnabled = _session.CanCutOut;
        }

        _speedLabel.Text = RecordingNames.Speed(doc.ExportSpeed, CultureInfo.CurrentUICulture);
        _speed.IsEnabled = ready && !exporting;
        var (w, h) = _session.ExportSize(doc.Quality);
        _qualityLabel.Text = $"{QualityName(doc.Quality)}  {w} × {h}";
        _quality.IsEnabled = ready;
        _systemControls?.Refresh();
        _micControls?.Refresh();
        UpdateTime();
        InvalidateLanes();
    }

    public static string QualityName(ExportQuality quality) => quality switch
    {
        ExportQuality.Small => L.Get("recorder.qualitySmall"),
        ExportQuality.High => L.Get("recorder.qualityHigh"),
        _ => L.Get("recorder.qualityBalanced"),
    };

    private void ShowQualityMenu()
    {
        var menu = new MenuFlyout { Placement = PlacementMode.TopEdgeAlignedRight };
        foreach (var quality in new[] { ExportQuality.Small, ExportQuality.Balanced, ExportQuality.High })
        {
            var (w, h) = _session.ExportSize(quality);
            var item = new MenuItem
            {
                Header = $"{QualityName(quality)}   {w} × {h}",
                ToggleType = MenuItemToggleType.Radio,
                IsChecked = _session.Document.Quality == quality,
            };
            item.Click += (_, _) => _session.SetQuality(quality);
            menu.Items.Add(item);
        }

        menu.ShowAt(_quality);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer.Stop();
        _session.Changed -= OnChanged;
        _session.Playback.StateChanged -= Refresh;
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>Volume slider with a percentage and Remove/Restore, beside an audio lane.</summary>
    private sealed class AudioControls : StackPanel
    {
        private readonly EditorSession _session;
        private readonly bool _microphone;
        private readonly Slider _volume;
        private readonly TextBlock _percent;
        private readonly Button _toggle;
        private bool _updating;
        private bool _dragging;

        public AudioControls(EditorSession session, bool microphone)
        {
            _session = session;
            _microphone = microphone;
            Orientation = Orientation.Horizontal;
            Spacing = 8;
            VerticalAlignment = VerticalAlignment.Center;
            _volume = new Slider { Minimum = 0, Maximum = 1, Width = 92, SmallChange = 0.01, LargeChange = 0.1, VerticalAlignment = VerticalAlignment.Center, Classes = { "inspector" } };
            ToolTip.SetTip(_volume, L.Get("recorder.audioVolumeLabel"));
            AutomationProperties.SetName(_volume, L.Get("recorder.audioVolumeLabel"));
            _percent = new TextBlock { Width = 36, Classes = { "sliderValue" }, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right };
            _toggle = new Button { Classes = { "editor" }, MinWidth = 96 };
            _toggle.Click += (_, _) =>
            {
                var keep = microphone ? session.Document.KeepsMicrophone : session.Document.KeepsSystemAudio;
                if (microphone)
                {
                    session.SetKeepsMicrophone(!keep);
                }
                else
                {
                    session.SetKeepsSystemAudio(!keep);
                }
            };
            _volume.AddHandler(PointerPressedEvent, (_, _) =>
            {
                _dragging = true;
                session.BeginInteraction();
            }, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
            _volume.AddHandler(PointerReleasedEvent, (_, _) => End(), Avalonia.Interactivity.RoutingStrategies.Tunnel | Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
            _volume.AddHandler(PointerCaptureLostEvent, (_, _) => End(), Avalonia.Interactivity.RoutingStrategies.Tunnel | Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
            _volume.ValueChanged += (_, e) =>
            {
                _percent.Text = $"{Math.Round(e.NewValue * 100, MidpointRounding.AwayFromZero):0}%";
                if (_updating)
                {
                    return;
                }

                if (!_dragging)
                {
                    session.BeginInteraction();
                }

                if (microphone)
                {
                    session.SetMicrophoneGain(e.NewValue);
                }
                else
                {
                    session.SetSystemGain(e.NewValue);
                }

                if (!_dragging)
                {
                    session.CommitInteraction();
                }
            };
            Children.Add(_volume);
            Children.Add(_percent);
            Children.Add(_toggle);
        }

        private void End()
        {
            if (_dragging)
            {
                _dragging = false;
                _session.CommitInteraction();
            }
        }

        public void Refresh()
        {
            var doc = _session.Document;
            var keep = _microphone ? doc.KeepsMicrophone : doc.KeepsSystemAudio;
            var gain = _microphone ? doc.MicrophoneGain : doc.SystemAudioGain;
            if (!_dragging)
            {
                _updating = true;
                _volume.Value = gain;
                _updating = false;
            }

            _percent.Text = $"{Math.Round(gain * 100, MidpointRounding.AwayFromZero):0}%";
            _volume.IsEnabled = keep;
            _toggle.Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    EditorUi.Icon(keep ? "Dismiss" : "ArrowUndo", 13),
                    new TextBlock { Text = L.Get(keep ? "recorder.removeAudio" : "recorder.restoreAudio"), VerticalAlignment = VerticalAlignment.Center },
                },
            };
            if (!keep)
            {
                _toggle.Foreground = new SolidColorBrush(EditorPalette.SystemBlue);
            }
            else
            {
                _toggle.ClearValue(TemplatedControl.ForegroundProperty);
            }
        }
    }
}
