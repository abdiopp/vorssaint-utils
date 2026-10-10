// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Extensions.DependencyInjection;
using Rivet.Core.App;
using Rivet.Core.Contracts;
using Rivet.Core.Diagnostics;
using Rivet.Core.Platform;
using Rivet.Core.RecordingEditor;
using Rivet.Core.Settings;
using Avalonia.Threading;
using Rivet.Core.Util;

namespace Rivet.App.Features.RecordingEditor;

/// <summary>Which lane item is selected (one at a time, across kinds).</summary>
public enum LaneKind
{
    Zoom,
    Text,
    Image,
    Blur,
}

public enum InspectorTab
{
    Look,
    Pointer,
    Zoom,
}

/// <summary>What changed, so views refresh only what they show.</summary>
[Flags]
public enum SessionChange
{
    None = 0,
    Document = 1,
    Selection = 2,
    Mode = 4,
    Export = 8,
    Presets = 16,
    Playback = 32,
    Media = 64,
    All = 127,
}

/// <summary>
/// One open recording: the document and its undo history, the selection,
/// aiming/drawing modes, the preview player and exports (spec 02 §3.20–§3.36).
/// Lives on the UI thread; heavy work runs on background threads.
/// </summary>
public sealed partial class EditorSession : IDisposable
{
    private readonly IServiceProvider _services;
    private readonly Debouncer _pictureDebounce;
    private bool _disposed;

    private EditorSession(IServiceProvider services, TakeData take, EditDocument document, double duration, int width, int height, int fps, string? loadError)
    {
        _services = services;
        Take = take;
        Duration = duration;
        SourceWidth = width;
        SourceHeight = height;
        Fps = fps;
        LoadError = loadError;
        Settings = services.GetRequiredService<ISettingsStore>();
        Paths = services.GetRequiredService<AppPaths>();
        Hud = services.GetService<IHud>();
        History = new EditHistory(document);
        History.Changed += OnDocumentChanged;
        History.Committed += Persist;
        Timeline = EditTimeline.For(document, duration);
        _pictureDebounce = new Debouncer(TimeSpan.FromMilliseconds(120), () => Dispatcher.UIThread.Post(PushPlan));
        Presets = RecordingEditorSettings.ReadPresets(Settings);
        BackdropCustoms = RecordingEditorSettings.ReadBackdropPresets(Settings);
        Playback = new PlaybackController(this, services);
    }

    public TakeData Take { get; }

    public double Duration { get; }

    public int SourceWidth { get; }

    public int SourceHeight { get; }

    /// <summary>Editor and export frame rate (30 or 60).</summary>
    public int Fps { get; }

    /// <summary>Set when the master cannot be decoded on this PC; editing is disabled.</summary>
    public string? LoadError { get; }

    public bool IsReady => LoadError is null && Duration > 0;

    public ISettingsStore Settings { get; }

    public AppPaths Paths { get; }

    public IHud? Hud { get; }

    public IServiceProvider Services => _services;

    public EditHistory History { get; }

    public EditDocument Document => History.Current;

    public EditTimeline Timeline { get; private set; }

    public PlaybackController Playback { get; }

    public bool HasPointerTrack => Take.HasPointerTrack;

    public bool HasSystemAudio => Take.SystemAudioPath is not null;

    public bool HasMicrophone => Take.MicrophoneAudioPath is not null;

    public LaneKind? SelectedKind { get; private set; }

    public string? SelectedId { get; private set; }

    /// <summary>A Shift-drag selection on the filmstrip, in source seconds.</summary>
    public TimeRange? CutSelection { get; private set; }

    public InspectorTab Tab { get; private set; } = InspectorTab.Look;

    /// <summary>Choosing a hand-aimed zoom focus: the stage shows the raw recording.</summary>
    public bool IsAiming { get; private set; }

    /// <summary>Drawing a blur area: the stage shows the raw recording.</summary>
    public bool IsDrawingBlur { get; private set; }

    /// <summary>The amount last chosen for a zoom in this editor (new zooms use it).</summary>
    public double? LastZoomAmount { get; private set; }

    public IReadOnlyList<EditPreset> Presets { get; private set; }

    public IReadOnlyList<RecorderBackdrop> BackdropCustoms { get; private set; }

