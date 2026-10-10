// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Rivet.Core.App;
using Rivet.Core.Contracts;
using Rivet.Core.Diagnostics;
using Rivet.Core.Localization;
using Rivet.Core.Modules.MediaTools;
using Rivet.Core.Platform;
using Rivet.Core.Settings;
using Rivet.Imaging.MediaTools;
using Rivet.Imaging.Skia;
using SkiaSharp;

namespace Rivet.App.Features.MediaTools;

/// <summary>
/// The shared media workspace state and job runner (spec 07 §3.6): one tool
/// at a time, the inputs, the output (automatic or chosen), trims and the
/// probed video facts, and one job at a time. Every host (tray panel tile,
/// Settings page, the Media window) shows the same service, so a job keeps
/// running when a host closes and reopening shows its progress or result.
/// Every output goes through <see cref="MediaOutputCommit"/>.
/// </summary>
public sealed class MediaToolsService : IDisposable
{
    public const string ConvertedFolderName = "Converted";
    private const double GifQualityHint = 0.74;
    private const int GifPaletteSamples = 16;
    private const long EditFreeSpaceBytes = 500_000_000;

    private readonly IServiceProvider _services;
    private readonly ISettingsStore _settings;
    private readonly IVideoProbe _probe;
    private readonly IVideoTranscoder _transcoder;
    private readonly IVideoFrameReader _frames;
    private readonly IOcrEngine _ocr;
    private readonly IImageCodecs _codecs;
    private readonly IFileIdentity _identity;
    private readonly List<string> _inputs = [];
    private CancellationTokenSource? _probeCancel;
    private int _inputGeneration;
    private string? _defaultOutput;

    public MediaToolsService(IServiceProvider services)
    {
        _services = services;
        _settings = services.GetRequiredService<ISettingsStore>();
        _probe = services.GetRequiredService<IVideoProbe>();
        _transcoder = services.GetRequiredService<IVideoTranscoder>();
        _frames = services.GetRequiredService<IVideoFrameReader>();
        _ocr = services.GetRequiredService<IOcrEngine>();
        _codecs = services.GetRequiredService<IImageCodecs>();
        _identity = services.GetService<IFileIdentity>() ?? new PortableFileIdentity();
        Job.Changed += (_, _) => RaiseOnUi(JobChanged);
        Hooks = new MediaCodecHooks { Decode = _codecs.Decode, EncodeHeic = _codecs.CanEncodeHeic ? _codecs.EncodeHeic : null };
    }

    /// <summary>The job state; <see cref="JobChanged"/> mirrors its changes on the UI thread.</summary>
    public MediaJobState Job { get; } = new();

    /// <summary>Tool, inputs, output or probed facts changed (UI thread).</summary>
    public event EventHandler? Changed;

    /// <summary>Job progress or phase changed (UI thread).</summary>
    public event EventHandler? JobChanged;

    public MediaCodecHooks Hooks { get; }

    public MediaTool Tool => MediaSettings.ParseTool(_settings.Get(MediaSettings.LastTool));

    public IReadOnlyList<string> Inputs => _inputs;

    /// <summary>A file the user chose (single output) or a folder (image batches); null = automatic.</summary>
    public string? ManualOutput { get; private set; }

    public VideoInfo? VideoInfo { get; private set; }

    /// <summary>The first image's upright size (Image tool).</summary>
    public MediaSize? ImageSize { get; private set; }

    /// <summary>The input was rejected (wrong type); shown like a failed job.</summary>
    public string? InputError { get; private set; }

    public bool IsBatch => Tool == MediaTool.Image && _inputs.Count > 1;

    public bool CanEncodeHeic => _codecs.CanEncodeHeic;

    public bool VideoAvailable => _transcoder.IsAvailable;

    public bool GifAvailable => _frames.IsAvailable;

    public bool OcrAvailable => _ocr.IsAvailable;

    /// <summary>The output the next run writes: the manual choice, else the automatic one.</summary>
    public string? Output => ManualOutput ?? _defaultOutput;

    public bool HasEditor => _services.GetService<IRecordingEditor>() is not null;

    public void SetTool(MediaTool tool)
    {
        if (tool == Tool)
        {
            return;
        }

        // Switching keeps the inputs even when they do not fit; running then fails with that tool's error.
        Job.Reset();
        _settings.Set(MediaSettings.LastTool, MediaSettings.ToStorage(tool));
        InputError = null;
        ManualOutput = null;
        OnInputsChanged(resetTrim: false);
    }

