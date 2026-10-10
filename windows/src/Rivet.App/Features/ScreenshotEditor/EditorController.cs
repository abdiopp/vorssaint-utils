// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Rivet.Core.Contracts;
using Rivet.Core.Diagnostics;
using Rivet.Core.Platform;
using Rivet.Core.ScreenshotEditor;
using Rivet.Core.Settings;
using Rivet.Imaging.Backdrop;
using Rivet.Imaging.ScreenshotEditor;
using Rivet.Imaging.Skia;
using SkiaSharp;

namespace Rivet.App.Features.ScreenshotEditor;

/// <summary>
/// One editor's state beyond the document: backdrop, watermark and shadows
/// (global, remembered, not undoable), text recognition and the QR scan in
/// the background, the dirty check and the exports.
/// </summary>
internal sealed class EditorController : IDisposable
{
    private readonly ISettingsStore _settings;
    private readonly TextRecognitionRunner? _recognizer;
    private readonly List<IDisposable> _subscriptions = [];
    private CancellationTokenSource? _recognition;
    private CancellationTokenSource? _qrScan;
    private int _qrGeneration;
    private (BackdropStyle Backdrop, WatermarkStyle Watermark, bool Shadows) _clean;
    private BackdropStyle _backdrop;
    private WatermarkStyle _watermark;
    private bool _shadows;
    private bool _disposed;

    public EditorController(IServiceProvider services, ScreenshotEditRequest request)
    {
        Services = services;
        _settings = services.GetRequiredService<ISettingsStore>();
        Request = request;
        Scale = double.IsFinite(request.Image.Scale) && request.Image.Scale > 0 ? request.Image.Scale : 1;
        var image = SkiaEditorImage.FromPixelBuffer(request.Image);
        var style = EditorStyle.Load(_settings);
        Session = new EditorSession(image, Scale, style, EditorStyle.LoadTool(_settings), AnnotationFonts.Shared, Crop);
        Session.EnsureBlurSample = (blurStyle, level) => Painter.Caches.SamplesFor(BaseImage).TryEnsure(blurStyle, level);
        Session.StyleChanged += (_, _) => Session.Style.Save(_settings);
        Session.ToolChanged += (_, _) =>
            _settings.Set(EditorSettings.LastTool, EditorTools.IsCreation(Session.Tool) ? EditorTools.Id(Session.Tool) : "arrow");
        Session.ImageChanged += (_, _) => StartBackgroundScans();
        Session.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);

        _backdrop = BackdropCodec.Decode(_settings.Get(EditorSettings.BackdropStyleJson));
        _watermark = WatermarkCodec.Decode(_settings.Get(EditorSettings.WatermarkStyleJson));
        _shadows = _settings.Get(EditorSettings.AnnotationShadows);
        _clean = (_backdrop, _watermark, _shadows);

        if (services.GetService<ITextRecognizer>() is { } recognizer)
        {
            _recognizer = new TextRecognitionRunner(recognizer);
        }