    /// <summary>Raised on the UI thread after state changes.</summary>
    public event Action<SessionChange>? Changed;

    /// <summary>Raised when the window must close (Copy and delete, discard, feature removal).</summary>
    public event Action? CloseRequested;

    // ── Loading (§3.20.2) ─────────────────────────────────────────────

    /// <summary>Reads the take and probes the master off the UI thread.</summary>
    public static async Task<EditorSession> LoadAsync(string folder, IServiceProvider services)
    {
        var settings = services.GetRequiredService<ISettingsStore>();
        var factory = services.GetRequiredService<IVideoFrameSourceFactory>();
        var (take, duration, width, height, fps, error) = await Task.Run(() =>
        {
            var data = TakeData.Load(folder);
            double length = data.Manifest?.Video.DurationSeconds ?? 0;
            int w = data.Manifest?.Video.Width ?? 0, h = data.Manifest?.Video.Height ?? 0;
            double nominal = 0;
            string? failure = null;
            try
            {
                using var probe = factory.Open(data.VideoPath);
                length = probe.Duration > 0 ? probe.Duration : length;
                w = probe.VideoWidth;
                h = probe.VideoHeight;
                nominal = probe.NominalFrameRate;
            }
            catch (MediaUnavailableException ex)
            {
                failure = ex.Message;
                Log.Warn("recording-editor", "Media Foundation is unavailable.", ex);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                failure = ex.Message;
                Log.Warn("recording-editor", $"The master in {folder} cannot be decoded.", ex);
            }

            var rate = data.ManifestFrameRate > 0 ? data.ManifestFrameRate : ExportMath.SnapFrameRate(nominal);
            return (data, length, RecorderMath.EvenSide(Math.Max(32, w)), RecorderMath.EvenSide(Math.Max(32, h)), rate, failure);
        }).ConfigureAwait(true);

        var document = (take.SavedDocument ?? RecordingEditorSettings.NewTakeDocument(settings)).Sanitized(duration);
        var session = new EditorSession(services, take, document, duration, width, height, fps, error is null && duration <= 0 ? "empty" : error);
        session.GenerateZoomsOnce();
        return session;
    }

    /// <summary>Automatic zooms once per take, not an undo step, persisted right away (even when none result).</summary>
    private void GenerateZoomsOnce()
    {
        var doc = Document;
        if (!IsReady || doc.ZoomsGenerated || !doc.ZoomEnabled)
        {
            if (Take.SavedDocument is null && IsReady)
            {
                Persist(doc);
            }

            return;
        }

        var zooms = HasPointerTrack
            ? AutoZoom.Generate(Take.Pointer.Clicks, doc.ZoomsOnTyping ? Take.Typing.Times : null, Duration, doc.ZoomAmount)
            : [];
        History.ReplaceWithoutUndo(doc with { ZoomSegments = zooms, ZoomsGenerated = true });
    }

    // ── Document changes ──────────────────────────────────────────────

    /// <summary>One undoable change.</summary>
    public void Apply(EditDocument next)
    {
        if (!IsReady)
        {
            return;
        }

        History.Apply(next.Sanitized(Duration));
    }

    public void BeginInteraction()
    {
        if (IsReady)
        {
            History.BeginInteraction();
        }
    }

    /// <summary>A live value during a drag or while typing (no undo entry, not persisted yet).</summary>
    public void SetLive(EditDocument next)
    {
        if (!IsReady)
        {
            return;
        }

        if (!History.InInteraction)
        {
            History.BeginInteraction();
        }

        History.SetLive(next.Sanitized(Duration));
    }

    public void CommitInteraction() => History.CommitInteraction();

    public void Undo()
    {
        if (History.Undo())
        {
            SeekSource(Timeline.Trim.Start);
        }
    }

    public void Redo()
    {
        if (History.Redo())
        {
            SeekSource(Timeline.Trim.Start);
        }
    }

