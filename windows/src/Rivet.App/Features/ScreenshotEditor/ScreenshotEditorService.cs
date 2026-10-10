// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Shell;
using Rivet.Core.Contracts;
using Rivet.Core.Diagnostics;
using Rivet.Core.Features;
using Rivet.Core.Platform;
using Rivet.Core.ScreenshotEditor;
using Rivet.Imaging.ScreenshotEditor;

namespace Rivet.App.Features.ScreenshotEditor;

/// <summary>
/// Opens screenshot editors (one window per capture, several at once) and
/// closes them all, with every pin, when the Screenshot feature is uninstalled.
/// </summary>
public sealed class ScreenshotEditorService(IServiceProvider services) : IScreenshotEditor, IFeatureController
{
    private readonly List<EditorWindow> _open = [];

    public int OpenCount => _open.Count;

    public Task OpenAsync(ScreenshotEditRequest request)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            Open(request);
            return Task.CompletedTask;
        }

        return Dispatcher.UIThread.InvokeAsync(() => { Open(request); }).GetTask();
    }

    internal EditorWindow Open(ScreenshotEditRequest request)
    {
        var window = new EditorWindow(services, request);
        window.PlaceOnPointerDisplay();
        window.Closed += (_, _) => _open.Remove(window);
        _open.Add(window);
        window.Show();
        window.Activate();
        var handle = WindowInterop.Handle(window);
        if (handle != 0)
        {
            // A tray app does not own the foreground; ask for it so the editor takes the keyboard.
            services.GetService<IWindowChrome>()?.BringToFront(handle);
        }

        return window;
    }

    public void Sync(bool available)
    {
        if (!available)
        {
            CloseAll();
        }
    }

    public void CloseAll()
    {
        foreach (var window in _open.ToList())
        {
            window.ForceClose();
        }

        PinWindow.CloseAll();
    }

    /// <summary>Deletes drag and share folders left by an earlier run (only exact app-named folders created before this launch).</summary>
    public static void CleanUpAtLaunch()
    {
        var started = DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount64 % int.MaxValue);
        try
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            started = process.StartTime;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            Log.Warn("screenshotEditor", "Process start time unavailable.", ex);
        }

        try
        {
            foreach (var folder in Directory.EnumerateDirectories(Path.GetTempPath(), EditorFiles.DragFolderPrefix + "*"))
            {
                if (Directory.GetCreationTime(folder) < started)
                {
                    EditorOutput.DeleteDragFolder(folder);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("screenshotEditor", "Could not clean old drag folders.", ex);
        }
    }
}
