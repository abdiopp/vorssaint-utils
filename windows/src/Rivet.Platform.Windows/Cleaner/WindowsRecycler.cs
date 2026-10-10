// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Rivet.Core.Diagnostics;
using Rivet.Core.Maintenance.Cleaner;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;

namespace Rivet.Platform.Windows.Cleaner;

/// <summary>
/// Recycle, verify, refuse (spec §3.1.9). Windows silently deletes what it
/// cannot recycle, so every item is checked first: a fixed drive, a Recycle
/// Bin that is switched on for that drive, and room left in it. Then one
/// <c>IFileOperation</c> recycles the batch; its progress sink reports, per
/// item, whether a recycled copy was created. FOF_WANTNUKEWARNING keeps a
/// last safety net: should Windows still want to delete something
/// permanently, it asks the person instead of doing it silently.
/// </summary>
public sealed class WindowsRecycler(WindowsFileSystem fs) : IRecycler
{
    private const uint FofSilent = 0x0004;
    private const uint FofNoConfirmation = 0x0010;
    private const uint FofAllowUndo = 0x0040;
    private const uint FofNoErrorUi = 0x0400;
    private const uint FofWantNukeWarning = 0x4000;
    private const uint FofxShowElevationPrompt = 0x00040000;
    private const uint FofxRecycleOnDelete = 0x00080000;
    private const uint DriveFixed = 3;
    private const long Megabyte = 1024L * 1024;

    public IReadOnlyList<RecycleOutcome> Recycle(IReadOnlyList<string> paths, bool allowElevationPrompt)
    {
        var outcomes = new Dictionary<string, RecycleOutcome>(StringComparer.OrdinalIgnoreCase);
        var allowed = new List<string>();
        var budget = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            var full = SafePaths.Normalize(path);
            if (fs.Stat(full) is null)
            {
                outcomes[full] = new RecycleOutcome(full, RecycleStatus.Missing);
                continue;
            }

            if (Refusal(full, budget) is { } reason)
            {
                outcomes[full] = new RecycleOutcome(full, RecycleStatus.Refused, reason);
                continue;
            }

            allowed.Add(full);
        }

        if (allowed.Count > 0)
        {
            foreach (var outcome in RunOnStaThread(allowed, allowElevationPrompt))
            {
                outcomes[outcome.Path] = outcome;
            }
        }