    private void OnDocumentChanged(EditDocument before, EditDocument after, EditChange change)
    {
        if ((change & EditChange.Timing) != 0)
        {
            Timeline = EditTimeline.For(after, Duration);
        }

        // A selection that no longer exists is cleared (redo never revives it).
        if (SelectedKind is { } kind && SelectedId is { } id && !Exists(after, kind, id))
        {
            SelectedKind = null;
            SelectedId = null;
            IsAiming = false;
            IsDrawingBlur = false;
        }

        if ((change & EditChange.Audio) != 0)
        {
            Playback.AudioChanged();
        }

        if ((change & EditChange.Timing) != 0)
        {
            PushPlan();
        }
        else if ((change & EditChange.Picture) != 0)
        {
            _pictureDebounce.Trigger();
        }

        Raise(SessionChange.Document | SessionChange.Selection);
    }

    private static bool Exists(EditDocument doc, LaneKind kind, string id) => kind switch
    {
        LaneKind.Zoom => doc.ZoomSegments.Any(z => z.Id == id),
        LaneKind.Text => doc.Texts.Any(t => t.Id == id),
        LaneKind.Image => doc.Images.Any(i => i.Id == id),
        _ => doc.Blurs.Any(b => b.Id == id),
    };

    /// <summary>Hands the current document to the preview (suppressed while aiming or drawing).</summary>
    private void PushPlan()
    {
        if (_disposed)
        {
            return;
        }

        Playback.UpdateDocument(Document, raw: IsAiming || IsDrawingBlur);
    }

    private void Persist(EditDocument doc)
    {
        if (_disposed || !Directory.Exists(Take.Folder))
        {
            return;
        }

        try
        {
            doc.Write(Take.Folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("recording-editor", "Could not save edit.json.", ex);
        }
    }

    public void Raise(SessionChange change) => Changed?.Invoke(change);

    // ── Selection and modes ───────────────────────────────────────────

    public void Select(LaneKind kind, string id)
    {
        if (SelectedKind == kind && SelectedId == id && CutSelection is null)
        {
            return;
        }

        EndModes();
        SelectedKind = kind;
        SelectedId = id;
        CutSelection = null;
        Raise(SessionChange.Selection | SessionChange.Mode);
    }

    public void ClearSelection()
    {
        if (SelectedKind is null && !IsAiming && !IsDrawingBlur)
        {
            return;
        }

        EndModes();
        SelectedKind = null;
        SelectedId = null;
        Raise(SessionChange.Selection | SessionChange.Mode);
    }

    public bool IsSelected(LaneKind kind) => SelectedKind == kind && SelectedId is not null;

    public void SetTab(InspectorTab tab)
    {
        if (Tab != tab)
        {
            Tab = tab;
            Raise(SessionChange.Selection);
        }
    }

    public void SetCutSelection(TimeRange? range)
    {
        CutSelection = range;
        Raise(SessionChange.Selection);
    }

    public void StartAiming()
    {
        if (SelectedKind != LaneKind.Zoom)
        {
            return;
        }

        IsDrawingBlur = false;
        IsAiming = true;
        Playback.Pause();
        PushPlan();
        Raise(SessionChange.Mode);
    }

    public void StartDrawingBlur()
    {
        if (SelectedKind != LaneKind.Blur)
        {
            return;
        }

        IsAiming = false;
        IsDrawingBlur = true;
        Playback.Pause();
        PushPlan();
        Raise(SessionChange.Mode);
    }

    /// <summary>Ends aiming or blur drawing; the edited preview returns.</summary>
    public void EndModes()
    {
        if (!IsAiming && !IsDrawingBlur)
        {
            return;
        }

        IsAiming = false;
        IsDrawingBlur = false;
        PushPlan();
        Raise(SessionChange.Mode);
    }

    // ── Playback glue ─────────────────────────────────────────────────

    /// <summary>The playhead as a source time (lanes, ruler and filmstrip use source time).</summary>
    public double PlayheadSource => Timeline.SourceTime(Playback.OutputTime);

    /// <summary>Seeks to a source moment; a removed moment goes to the nearest kept one.</summary>
    public void SeekSource(double sourceTime)
    {
        if (!IsReady)
        {
            return;
        }

        Playback.Seek(Timeline.NearestOutputTime(sourceTime));
    }

    public void TogglePlay()
    {
        if (!IsReady)
        {
            return;
        }

        EndModes();
        Playback.TogglePlay();
    }

    // ── Lifetime ──────────────────────────────────────────────────────

    public void RequestClose() => CloseRequested?.Invoke();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _pictureDebounce.Dispose();
        DisposeMedia();
        CancelExport();
        Playback.Dispose();
    }
}
