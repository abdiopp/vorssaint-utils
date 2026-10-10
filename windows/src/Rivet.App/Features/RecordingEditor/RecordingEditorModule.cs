// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Modules;
using Rivet.Core.Actions;
using Rivet.Core.App;
using Rivet.Core.Contracts;
using Rivet.Core.Diagnostics;
using Rivet.Core.Features;
using Rivet.Core.Localization;
using Rivet.Core.Platform;
using Rivet.Core.Recording;
using Rivet.Core.RecordingEditor;
using Rivet.Imaging.RecordingEditor;

namespace Rivet.App.Features.RecordingEditor;

/// <summary>
/// The recording editor (feature <c>screenRecorder</c>, editor part):
/// implements <see cref="IRecordingEditor"/>. The recording engine opens the
/// editor with a finished take folder; uninstalling the feature closes every
/// editor (and deletes their takes).
/// </summary>
public sealed class RecordingEditorModule : IFeatureModule
{
    public string Id => "recordingEditor";

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<RecordingEditorService>();
        services.AddSingleton<IRecordingEditor>(sp => sp.GetRequiredService<RecordingEditorService>());
    }

    public void Initialize(ModuleContext context)
    {
        var service = context.Get<RecordingEditorService>();
        context.Features.RegisterController(FeatureIds.ScreenRecorder, new DelegateFeatureController(available =>
        {
            if (!available)
            {
                Dispatcher.UIThread.Post(service.CloseAll);
            }
        }));

        // Development builds have no recording engine on this host: a sample take opens the editor.
        if (context.Services.GetService<IPlatformInfo>() is { IsWindows: false })
        {
            context.Actions.Register(new AppAction
            {
                Id = "recordingEditor.openSample",
                FeatureId = FeatureIds.ScreenRecorder,
                TitleKey = "win.recordingEditor.openSample",
                Icon = "VideoClip",
                Run = _ => service.OpenSampleAsync(),
            });
        }
    }
}

/// <summary>One editor window per take; several can be open at once (spec 02 §3.20.1).</summary>
public sealed class RecordingEditorService(IServiceProvider services) : IRecordingEditor
{
    private readonly Dictionary<string, EditorWindow> _open = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public IReadOnlyCollection<EditorWindow> OpenEditors => _open.Values;

    /// <summary>
    /// Take folders owned by open editors. The recording engine's sweep must
    /// skip these (spec 02 §3.18); see "Requests for shared code" in the module doc.
    /// </summary>
    public IReadOnlyCollection<string> OpenTakeFolders => _open.Keys;

    public bool IsOpen(string takeFolder) => _open.ContainsKey(Path.GetFullPath(takeFolder));

    public async Task OpenAsync(string takeFolder)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            // Windows are created on the UI thread, whoever calls.
            await Dispatcher.UIThread.InvokeAsync(() => OpenAsync(takeFolder));
            return;
        }

        var folder = Path.GetFullPath(takeFolder);
        if (_open.TryGetValue(folder, out var existing))
        {
            existing.Activate();
            return;
        }

        if (!Directory.Exists(folder))
        {
            Log.Warn("recording-editor", $"Take folder {folder} does not exist.");
            services.GetService<IHud>()?.Show(L.Get("recorder.recordFailed"), HudStyle.Error);
            return;
        }

        var session = await EditorSession.LoadAsync(folder, services);
        if (_open.ContainsKey(folder))
        {
            session.Dispose();
            _open[folder].Activate();
            return;
        }

        var window = new EditorWindow(session);
        _open[folder] = window;
        window.Closed += async (_, _) =>
        {
            _open.Remove(folder);
            await session.WaitForExportAsync(TimeSpan.FromSeconds(5));
            session.Dispose();
            // The take lives exactly as long as its editor.
            RecordingFolders.TryDeleteFolder(folder);
        };
        window.PlaceOnPointerScreen();
        window.Show();
        window.Activate();
    }

    /// <summary>Closes every editor without asking; their takes are deleted.</summary>
    public void CloseAll()
    {
        foreach (var window in _open.Values.ToList())
        {
            window.ForceClose();
        }
    }

    /// <summary>Writes a synthetic take under the recordings folder and opens it (development build).</summary>
    public async Task OpenSampleAsync()
    {
        var paths = services.GetRequiredService<AppPaths>();
        var folder = TakeFolders.Create(paths);
        await Task.Run(() => SampleTake.Write(folder));
        await OpenAsync(folder);
    }
}