    /// <summary>
    /// New input from the picker or a drop (drop order kept). The Image tool keeps
    /// only images (any count); the others keep the first file and reject a mismatch.
    /// Choosing new input cancels a running job.
    /// </summary>
    public void SetInputs(IEnumerable<string> paths)
    {
        var all = paths.Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p)).ToList();
        Job.Reset();
        InputError = null;
        ManualOutput = null;
        _inputs.Clear();
        var tool = Tool;
        if (tool == MediaTool.Image)
        {
            _inputs.AddRange(all.Where(MediaImageFormats.IsImage));
            if (_inputs.Count == 0 && all.Count > 0)
            {
                InputError = L.Get("win.mediaTools.unsupported");
            }
        }
        else if (all.Count > 0)
        {
            var first = all[0];
            var fits = tool == MediaTool.Text ? MediaImageFormats.IsImage(first) : MediaImageFormats.IsVideo(first);
            if (fits && all.Skip(1).All(p => tool == MediaTool.Text ? MediaImageFormats.IsImage(p) : MediaImageFormats.IsVideo(p)))
            {
                _inputs.Add(first);
            }
            else
            {
                InputError = L.Get("win.mediaTools.unsupported");
            }
        }

        OnInputsChanged(resetTrim: true);
    }

    public void ClearInputs()
    {
        Job.Reset();
        _inputs.Clear();
        InputError = null;
        ManualOutput = null;
        OnInputsChanged(resetTrim: true);
    }

    public void SetManualOutput(string? path)
    {
        ManualOutput = string.IsNullOrWhiteSpace(path) ? null : path;
        RaiseOnUi(Changed);
    }

    /// <summary>Image options changed: recompute the automatic name; a manual single output follows the format's extension.</summary>
    public void OnImageOptionsChanged()
    {
        if (Tool != MediaTool.Image)
        {
            return;
        }

        if (ManualOutput is { } manual && !IsBatch)
        {
            var extension = CurrentImageOptions().Extension;
            if (!string.Equals(Path.GetExtension(manual).TrimStart('.'), extension, StringComparison.OrdinalIgnoreCase))
            {
                var folder = Path.GetDirectoryName(manual) ?? string.Empty;
                ManualOutput = MediaNaming.Unique(folder, Path.GetFileNameWithoutExtension(manual), extension);
            }
        }

        _defaultOutput = ComputeDefaultOutput();
        RaiseOnUi(Changed);
    }

    /// <summary>Starts the current tool's job. Returns when it finished, failed or was cancelled.</summary>
    public async Task RunAsync()
    {
        if (_inputs.Count == 0)
        {
            var (failId, _) = Job.Begin();
            Job.Fail(failId, InputError ?? L.Get("Strings.mediaErrorNoFile"));
            return;
        }

        var tool = Tool;
        var (id, token) = Job.Begin();
        try
        {
            var result = tool switch
            {
                MediaTool.Video => await RunVideoAsync(id, token).ConfigureAwait(true),
                MediaTool.Gif => await RunGifAsync(id, token).ConfigureAwait(true),
                MediaTool.Image => await RunImageAsync(id, token).ConfigureAwait(true),
                _ => await RunTextAsync(id, token).ConfigureAwait(true),
            };
            Job.Complete(id, result);
        }
        catch (OperationCanceledException)
        {
            // Cancel() already published "cancelled"; a superseded job stays silent.
        }
        catch (MediaJobException ex)
        {
            Job.Fail(id, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            Log.Warn("media", $"{tool} job failed.", ex);
            Job.Fail(id, ex.Message);
        }
    }

    public void Cancel() => Job.Cancel();

    // ── Image options ────────────────────────────────────────────────────

    public ImageOptions CurrentImageOptions() => new(
        MediaImageFormats.Parse(_settings.Get(MediaSettings.ImageFormat)),
        _settings.Get(MediaSettings.ImageQuality),
        new ImageResize(
            MediaImageFormats.ParseResize(_settings.Get(MediaSettings.ImageResizeKind)),
            _settings.Get(MediaSettings.ImageMaxDimension),
            _settings.Get(MediaSettings.ImageResizeWidth),
            _settings.Get(MediaSettings.ImageResizeHeight),
            MediaImageFormats.ParseExact(_settings.Get(MediaSettings.ImageExactMode))),
        _settings.Get(MediaSettings.ImageStripMetadata),
        new WatermarkOptions(
            MediaImageFormats.ParseWatermarkKind(_settings.Get(MediaSettings.WatermarkKind)),
            _settings.Get(MediaSettings.WatermarkText),
            _settings.Get(MediaSettings.WatermarkLogoPath),
            MediaImageFormats.ParsePosition(_settings.Get(MediaSettings.WatermarkPosition)),
            _settings.Get(MediaSettings.WatermarkOpacity),
            _settings.Get(MediaSettings.WatermarkMargin),
            _settings.Get(MediaSettings.WatermarkScale)),
        _settings.Get(MediaSettings.ImageRenamePattern),
        MediaImageFormats.ParseBackground(_settings.Get(MediaSettings.ImageBackground)),
        _settings.Get(MediaSettings.ImagePreserveModificationDate));

    /// <summary>Writes every image option (presets and profiles replace them all).</summary>
    public void ApplyImageOptions(ImageOptions o)
    {
        _settings.Set(MediaSettings.ImageFormat, MediaImageFormats.StorageValue(o.Format));
        _settings.Set(MediaSettings.ImageQuality, o.Quality);
        _settings.Set(MediaSettings.ImageResizeKind, MediaImageFormats.ResizeStorage(o.Resize.Kind));
        _settings.Set(MediaSettings.ImageMaxDimension, o.Resize.MaxDimension);
        _settings.Set(MediaSettings.ImageResizeWidth, o.Resize.Width);
        _settings.Set(MediaSettings.ImageResizeHeight, o.Resize.Height);
        _settings.Set(MediaSettings.ImageExactMode, MediaImageFormats.ExactStorage(o.Resize.ExactMode));
        _settings.Set(MediaSettings.ImageStripMetadata, o.StripMetadata);
        _settings.Set(MediaSettings.WatermarkKind, MediaImageFormats.WatermarkKindStorage(o.Watermark.Kind));
        _settings.Set(MediaSettings.WatermarkText, o.Watermark.Text);
        _settings.Set(MediaSettings.WatermarkLogoPath, o.Watermark.LogoPath);
        _settings.Set(MediaSettings.WatermarkPosition, MediaImageFormats.PositionStorage(o.Watermark.Position));
        _settings.Set(MediaSettings.WatermarkOpacity, o.Watermark.Opacity);
        _settings.Set(MediaSettings.WatermarkMargin, o.Watermark.Margin);
        _settings.Set(MediaSettings.WatermarkScale, o.Watermark.Scale);
        _settings.Set(MediaSettings.ImageRenamePattern, o.RenamePattern);
        _settings.Set(MediaSettings.ImageBackground, MediaImageFormats.BackgroundStorage(o.Background));
        _settings.Set(MediaSettings.ImagePreserveModificationDate, o.PreserveModificationDate);
        OnImageOptionsChanged();
    }

    public IReadOnlyList<ImageProfile> Profiles() =>
        MediaImageProfiles.Decode(_settings.Get(MediaSettings.ImageProfiles), ProfileDefaultName);

    public ImageProfile? SelectedProfile()
    {
        var id = _settings.Get(MediaSettings.ImageSelectedProfileId);
        return id.Length == 0 ? null : Profiles().FirstOrDefault(p => p.Id == id);
    }

    public void SelectProfile(string? id)
    {
        var profile = id is null ? null : Profiles().FirstOrDefault(p => p.Id == id);
        _settings.Set(MediaSettings.ImageSelectedProfileId, profile?.Id ?? string.Empty);
        if (profile is not null)
        {
            ApplyImageOptions(profile.Options);
        }
        else
        {
            RaiseOnUi(Changed);
        }
    }

    /// <summary>"Save new": appends the current options with the typed name (or "Profile %d") and selects it.</summary>
    public ImageProfile SaveNewProfile(string? name)
    {
        var profiles = Profiles().ToList();
        var profile = new ImageProfile(Guid.NewGuid().ToString("D").ToUpperInvariant(), name?.Trim() ?? string.Empty, CurrentImageOptions());
        profiles.Add(profile);
        SaveProfiles(profiles);
        var saved = Profiles().Last();
        _settings.Set(MediaSettings.ImageSelectedProfileId, saved.Id);
        RaiseOnUi(Changed);
        return saved;
    }

    /// <summary>"Update": overwrites the selected profile, renaming it when a name was typed.</summary>
    public void UpdateSelectedProfile(string? name)
    {
        var selected = SelectedProfile();
        if (selected is null)
        {
            return;
        }

        var profiles = Profiles().Select(p => p.Id == selected.Id
            ? p with { Name = string.IsNullOrWhiteSpace(name) ? p.Name : name.Trim(), Options = CurrentImageOptions() }
            : p).ToList();
        SaveProfiles(profiles);
        RaiseOnUi(Changed);
    }

    public void DeleteSelectedProfile()
    {
        var selected = SelectedProfile();
        if (selected is null)
        {
            return;
        }

        SaveProfiles(Profiles().Where(p => p.Id != selected.Id).ToList());
        _settings.Set(MediaSettings.ImageSelectedProfileId, string.Empty);
        RaiseOnUi(Changed);
    }

    /// <summary>The current options differ from the selected profile.</summary>
    public bool SelectedProfileModified() => SelectedProfile() is { } profile && profile.Options != CurrentImageOptions();

    // ── Results ─────────────────────────────────────────────────────────

    public void RevealOutputs()
    {
        if (Job.Result?.Outputs.FirstOrDefault() is { } first)
        {
            _services.GetRequiredService<IShellService>().RevealInExplorer(first);
        }
    }

    public void CopyText()
    {
        if (Job.Result?.Text is { } text)
        {
            _services.GetRequiredService<IClipboardService>().SetText(text);
        }
    }

    /// <summary>"%d saved, %d failed" then one "input -> output" line per saved file and the failures.</summary>
    public string Summary(MediaJobResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine(L.Format("MediaImageConverterStrings.batchSummaryHeaderFormat", result.Succeeded, result.Failures.Count));
        for (var i = 0; i < result.Outputs.Count; i++)
        {
            var input = i < result.Inputs.Count ? Path.GetFileName(result.Inputs[i]) : string.Empty;
            builder.AppendLine(L.Format("MediaImageConverterStrings.batchSummaryItemFormat", input, Path.GetFileName(result.Outputs[i])));
        }

        foreach (var failure in result.Failures)
        {
            builder.AppendLine($"{Path.GetFileName(failure.Input)}: {failure.Message}");
        }

        return builder.ToString().TrimEnd();
    }

    public void CopySummary()
    {
        if (Job.Result is { } result)
        {
            _services.GetRequiredService<IClipboardService>().SetText(Summary(result));
        }
    }

    /// <summary>
    /// "Edit": copies the one clip into a private take folder (when at least 500 MB stay
    /// free) and opens the recording editor; the folder goes away when that fails.
    /// </summary>
    public async Task<bool> EditAsync()
    {
        var editor = _services.GetService<IRecordingEditor>();
        if (editor is null || Tool != MediaTool.Video || _inputs.Count != 1)
        {
            return false;
        }

        var input = _inputs[0];
        var generation = _inputGeneration;
        var root = _services.GetRequiredService<AppPaths>().LocalFolder("Recordings");
        var take = Path.Combine(root, "Take-" + Guid.NewGuid().ToString("D").ToUpperInvariant());
        try
        {
            var size = new FileInfo(input).Length;
            var free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root))!).AvailableFreeSpace;
            if (free - size < EditFreeSpaceBytes)
            {
                Job.Fail(Job.Begin().Id, L.Get("win.mediaTools.notEnoughSpace"));
                return false;
            }

            Directory.CreateDirectory(take);
            var target = Path.Combine(take, "take" + Path.GetExtension(input).ToLowerInvariant());
            await Task.Run(() => File.Copy(input, target)).ConfigureAwait(true);
            if (generation != _inputGeneration)
            {
                Directory.Delete(take, recursive: true);
                return false;
            }

            await editor.OpenAsync(take).ConfigureAwait(true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            Log.Warn("media", "Handing the clip to the editor failed.", ex);
            try
            {
                if (Directory.Exists(take))
                {
                    Directory.Delete(take, recursive: true);
                }
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
            }

            return false;
        }
    }

    public void Dispose()
    {
        _probeCancel?.Cancel();
        Job.Reset();
    }

    // ── Inputs ──────────────────────────────────────────────────────────

    private void OnInputsChanged(bool resetTrim)
    {
        _inputGeneration++;
        _probeCancel?.Cancel();
        _probeCancel = null;
        VideoInfo = null;
        ImageSize = null;
        if (resetTrim)
        {
            _settings.Set(MediaSettings.VideoStart, 0);
            _settings.Set(MediaSettings.VideoEnd, 0);
            _settings.Set(MediaSettings.GifStart, 0);
            _settings.Set(MediaSettings.GifEnd, 0);
        }

        var tool = Tool;
        if (_inputs.Count > 0 && tool == MediaTool.Image)
        {
            ImageSize = MediaImageProcessor.ReadSize(_inputs[0], Hooks);
        }
        else if (_inputs.Count > 0 && tool is MediaTool.Video or MediaTool.Gif && MediaImageFormats.IsVideo(_inputs[0]))
        {
            _ = LoadDurationAsync(_inputs[0], _inputGeneration, tool, resetTrim);
        }

        _defaultOutput = ComputeDefaultOutput();
        RaiseOnUi(Changed);
    }

    private async Task LoadDurationAsync(string input, int generation, MediaTool tool, bool setTrim)
    {
        var cancel = _probeCancel = new CancellationTokenSource();
        VideoInfo? info;
        try
        {
            info = await _probe.ProbeAsync(input, cancel.Token).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn("media", "Reading the clip failed.", ex);
            info = null;
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // A late reply for a superseded input or tool is ignored.
        if (generation != _inputGeneration || tool != Tool || info is null)
        {
            return;
        }

        VideoInfo = info;
        if (setTrim)
        {
            var end = Math.Round(info.Duration, 1);
            if (tool == MediaTool.Video)
            {
                _settings.Set(MediaSettings.VideoStart, 0);
                _settings.Set(MediaSettings.VideoEnd, end);
            }
            else
            {
                _settings.Set(MediaSettings.GifStart, 0);
                _settings.Set(MediaSettings.GifEnd, end);
            }
        }

        RaiseOnUi(Changed);
    }

    /// <summary>The automatic output (spec 07 §3.6.8), computed when the input or options change, not at run time.</summary>
    private string? ComputeDefaultOutput()
    {
        if (_inputs.Count == 0)
        {
            return null;
        }

        var input = _inputs[0];
        var folder = Path.GetDirectoryName(Path.GetFullPath(input)) ?? string.Empty;
        switch (Tool)
        {
            case MediaTool.Video:
                return MediaNaming.Unique(folder, MediaNaming.DefaultVideoStem(input), "mp4");
            case MediaTool.Gif:
                return MediaNaming.Unique(folder, MediaNaming.DefaultGifStem(input), "gif");
            case MediaTool.Text:
                return MediaNaming.Unique(folder, MediaNaming.DefaultTextStem(input), "txt");
            default:
                if (IsBatch)
                {
                    return _settings.Get(MediaSettings.ImageSaveInSubfolder) ? Path.Combine(folder, ConvertedFolderName) : folder;
                }

                var options = CurrentImageOptions();
                var target = ImageSize is { } size ? MediaSizing.TargetSize(size, options.Resize) : new MediaSize(0, 0);
                var stem = MediaNaming.ExpandPattern(options.RenamePattern, input, 1, target.Width, target.Height, options.Extension, DateTime.UtcNow);

                // Never the source itself: photo.jpg processed as JPEG becomes "photo 2.jpg".
                return MediaNaming.Unique(folder, stem, options.Extension, null, p => File.Exists(p) || Directory.Exists(p) || _identity.AreSameFile(p, input));
        }
    }

    // ── Video ───────────────────────────────────────────────────────────

    private async Task<MediaJobResult> RunVideoAsync(int id, CancellationToken token)
    {
        var input = _inputs[0];
        var info = await ProbeForRunAsync(input, token).ConfigureAwait(true);
        var trim = MediaPlanner.ClampTrim(_settings.Get(MediaSettings.VideoStart), _settings.Get(MediaSettings.VideoEnd), info.Duration)
                   ?? throw new MediaJobException(L.Get("win.mediaTools.unsupported"));
        var output = CheckedOutput(input);
        if (!_transcoder.IsAvailable)
        {
            throw new MediaJobException(L.Get("win.mediaTools.videoUnavailable"));
        }

        var duration = trim.End - trim.Start;
        var sizing = MediaSettings.ParseSizing(_settings.Get(MediaSettings.VideoSizing));
        using var commit = new MediaOutputCommit(output, _identity);
        var progress = new Progress<double>(p => Job.Report(id, Math.Min(0.99, p)));
        if (sizing == MediaSizingMode.Resolution)
        {
            var step = MediaPlanner.ResolutionStep(info.Size, info.FrameRate, _settings.Get(MediaSettings.VideoQuality), _settings.Get(MediaSettings.VideoMaxDimension), info.HasAudio);
            await _transcoder.EncodeAsync(new VideoEncodeRequest
            {
                Input = input,
                Output = commit.TempFile,
                Start = trim.Start,
                End = trim.End,
                Size = step.Size,
                VideoBitRate = step.VideoBitRate,
                AudioBitRate = step.AudioBitRate,
                FrameRate = MediaPlanner.FrameRate(info.FrameRate),
                KeepAudio = info.HasAudio,
            }, progress, token).ConfigureAwait(true);
        }
        else
        {
            var target = _settings.Get(MediaSettings.VideoTargetMegabytes) * MediaPlanner.BytesPerMegabyte;
            var scale = 1.0;
            var fitted = false;
            for (var pass = 0; pass < MediaPlanner.MaxVideoPasses && !fitted; pass++)
            {
                var plan = MediaPlanner.PlanVideo(target, duration, info.Size, info.FrameRate, info.HasAudio, scale)
                           ?? throw new MediaJobException(L.Get("Strings.mediaErrorTargetTooSmall"));
                Job.Report(id, 0);
                await _transcoder.EncodeAsync(new VideoEncodeRequest
                {
                    Input = input,
                    Output = commit.TempFile,
                    Start = trim.Start,
                    End = trim.End,
                    Size = plan.Size,
                    VideoBitRate = plan.VideoBitRate,
                    AudioBitRate = plan.AudioBitRate,
                    FrameRate = plan.FrameRate,
                    KeepAudio = info.HasAudio,
                }, progress, token).ConfigureAwait(true);
                var actual = new FileInfo(commit.TempFile).Length;
                if (actual <= target)
                {
                    fitted = true;
                    break;
                }

                var next = MediaPlanner.VideoRetryScale(scale, target, actual);
                if (next >= scale)
                {
                    break;
                }

                scale = next;
            }

            if (!fitted)
            {
                throw new MediaJobException(L.Get("Strings.mediaErrorTargetTooSmall"));
            }
        }

        token.ThrowIfCancellationRequested();
        commit.Commit();
        return Finished(MediaTool.Video, [input], [output]);
    }

    // ── GIF ─────────────────────────────────────────────────────────────

    private async Task<MediaJobResult> RunGifAsync(int id, CancellationToken token)
    {
        var input = _inputs[0];
        var info = await ProbeForRunAsync(input, token).ConfigureAwait(true);
        var trim = MediaPlanner.ClampTrim(_settings.Get(MediaSettings.GifStart), _settings.Get(MediaSettings.GifEnd), info.Duration)
                   ?? throw new MediaJobException(L.Get("win.mediaTools.unsupported"));
        var output = CheckedOutput(input);
        if (!_frames.IsAvailable)
        {
            throw new MediaJobException(L.Get("win.mediaTools.videoUnavailable"));
        }

        var duration = trim.End - trim.Start;
        var sizing = MediaSettings.ParseSizing(_settings.Get(MediaSettings.GifSizing));
        var loops = _settings.Get(MediaSettings.GifLoops);
        var plan = sizing == MediaSizingMode.TargetSize
            ? MediaPlanner.GifStart(info.Size.Width, duration)
            : new GifPlan(_settings.Get(MediaSettings.GifWidth), (int)_settings.Get(MediaSettings.GifFps));
        var target = sizing == MediaSizingMode.TargetSize ? _settings.Get(MediaSettings.GifTargetMegabytes) * MediaPlanner.BytesPerMegabyte : long.MaxValue;
        using var commit = new MediaOutputCommit(output, _identity);
        for (var pass = 0; ; pass++)
        {
            if (!MediaPlanner.GifFits(duration, plan.Fps))
            {
                throw new MediaJobException(L.Format("recorder.gifTooLongFormat", MediaPlanner.GifMaxSeconds(plan.Fps)));
            }

            Job.Report(id, 0);
            await EncodeGifAsync(id, input, trim.Start, trim.End, info.Size, plan, loops, commit.TempFile, token).ConfigureAwait(true);
            var actual = new FileInfo(commit.TempFile).Length;
            if (actual <= target)
            {
                break;
            }

            var next = pass + 1 < MediaPlanner.MaxGifPasses ? MediaPlanner.GifRetry(plan, target, actual) : null;
            plan = next ?? throw new MediaJobException(L.Get("Strings.mediaErrorTargetTooSmall"));
        }

        token.ThrowIfCancellationRequested();
        commit.Commit();
        return Finished(MediaTool.Gif, [input], [output]);
    }

    /// <summary>
    /// Two reads of the clip: a few evenly spread frames build one global palette,
    /// then every frame is dithered against it and appended (frames are never all held in memory).
    /// </summary>
    private async Task EncodeGifAsync(int id, string input, double start, double end, MediaSize source, GifPlan plan, bool loops, string path, CancellationToken token)
    {
        var size = MediaSizing.ScaledEvenSize(source, plan.Width);
        var count = MediaPlanner.GifFrameCount(end - start, plan.Fps);
        var times = Enumerable.Range(0, count).Select(i => MediaPlanner.GifFrameTime(i, plan.Fps, start, end)).ToList();
        var sampleCount = Math.Min(GifPaletteSamples, count);
        var sampleTimes = Enumerable.Range(0, sampleCount).Select(i => times[(int)((long)i * (count - 1) / Math.Max(1, sampleCount - 1))]).Distinct().ToList();
        var samples = new List<PixelBuffer>();
        await _frames.ReadFramesAsync(input, sampleTimes, size, (_, frame) =>
        {
            samples.Add(new PixelBuffer(frame.Width, frame.Height, (byte[])frame.Pixels.Clone(), frame.Stride));
            return Task.CompletedTask;
        }, token).ConfigureAwait(false);
        if (samples.Count == 0)
        {
            throw new MediaJobException(L.Get("win.mediaTools.frameUnavailable"));
        }

        var palette = await Task.Run(() => GifPalette.Build(samples), token).ConfigureAwait(false);
        samples.Clear();
        var delay = MediaPlanner.GifDelayCentiseconds(plan.Fps);
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        using var encoder = new GifEncoder(stream, size.Width, size.Height, palette, new GifEncoderOptions { Loop = loops, Dither = GifQualityHint });
        await _frames.ReadFramesAsync(input, times, size, (index, frame) =>
        {
            token.ThrowIfCancellationRequested();
            encoder.AddFrame(frame, delay);
            Job.Report(id, Math.Min(0.99, (index + 1) / (double)count));
            return Task.CompletedTask;
        }, token).ConfigureAwait(false);
        encoder.Finish();
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    // ── Image ───────────────────────────────────────────────────────────

    private async Task<MediaJobResult> RunImageAsync(int id, CancellationToken token)
    {
        var inputs = _inputs.ToList();
        var options = CurrentImageOptions();
        if (options.Format == ImageOutputFormat.Heic && !_codecs.CanEncodeHeic)
        {
            throw new MediaJobException(L.Get("win.mediaTools.heicMissing"));
        }

        SKImage? logo = null;
        if (options.Watermark.HasLogo)
        {
            logo = MediaImageProcessor.LoadLogo(options.Watermark.LogoPath, Hooks)
                   ?? throw new MediaJobException(L.Get("MediaImageConverterStrings.noLogo"));
        }

        try
        {
            var batch = inputs.Count > 1;
            var folder = batch ? Output ?? Path.GetDirectoryName(Path.GetFullPath(inputs[0]))! : null;
            if (batch)
            {
                Directory.CreateDirectory(folder!);
            }

            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var outputs = new List<string>();
            var succeededInputs = new List<string>();
            var failures = new List<MediaItemFailure>();
            var now = DateTime.UtcNow;
            for (var i = 0; i < inputs.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var input = inputs[i];
                try
                {
                    string destination;
                    if (batch)
                    {
                        var size = MediaImageProcessor.ReadSize(input, Hooks) ?? throw new MediaJobException(L.Get("win.mediaTools.unsupported"));
                        var target = MediaSizing.TargetSize(size, options.Resize);
                        var stem = MediaNaming.ExpandPattern(options.RenamePattern, input, i + 1, target.Width, target.Height, options.Extension, now);
                        destination = MediaNaming.Unique(folder!, stem, options.Extension, used, p => File.Exists(p) || Directory.Exists(p) || _identity.AreSameFile(p, input));
                    }
                    else
                    {
                        destination = Output ?? throw new MediaJobException(L.Get("Strings.mediaErrorNoFile"));
                    }

                    if (File.Exists(destination) && _identity.AreSameFile(destination, input))
                    {
                        throw new MediaJobException(L.Get("Strings.mediaErrorSameOutput"));
                    }

                    using var commit = new MediaOutputCommit(destination, _identity);
                    await Task.Run(() => MediaImageProcessor.Process(input, commit.TempFile, options, logo, Hooks), token).ConfigureAwait(true);
                    token.ThrowIfCancellationRequested();
                    commit.Commit();
                    if (options.PreserveModificationDate)
                    {
                        File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(input));
                    }

                    outputs.Add(destination);
                    succeededInputs.Add(input);
                }
                catch (MediaImageProcessor.ProcessException ex)
                {
                    var message = ex.Message == "tooLarge" ? L.Get("MediaImageConverterStrings.tooLarge") : L.Get("win.mediaTools.unsupported");
                    if (!batch)
                    {
                        throw new MediaJobException(message);
                    }

                    failures.Add(new MediaItemFailure(input, message));
                }
                catch (Exception ex) when (batch && ex is MediaJobException or IOException or UnauthorizedAccessException)
                {
                    failures.Add(new MediaItemFailure(input, ex.Message));
                }

                Job.Report(id, (i + 1) / (double)inputs.Count);
            }

            if (outputs.Count == 0 && failures.Count > 0)
            {
                throw new MediaJobException(failures[0].Message);
            }

            return Finished(MediaTool.Image, succeededInputs, outputs) with { Failures = failures };
        }
        finally
        {
            logo?.Dispose();
        }
    }

    // ── Text ────────────────────────────────────────────────────────────

    private async Task<MediaJobResult> RunTextAsync(int id, CancellationToken token)
    {
        var input = _inputs[0];
        if (!MediaImageFormats.IsImage(input))
        {
            throw new MediaJobException(L.Get("win.mediaTools.unsupported"));
        }

        var output = CheckedOutput(input);
        if (!_ocr.IsAvailable)
        {
            throw new MediaJobException(L.Get("win.mediaTools.ocrUnavailable"));
        }

        var size = MediaImageProcessor.ReadSize(input, Hooks) ?? throw new MediaJobException(L.Get("win.mediaTools.unsupported"));
        if (!MediaSizing.IsSafe(size))
        {
            throw new MediaJobException(L.Get("MediaImageConverterStrings.tooLarge"));
        }

        // Unlike macOS, the photo is read upright (EXIF orientation applied), so rotated phone pictures work.
        var buffer = await Task.Run(() =>
        {
            using var image = MediaImageProcessor.Decode(input, 0, Hooks) ?? throw new MediaJobException(L.Get("win.mediaTools.unsupported"));
            return SkiaConvert.ToPixelBuffer(image);
        }, token).ConfigureAwait(true);
        Job.Report(id, 0.2);
        var languages = MediaText.OcrLanguages(Localizer.Current.Language);
        var text = await _ocr.RecognizeAsync(buffer, languages, _settings.Get(MediaSettings.TextAccurate), token).ConfigureAwait(true);
        token.ThrowIfCancellationRequested();
        using (var commit = new MediaOutputCommit(output, _identity))
        {
            await File.WriteAllBytesAsync(commit.TempFile, new UTF8Encoding(false).GetBytes(text), token).ConfigureAwait(true);
            commit.Commit();
        }

        return Finished(MediaTool.Text, [input], [output]) with { Text = text };
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private async Task<VideoInfo> ProbeForRunAsync(string input, CancellationToken token)
    {
        if (!MediaImageFormats.IsVideo(input))
        {
            throw new MediaJobException(L.Get("win.mediaTools.unsupported"));
        }

        var info = VideoInfo ?? await _probe.ProbeAsync(input, token).ConfigureAwait(true);
        if (info is null)
        {
            throw new MediaJobException(_transcoder.IsAvailable ? L.Get("win.mediaTools.unsupported") : L.Get("win.mediaTools.videoUnavailable"));
        }

        if (!info.HasVideo)
        {
            throw new MediaJobException(L.Get("Strings.mediaErrorNoVideo"));
        }

        return info;
    }

    /// <summary>The single output, refusing the input itself (same volume and file id).</summary>
    private string CheckedOutput(string input)
    {
        var output = Output ?? throw new MediaJobException(L.Get("Strings.mediaErrorNoFile"));
        if (File.Exists(output) && _identity.AreSameFile(output, input))
        {
            throw new MediaJobException(L.Get("Strings.mediaErrorSameOutput"));
        }

        return output;
    }

    private static MediaJobResult Finished(MediaTool tool, IReadOnlyList<string> inputs, IReadOnlyList<string> outputs) => new()
    {
        Tool = tool,
        Inputs = inputs,
        Outputs = outputs,
        InputBytes = inputs.Sum(SafeLength),
        OutputBytes = outputs.Sum(SafeLength),
    };

    private static long SafeLength(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static string ProfileDefaultName(int position) => L.Format("MediaImageConverterStrings.profileDefaultNameFormat", position);

    private void SaveProfiles(IReadOnlyList<ImageProfile> profiles) =>
        _settings.Set(MediaSettings.ImageProfiles, MediaImageProfiles.Encode(profiles, ProfileDefaultName));

    private void RaiseOnUi(EventHandler? handler)
    {
        if (handler is null)
        {
            return;
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            handler(this, EventArgs.Empty);
        }
        else
        {
            Dispatcher.UIThread.Post(() => handler(this, EventArgs.Empty));
        }
    }
}
