// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Extensions.DependencyInjection;
using Rivet.Core.Contracts;
using Rivet.Core.Diagnostics;
using Rivet.Core.Localization;
using Rivet.Core.Platform;
using Rivet.Core.Recording;
using Rivet.Core.Recording.Engine;
using Rivet.Core.Settings;

namespace Rivet.App.Features.Recording;

/// <summary>
/// What happens to a finished take (spec 02 §3.17): a normal stop with
/// "Open the editor after recording" on hands the take folder to the
/// recording editor, which owns it from then on. Otherwise (setting off, no
/// editor installed, disk almost full, feature removed, quitting) the
/// recording is saved straight away as <c>Recording yyyy-MM-dd at HH.mm.ss.mp4</c>
/// with its sound mixed in, added to Recent captures, and the take deleted.
/// If saving fails the take is kept: the editor opens on it as a recovery, or
/// File Explorer shows the master when there is no editor.
/// </summary>
public sealed class RecordingDelivery
{
    private readonly IServiceProvider _services;
    private readonly ISettingsStore _settings;

    public RecordingDelivery(IServiceProvider services)
    {
        _services = services;
        _settings = services.GetRequiredService<ISettingsStore>();
    }

    /// <summary>The folder recordings are saved to straight away (Videos when unset or unusable).</summary>
    public string SaveFolder => ResolveSaveFolder(_settings.Get(RecorderSettings.SaveFolder));

    public static string DefaultSaveFolder
    {
        get
        {
            var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            return string.IsNullOrEmpty(videos) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : videos;
        }
    }

    public static string ResolveSaveFolder(string configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return DefaultSaveFolder;
        }

        var expanded = Environment.ExpandEnvironmentVariables(configured.Trim());
        if (expanded.StartsWith('~'))
        {
            expanded = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), expanded.TrimStart('~').TrimStart('/', '\\'));
        }

        try
        {
            return Path.IsPathFullyQualified(expanded) && Directory.Exists(expanded) ? expanded : DefaultSaveFolder;
        }
        catch (ArgumentException)
        {
            return DefaultSaveFolder;
        }
    }

    /// <summary>Delivers a written take. Returns the saved file, or null when the editor took it (or saving failed).</summary>
    public async Task<string?> DeliverAsync(string takeFolder, TakeManifest manifest, StopReason reason)
    {
        var hud = _services.GetService<IHud>();
        var editor = _services.GetService<IRecordingEditor>();
        if (reason.OpensEditor && _settings.Get(RecorderSettings.OpenEditor) && editor is not null)
        {
            try
            {
                await editor.OpenAsync(takeFolder);
                if (reason.Message is { } message)
                {
                    hud?.Show(message, HudStyle.Warning);
                }

                return null;
            }
            catch (Exception ex)
            {
                Log.Error("recorder", "The recording editor could not open the take; saving it instead.", ex);
            }
        }

        var saved = await SaveAsync(takeFolder, manifest);
        if (saved is not null)
        {
            await Task.Run(() => TryDeleteTake(takeFolder));
            AddToRecents(saved, manifest);
            hud?.Show(reason.Message ?? L.Format("recorder.savedHUDFormat", FolderDisplayName(Path.GetDirectoryName(saved)!)), RecordingChrome.StyleFor(reason.Kind), "CheckmarkCircle");
            return saved;
        }

        _services.GetService<IRecorderSystem>()?.Beep();
        hud?.Show(L.Get("recorder.recordFailed"), HudStyle.Error);
        if (editor is not null)
        {
            try
            {
                await editor.OpenAsync(takeFolder);
                return null;
            }
            catch (Exception ex)
            {
                Log.Error("recorder", "Recovery through the editor failed.", ex);
            }
        }

        // Last resort: the take stays (it is swept after 24 h); show the master so it can be rescued.
        var master = Path.Combine(takeFolder, manifest.Video.File);
        if (File.Exists(master))
        {
            _services.GetService<IShellService>()?.RevealInExplorer(master);
        }

        return null;
    }

    /// <summary>Saves the take to the save folder (no UI, safe while quitting). Returns the file or null.</summary>
    public async Task<string?> SaveAsync(string takeFolder, TakeManifest manifest)
    {
        var master = Path.Combine(takeFolder, manifest.Video.File);
        if (!File.Exists(master))
        {
            return null;
        }

        string destination;
        try
        {
            var folder = SaveFolder;
            Directory.CreateDirectory(folder);
            var name = RecordingFileName.Build(L.Get("recorder.fileNamePrefix"), DateTime.Now, ".mp4", n => File.Exists(Path.Combine(folder, n)));
            destination = Path.Combine(folder, name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("recorder", "The save folder is not usable.", ex);
            return null;
        }

        if (manifest.Audio.Count > 0 && _services.GetService<IRawTakeExporter>() is { } exporter)
        {
            try
            {
                if (await exporter.ExportAsync(takeFolder, manifest, destination, CancellationToken.None).ConfigureAwait(false))
                {
                    return destination;
                }

                Log.Warn("recorder", "Saving with sound failed; saving the picture only.");
            }
            catch (Exception ex)
            {
                Log.Warn("recorder", "Saving with sound failed; saving the picture only.", ex);
            }
        }

        try
        {
            await Task.Run(() => File.Move(master, destination)).ConfigureAwait(false);
            return destination;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("recorder", "Saving the recording failed.", ex);
            return null;
        }
    }

    public static void TryDeleteTake(string takeFolder)
    {
        try
        {
            if (Directory.Exists(takeFolder))
            {
                Directory.Delete(takeFolder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("recorder", $"Could not delete {Path.GetFileName(takeFolder)}; it will be swept later.", ex);
        }
    }

    public static string FolderDisplayName(string folder)
    {
        var name = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrEmpty(name) ? folder : name;
    }

    private void AddToRecents(string path, TakeManifest manifest)
    {
        try
        {
            _services.GetService<IRecentCaptures>()?.Add(new CaptureRecord
            {
                Id = Guid.NewGuid().ToString("D"),
                Kind = CaptureKind.Recording,
                FilePath = path,
                CreatedAt = DateTimeOffset.Now,
                PixelWidth = manifest.Video.Width,
                PixelHeight = manifest.Video.Height,
                Duration = TimeSpan.FromSeconds(manifest.Video.DurationSeconds),
            });
        }
        catch (Exception ex)
        {
            Log.Warn("recorder", "Adding the recording to Recent captures failed.", ex);
        }
    }
}
