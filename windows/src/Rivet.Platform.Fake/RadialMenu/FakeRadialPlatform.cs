// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Extensions.DependencyInjection;
using Rivet.Core.Diagnostics;
using Rivet.Core.Modules;
using Rivet.Core.Modules.RadialMenu;
using Rivet.Core.Platform;

namespace Rivet.Platform.Fake.RadialMenu;

/// <summary>Logs what a slice would do; file paths are checked against the host file system.</summary>
public sealed class FakeRadialPlatform : IRadialPlatform
{
    public List<string> Log { get; } = [];

    public int Beeps { get; private set; }

    public bool LaunchOrActivateApp(string path)
    {
        Log.Add("app:" + path);
        Core.Diagnostics.Log.Info("radial", $"[fake] launch {path}");
        return true;
    }

    public bool OpenPath(string path)
    {
        var expanded = ExpandPath(path);
        Log.Add("open:" + expanded);
        return File.Exists(expanded) || Directory.Exists(expanded);
    }

    public void OpenUrl(string url) => Log.Add("url:" + url);

    public string DisplayName(string path)
    {
        var trimmed = path.TrimEnd('/', '\\');
        var name = trimmed[(trimmed.LastIndexOfAny(['/', '\\']) + 1)..];
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    public PixelBuffer? Icon(string path, int sizePx) => null;

    public nint ForegroundWindow() => 0;

    public void Activate(nint hwnd)
    {
    }

    public bool ApplyWindowLayout(nint hwnd, string layoutId)
    {
        Log.Add("layout:" + layoutId);
        return hwnd != 0;
    }

    public string ExpandPath(string path)
    {
        var expanded = Environment.ExpandEnvironmentVariables(path);
        if (expanded.StartsWith('~'))
        {
            expanded = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + expanded[1..].Replace('\\', '/');
        }

        return expanded;
    }

    public void Beep()
    {
        Beeps++;
        Core.Diagnostics.Log.Info("radial", "[fake] beep");
    }
}

/// <summary>Reports a simulated playing track so the Now Playing slice and card can be seen.</summary>
public sealed class FakeNowPlaying : INowPlayingService
{
    public NowPlayingSnapshot? Current { get; set; } = new()
    {
        Title = "Simulated Song",
        Artist = "Rivet Band",
        Album = "Development Build",
        AppName = "Media Player",
        AppId = "Microsoft.ZuneMusic",
        IsPlaying = true,
    };

    public Task<NowPlayingSnapshot?> GetAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(NowPlayingSanitizer.Sanitize(Current));

    public bool Activate(NowPlayingSnapshot snapshot)
    {
        Log.Info("radial", $"[fake] activate {snapshot.AppId}");
        return true;
    }
}

public sealed class RadialMenuFakeRegistrar : IPlatformRegistrar
{
    public void Register(IServiceCollection services)
    {
        services.AddSingleton<FakeRadialPlatform>();
        services.AddSingleton<IRadialPlatform>(sp => sp.GetRequiredService<FakeRadialPlatform>());
        services.AddSingleton<FakeNowPlaying>();
        services.AddSingleton<INowPlayingService>(sp => sp.GetRequiredService<FakeNowPlaying>());
    }
}
