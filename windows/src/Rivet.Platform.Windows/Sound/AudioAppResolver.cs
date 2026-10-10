// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using Rivet.Core.Diagnostics;
using Rivet.Core.Platform;
using Rivet.Core.Sound;
using Rivet.Imaging.Skia;
using SkiaSharp;

namespace Rivet.Platform.Windows.Sound;

/// <summary>
/// Who owns an audio session (spec §3.9.8): a packaged app by its
/// AppUserModelID, else the executable by its full path (lowercase), which
/// also groups Chromium/Electron audio helpers with their app. Display name
/// from the version resource, else the session's own name, else the file
/// name. Icons load in the background and arrive with a later publish.
/// Used only on the audio thread.
/// </summary>
internal sealed class AudioAppResolver
{
    private readonly AudioThread _thread;
    private readonly Action _changed;
    private readonly Dictionary<int, ProcessIdentity> _processes = [];
    private readonly Dictionary<string, AppDetails> _details = new(StringComparer.Ordinal);

    public AudioAppResolver(AudioThread thread, Action changed)
    {
        _thread = thread;
        _changed = changed;
    }

    public AudioAppInfo Resolve(int processId, string? sessionDisplayName, string? sessionIconPath)
    {
        if (!_processes.TryGetValue(processId, out var identity))
        {
            identity = Identify(processId, sessionDisplayName);
            _processes[processId] = identity;
        }

        if (identity.PersistenceId is not { } key)
        {
            return new AudioAppInfo { GroupId = $"process:{processId}", DisplayName = identity.FallbackName };
        }

        if (!_details.TryGetValue(key, out var details))
        {
            details = new AppDetails(DisplayNameFor(identity, sessionDisplayName), null);
            _details[key] = details;
            LoadIcon(key, identity, sessionIconPath);
        }

        return new AudioAppInfo
        {
            PersistenceId = key,
            GroupId = key,
            DisplayName = details.DisplayName,
            ExecutablePath = identity.ExecutablePath,
            Icon = details.Icon,
        };
    }

    /// <summary>Drops cached identities of processes that no longer own a session (pids are reused).</summary>
    public void Prune(IEnumerable<int> liveProcessIds)
    {
        var live = liveProcessIds.ToHashSet();
        foreach (var pid in _processes.Keys.Where(p => !live.Contains(p)).ToList())
        {
            _processes.Remove(pid);
        }
    }

    private static ProcessIdentity Identify(int processId, string? sessionDisplayName)
    {
        var handle = SoundNative.OpenProcess(SoundNative.ProcessQueryLimitedInformation, 0, (uint)processId);
        if (handle == 0)
        {
            // Protected or gone: the process name is still readable without a handle.
            var name = ProcessName(processId);
            return name is null
                ? new ProcessIdentity(null, null, null, Clean(sessionDisplayName) ?? $"pid {processId}")
                : new ProcessIdentity((name + ".exe").ToLowerInvariant(), null, null, name);
        }

        try
        {
            var aumid = ApplicationUserModelId(handle);
            var path = ImagePath(handle);
            if (aumid is not null)
            {
                return new ProcessIdentity(aumid, path, aumid, Clean(sessionDisplayName) ?? aumid.Split('!')[^1]);
            }

            if (path is not null)
            {
                return new ProcessIdentity(path.ToLowerInvariant(), path, null, Path.GetFileNameWithoutExtension(path));
            }

            var name = ProcessName(processId) ?? $"pid {processId}";
            return new ProcessIdentity(name == $"pid {processId}" ? null : (name + ".exe").ToLowerInvariant(), null, null, name);
        }
        finally
        {
            SoundNative.CloseHandle(handle);
        }
    }