        return paths.Select(p => outcomes.TryGetValue(SafePaths.Normalize(p), out var o) ? o : new RecycleOutcome(p, RecycleStatus.Failed)).ToList();
    }

    /// <summary>Why an item cannot go to the Recycle Bin, or null when it can.</summary>
    private string? Refusal(string path, Dictionary<string, long> budget)
    {
        Span<char> buffer = stackalloc char[300];
        if (!PInvoke.GetVolumePathName(path, buffer))
        {
            return "no volume";
        }

        var root = new string(buffer[..buffer.IndexOf('\0')]);
        if (PInvoke.GetDriveType(root) != DriveFixed)
        {
            return "not a fixed drive";
        }

        if (!budget.TryGetValue(root, out var remaining))
        {
            remaining = RemainingCapacity(root);
            budget[root] = remaining;
        }

        if (remaining < 0)
        {
            return "Recycle Bin is off for this drive";
        }

        if (remaining == long.MaxValue)
        {
            return null;
        }

        var size = fs.Measure(path, CancellationToken.None).Bytes;
        if (size > remaining)
        {
            return "too large for the Recycle Bin";
        }

        budget[root] = remaining - size;
        return null;
    }

    /// <summary>
    /// Room left in the drive's Recycle Bin: its configured maximum minus what
    /// it holds. -1 when recycling is switched off (NukeOnDelete), MaxValue
    /// when Windows keeps no explicit setting (the nuke warning still guards).
    /// </summary>
    private static unsafe long RemainingCapacity(string root)
    {
        Span<char> volume = stackalloc char[64];
        if (!PInvoke.GetVolumeNameForVolumeMountPoint(root, volume))
        {
            return long.MaxValue;
        }

        var name = new string(volume[..volume.IndexOf('\0')]);
        var open = name.IndexOf('{');
        var close = name.IndexOf('}');
        if (open < 0 || close <= open)
        {
            return long.MaxValue;
        }

        var guid = name[open..(close + 1)];
        using var key = Registry.CurrentUser.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket\Volume\{guid}");
        if (key is null)
        {
            return long.MaxValue;
        }

        if (key.GetValue("NukeOnDelete") is int nuke && nuke != 0)
        {
            return -1;
        }

        if (key.GetValue("MaxCapacity") is not int capacityMb || capacityMb <= 0)
        {
            return long.MaxValue;
        }

        var info = new SHQUERYRBINFO { cbSize = (uint)sizeof(SHQUERYRBINFO) };
        var used = PInvoke.SHQueryRecycleBin(root, ref info).Succeeded ? info.i64Size : 0;
        return Math.Max(0, (capacityMb * Megabyte) - used);
    }

    private List<RecycleOutcome> RunOnStaThread(IReadOnlyList<string> paths, bool allowElevationPrompt)
    {
        List<RecycleOutcome>? result = null;
        // The shell's copy engine is apartment-threaded.
        var thread = new Thread(() => result = Run(paths, allowElevationPrompt)) { IsBackground = true, Name = "Recycle" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return result ?? paths.Select(p => new RecycleOutcome(p, RecycleStatus.Failed, "recycle thread failed")).ToList();
    }

    private List<RecycleOutcome> Run(IReadOnlyList<string> paths, bool allowElevationPrompt)
    {
        var sink = new Sink();
        IFileOperation? operation = null;
        uint cookie = 0;
        try
        {
            operation = (IFileOperation)new FileOperation();
            var flags = FofAllowUndo | FofNoConfirmation | FofWantNukeWarning | FofSilent | FofNoErrorUi | FofxRecycleOnDelete;
            if (allowElevationPrompt)
            {
                flags |= FofxShowElevationPrompt;
            }

            operation.SetOperationFlags((FILEOPERATION_FLAGS)flags);
            operation.Advise(sink, out cookie);
            foreach (var path in paths)
            {
                if (PInvoke.SHCreateItemFromParsingName(path, null, out IShellItem item).Succeeded)
                {
                    operation.DeleteItem(item, null!);
                }
                else
                {
                    sink.Results[path] = new RecycleOutcome(path, RecycleStatus.Failed, "not found by the shell");
                }
            }

            try
            {
                operation.PerformOperations();
            }
            catch (COMException ex)
            {
                // Per-item results come from the sink; a failure here usually means a skipped item.
                Log.Warn("cleaner", $"Recycle batch reported 0x{ex.HResult:X8}.");
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            Log.Error("cleaner", "The shell's file operation is unavailable.", ex);
        }
        finally
        {
            if (operation is not null && cookie != 0)
            {
                try
                {
                    operation.Unadvise(cookie);
                }
                catch (COMException)
                {
                }
            }
        }

        var outcomes = new List<RecycleOutcome>();
        foreach (var path in paths)
        {
            if (sink.Results.TryGetValue(path, out var reported))
            {
                outcomes.Add(reported.Status == RecycleStatus.Failed && fs.Stat(path) is not null && InUse(path)
                    ? reported with { Status = RecycleStatus.InUse }
                    : reported);
                continue;
            }

            // Not reported by the shell: judge by what is on disk now, never by hope.
            outcomes.Add(fs.Stat(path) is null
                ? new RecycleOutcome(path, RecycleStatus.Missing)
                : new RecycleOutcome(path, InUse(path) ? RecycleStatus.InUse : RecycleStatus.Failed));
        }

        return outcomes;
    }

    /// <summary>A file in use, or a folder with a file in use among its first entries.</summary>
    private bool InUse(string path)
    {
        var entry = fs.Stat(path);
        if (entry is null)
        {
            return false;
        }

        if (!entry.IsDirectory)
        {
            return fs.IsInUse(path);
        }

        var checkedFiles = 0;
        var stack = new Stack<string>();
        stack.Push(path);
        while (stack.Count > 0 && checkedFiles < 200)
        {
            foreach (var child in fs.List(stack.Pop()))
            {
                if (child.IsReparsePoint)
                {
                    continue;
                }

                if (child.IsDirectory)
                {
                    stack.Push(child.Path);
                }
                else if (checkedFiles++ < 200 && fs.IsInUse(child.Path))
                {
                    return true;
                }
            }
        }

        return false;
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class Sink : IFileOperationProgressSink
    {
        public Dictionary<string, RecycleOutcome> Results { get; } = new(StringComparer.OrdinalIgnoreCase);

        public void StartOperations()
        {
        }

        public void FinishOperations(HRESULT hrResult)
        {
        }

        public void PreRenameItem(uint dwFlags, IShellItem psiItem, PCWSTR pszNewName)
        {
        }

        public void PostRenameItem(uint dwFlags, IShellItem psiItem, PCWSTR pszNewName, HRESULT hrRename, IShellItem psiNewlyCreated)
        {
        }

        public void PreMoveItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, PCWSTR pszNewName)
        {
        }

        public void PostMoveItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, PCWSTR pszNewName, HRESULT hrMove, IShellItem psiNewlyCreated)
        {
        }

        public void PreCopyItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, PCWSTR pszNewName)
        {
        }

        public void PostCopyItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, PCWSTR pszNewName, HRESULT hrCopy, IShellItem psiNewlyCreated)
        {
        }

        public void PreDeleteItem(uint dwFlags, IShellItem psiItem)
        {
        }

        public void PostDeleteItem(uint dwFlags, IShellItem psiItem, HRESULT hrDelete, IShellItem psiNewlyCreated)
        {
            var path = PathOf(psiItem);
            if (path is null)
            {
                return;
            }

            Results[path] = hrDelete.Succeeded
                ? new RecycleOutcome(path, psiNewlyCreated is not null ? RecycleStatus.Recycled : RecycleStatus.DeletedPermanently)
                : new RecycleOutcome(path, RecycleStatus.Failed, $"0x{hrDelete.Value:X8}");
        }

        public void PreNewItem(uint dwFlags, IShellItem psiDestinationFolder, PCWSTR pszNewName)
        {
        }

        public void PostNewItem(uint dwFlags, IShellItem psiDestinationFolder, PCWSTR pszNewName, PCWSTR pszTemplateName, uint dwFileAttributes, HRESULT hrNew, IShellItem psiNewItem)
        {
        }

        public void UpdateProgress(uint iWorkTotal, uint iWorkSoFar)
        {
        }

        public void ResetTimer()
        {
        }

        public void PauseTimer()
        {
        }

        public void ResumeTimer()
        {
        }

        private static unsafe string? PathOf(IShellItem item)
        {
            try
            {
                PWSTR name;
                item.GetDisplayName(SIGDN.SIGDN_FILESYSPATH, &name);
                try
                {
                    return name.Value == null ? null : SafePaths.Normalize(new string(name.Value));
                }
                finally
                {
                    PInvoke.CoTaskMemFree(name.Value);
                }
            }
            catch (COMException)
            {
                return null;
            }
        }
    }
}
