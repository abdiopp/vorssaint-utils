// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Extensions.DependencyInjection;
using Rivet.Core.Diagnostics;
using Rivet.Core.Modules;
using Rivet.Core.Modules.CleaningMode;

namespace Rivet.Platform.Fake.CleaningMode;

/// <summary>Development stand-in: no session or foreground events; tests can raise them.</summary>
public sealed class FakeCleaningPlatform : ICleaningPlatform
{
    private readonly List<Action<SessionChange>> _session = [];
    private readonly List<Action<nint>> _foreground = [];

    public int Beeps { get; private set; }

    public IDisposable WatchSession(Action<SessionChange> changed)
    {
        _session.Add(changed);
        return new Token(() => _session.Remove(changed));
    }

    public IDisposable WatchForeground(Action<nint> changed)
    {
        _foreground.Add(changed);
        return new Token(() => _foreground.Remove(changed));
    }

    public string? ProcessPathOf(nint hwnd) => null;

    public int? LastInputTick() => null;

    public void Beep()
    {
        Beeps++;
        Log.Info("cleaning", "[fake] beep");
    }

    public void RaiseSession(SessionChange change)
    {
        foreach (var handler in _session.ToArray())
        {
            handler(change);
        }
    }

    private sealed class Token(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}

public sealed class CleaningModeFakeRegistrar : IPlatformRegistrar
{
    public void Register(IServiceCollection services)
    {
        services.AddSingleton<FakeCleaningPlatform>();
        services.AddSingleton<ICleaningPlatform>(sp => sp.GetRequiredService<FakeCleaningPlatform>());
    }
}