    private static string DisplayNameFor(ProcessIdentity identity, string? sessionDisplayName)
    {
        if (identity.Aumid is { } aumid)
        {
            try
            {
                var name = global::Windows.ApplicationModel.AppInfo.GetFromAppUserModelId(aumid)?.DisplayInfo?.DisplayName;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    return name.Trim();
                }
            }
            catch (Exception ex)
            {
                Log.Debug("sound", $"No package display name for {aumid}: {ex.Message}");
            }

            return identity.FallbackName;
        }

        if (identity.ExecutablePath is { } path)
        {
            try
            {
                var description = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim();
                if (!string.IsNullOrEmpty(description))
                {
                    return description;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
            }
        }

        return Clean(sessionDisplayName) ?? identity.FallbackName;
    }

    private void LoadIcon(string key, ProcessIdentity identity, string? sessionIconPath)
    {
        _ = Task.Run(async () =>
        {
            PixelBuffer? icon = null;
            try
            {
                icon = identity.Aumid is { } aumid
                    ? await PackageLogoAsync(aumid).ConfigureAwait(false)
                    : IconExtractor.FromLocation(sessionIconPath) ?? IconExtractor.FromLocation(identity.ExecutablePath);
            }
            catch (Exception ex)
            {
                Log.Debug("sound", $"No icon for {key}: {ex.Message}");
            }

            if (icon is null)
            {
                return;
            }

            _thread.Post(() =>
            {
                if (_details.TryGetValue(key, out var details))
                {
                    _details[key] = details with { Icon = icon };
                    _changed();
                }
            });
        });
    }

    private static async Task<PixelBuffer?> PackageLogoAsync(string aumid)
    {
        var info = global::Windows.ApplicationModel.AppInfo.GetFromAppUserModelId(aumid);
        var reference = info?.DisplayInfo?.GetLogo(new global::Windows.Foundation.Size(32, 32));
        if (reference is null)
        {
            return null;
        }

        using var stream = await reference.OpenReadAsync();
        using var managed = stream.AsStreamForRead();
        using var memory = new MemoryStream();
        await managed.CopyToAsync(memory).ConfigureAwait(false);
        using var decoded = SKBitmap.Decode(memory.ToArray());
        if (decoded is null)
        {
            return null;
        }

        using var resized = decoded.Width == 32 && decoded.Height == 32
            ? decoded.Copy()
            : decoded.Resize(new SKImageInfo(32, 32, SKColorType.Bgra8888, SKAlphaType.Premul), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        return resized is null ? null : SkiaConvert.ToPixelBuffer(resized);
    }

    private static unsafe string? ApplicationUserModelId(nint process)
    {
        uint length = 256;
        var buffer = stackalloc char[256];
        var result = SoundNative.GetApplicationUserModelId(process, ref length, buffer);
        if (result == SoundNative.ErrorInsufficientBuffer && length is > 256 and < 4096)
        {
            var large = new char[length];
            fixed (char* chars = large)
            {
                result = SoundNative.GetApplicationUserModelId(process, ref length, chars);
                return result == 0 ? new string(chars, 0, (int)Math.Max(0, length - 1)).TrimEnd('\0') : null;
            }
        }

        return result == 0 ? new string(buffer, 0, (int)Math.Max(0, length - 1)).TrimEnd('\0') : null;
    }

    private static unsafe string? ImagePath(nint process)
    {
        uint size = 1024;
        var buffer = stackalloc char[1024];
        return SoundNative.QueryFullProcessImageName(process, 0, buffer, ref size) && size > 0 ? new string(buffer, 0, (int)size) : null;
    }

    private static string? ProcessName(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>Session display names can be indirect resource strings ("@%SystemRoot%\…"); those are skipped.</summary>
    private static string? Clean(string? name) =>
        string.IsNullOrWhiteSpace(name) || name.TrimStart().StartsWith('@') ? null : name.Trim();

    private sealed record ProcessIdentity(string? PersistenceId, string? ExecutablePath, string? Aumid, string FallbackName);

    private sealed record AppDetails(string DisplayName, PixelBuffer? Icon);
}