        Wallpapers = services.GetService<IDesktopWallpaperProvider>() ?? new NoWallpapers();
        KeyboardLayout = services.GetService<IKeyboardLayoutInfo>() ?? new UsKeyboardLayoutInfo();
        _subscriptions.Add(_settings.Observe(
            () => Dispatcher.UIThread.Post(() => ToolKeysChanged?.Invoke(this, EventArgs.Empty)),
            EditorSettings.ToolOrderCsv, EditorSettings.ToolShortcutsCsv, EditorSettings.ToolShortcutsEnabled));
    }

    public event EventHandler? Changed;

    /// <summary>The backdrop changed (the canvas content size may differ).</summary>
    public event EventHandler? BackdropChanged;

    /// <summary>The QR result changed (found, gone after a crop).</summary>
    public event EventHandler? QrChanged;

    /// <summary>Tool order, keys or the "use tool shortcuts" switch changed.</summary>
    public event EventHandler? ToolKeysChanged;

    public IServiceProvider Services { get; }

    public ScreenshotEditRequest Request { get; }

    public double Scale { get; }

    public EditorSession Session { get; }

    public EditorCanvasPainter Painter { get; } = new();

    public IDesktopWallpaperProvider Wallpapers { get; }

    public IKeyboardLayoutInfo KeyboardLayout { get; }

    public SkiaEditorImage BaseImage => (SkiaEditorImage)Session.Image;

    public QrResult? Qr { get; private set; }

    public bool IsBusy { get; set; }

    public BackdropStyle Backdrop
    {
        get => _backdrop;
        set
        {
            var sanitized = value.Sanitized();
            if (SameBackdrop(sanitized, _backdrop))
            {
                return;
            }

            _backdrop = sanitized;
            _settings.Set(EditorSettings.BackdropStyleJson, BackdropCodec.Encode(sanitized));
            BackdropChanged?.Invoke(this, EventArgs.Empty);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public WatermarkStyle Watermark
    {
        get => _watermark;
        set
        {
            var sanitized = value.Sanitized();
            if (sanitized == _watermark)
            {
                return;
            }

            _watermark = sanitized;
            _settings.Set(EditorSettings.WatermarkStyleJson, WatermarkCodec.Encode(sanitized));
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool Shadows
    {
        get => _shadows;
        set
        {
            if (value == _shadows)
            {
                return;
            }

            _shadows = value;
            _settings.Set(EditorSettings.AnnotationShadows, value);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public IReadOnlyList<BackdropStyle> BackdropPresets => BackdropCodec.DecodePresets(_settings.Get(EditorSettings.BackdropPresetsJson));

    public void SaveBackdropPreset(BackdropStyle look) =>
        _settings.Set(EditorSettings.BackdropPresetsJson, BackdropCodec.EncodePresets(BackdropCodec.AddPreset(BackdropPresets, look)));

    public void RemoveBackdropPreset(BackdropStyle look) =>
        _settings.Set(EditorSettings.BackdropPresetsJson, BackdropCodec.EncodePresets(BackdropPresets.Where(p => !p.SameLook(look))));

    public IReadOnlyList<WatermarkStyle> WatermarkPresets => WatermarkCodec.DecodePresets(_settings.Get(EditorSettings.WatermarkPresetsJson));

    public void SaveWatermarkPreset(WatermarkStyle mark)
    {
        var sanitized = mark.Sanitized();
        var presets = WatermarkPresets;
        if (sanitized.Kind == WatermarkKind.None || presets.Any(p => p.SameMark(sanitized)))
        {
            return;
        }

        _settings.Set(EditorSettings.WatermarkPresetsJson, WatermarkCodec.EncodePresets(presets.Append(sanitized)));
    }

    public void RemoveWatermarkPreset(WatermarkStyle mark) =>
        _settings.Set(EditorSettings.WatermarkPresetsJson, WatermarkCodec.EncodePresets(WatermarkPresets.Where(p => !p.SameMark(mark))));

    public IReadOnlyList<EditorTool> ToolOrder => Core.ScreenshotEditor.ToolOrder.Parse(_settings.Get(EditorSettings.ToolOrderCsv));

    public ToolBindings ToolBindings => ToolBindings.Parse(_settings.Get(EditorSettings.ToolShortcutsCsv));

    public bool ToolShortcutsEnabled => _settings.Get(EditorSettings.ToolShortcutsEnabled);

    /// <summary>Image, marks, backdrop, watermark or shadows differ from the last clean state (open or last export).</summary>
    public bool IsDirty =>
        Session.IsContentDirty || !SameBackdrop(_clean.Backdrop, _backdrop) || _clean.Watermark != _watermark || _clean.Shadows != _shadows;

    /// <summary>Same look and sliders (records compare colour lists by reference, so compare by value).</summary>
    public static bool SameBackdrop(BackdropStyle a, BackdropStyle b) =>
        a.SameLook(b) && a.Padding == b.Padding && a.CornerRadius == b.CornerRadius && a.Blur == b.Blur;

    public void MarkClean()
    {
        Session.MarkClean();
        _clean = (_backdrop, _watermark, _shadows);
    }

    /// <summary>What the canvas renders this frame.</summary>
    public EditorRenderState RenderState() => new()
    {
        Image = BaseImage,
        Annotations = Session.Annotations,
        Scale = Scale,
        Shadows = _shadows,
        TextRuns = Session.Recognition.Runs,
        Watermark = _watermark,
        HiddenAnnotation = Session.EditingTextId,
    };

    /// <summary>
    /// Renders the export off the UI thread: the full result with backdrop and
    /// watermark, or (for Pin) without the backdrop; the "Save at 1x size"
    /// preference applies to both.
    /// </summary>
    public Task<EditorExport> ExportAsync(bool includeBackdrop)
    {
        var state = RenderState() with { HiddenAnnotation = null };
        var backdrop = _backdrop;
        var downscale = _settings.Get(EditorSettings.Downscale);
        return Task.Run(() => EditorRenderer.Export(state, backdrop, includeBackdrop, downscale));
    }

    public static PixelBuffer ToBuffer(EditorExport export) => SkiaConvert.ToPixelBuffer(export.Image, export.Scale);

    /// <summary>Starts text recognition and the QR scan of the current base image (after open and every image change).</summary>
    public void StartBackgroundScans()
    {
        if (_disposed)
        {
            return;
        }

        var image = BaseImage;
        _recognition?.Cancel();
        if (_recognizer is { IsAvailable: true } runner)
        {
            var cts = _recognition = new CancellationTokenSource();
            var buffer = image.ToPixelBuffer(Scale);
            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await runner.RecognizeAsync(buffer, cts.Token).ConfigureAwait(false);
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (!cts.IsCancellationRequested && !_disposed)
                        {
                            Session.SetRecognition(image, result);
                        }
                    });
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    Log.Warn("screenshotEditor", "Text recognition failed.", ex);
                }
            });
        }

        _qrScan?.Cancel();
        var qr = _qrScan = new CancellationTokenSource();
        var generation = ++_qrGeneration;
        var qrImage = image.Image;
        _ = Task.Run(() =>
        {
            var result = qr.IsCancellationRequested ? null : QrDetector.Detect(qrImage);
            Dispatcher.UIThread.Post(() =>
            {
                if (generation != _qrGeneration || _disposed)
                {
                    return;
                }

                Qr = result;
                QrChanged?.Invoke(this, EventArgs.Empty);
            });
        });
    }

    private static IEditorImage Crop(IEditorImage image, int x, int y, int width, int height) =>
        ((SkiaEditorImage)image).Crop(x, y, width, height);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _recognition?.Cancel();
        _qrScan?.Cancel();
        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }

        // A frame may still be drawing on the render thread; free the canvas caches a little later.
        var painter = Painter;
        DispatcherTimer.RunOnce(painter.Dispose, TimeSpan.FromSeconds(2));
    }
}
