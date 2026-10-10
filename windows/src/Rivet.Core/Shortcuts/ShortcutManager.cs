// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Actions;
using Rivet.Core.Diagnostics;
using Rivet.Core.Features;
using Rivet.Core.Settings;
using Rivet.Core.Util;

namespace Rivet.Core.Shortcuts;

/// <summary>
/// Keeps the registered system hotkeys in line with the roles' gates. While
/// the user records a new shortcut every hotkey is suspended, and every exit
/// path from recording resumes them.
/// </summary>
public sealed class ShortcutManager : IDisposable
{
    private readonly ISettingsStore _settings;
    private readonly FeatureRuntime _runtime;
    private readonly IHotkeyService _hotkeys;
    private readonly ActionRegistry _actions;
    private readonly Dictionary<string, ShortcutRole> _roles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IDisposable> _registrations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ShortcutState> _states = new(StringComparer.Ordinal);
    private readonly List<IDisposable> _subscriptions = [];
    private int _suspendCount;

    public ShortcutManager(ISettingsStore settings, FeatureRuntime runtime, IHotkeyService hotkeys, ActionRegistry actions)
    {
        _settings = settings;
        _runtime = runtime;
        _hotkeys = hotkeys;
        _actions = actions;
        _runtime.Changed += (_, _) => SyncAll();
    }

    public event EventHandler? StatesChanged;

    public IReadOnlyList<ShortcutRole> Roles => _roles.Values.ToList();

    public bool IsSuspended => _suspendCount > 0;

    public void Register(ShortcutRole role)
    {
        _roles[role.Id] = role;
        var keys = role.RequiredEnableKeys.Select(k => k.Key).Append(role.Storage.Key);
        _subscriptions.Add(_settings.Observe(keys, () => UiThread.Run(() => Sync(role))));
        Sync(role);
    }

    public ShortcutRole? Find(string roleId) => _roles.GetValueOrDefault(roleId);

    public KeyChord GetChord(ShortcutRole role) =>
        KeyChord.TryParse(_settings.Get(role.Storage), out var chord) ? chord : role.Default;

    public void SetChord(ShortcutRole role, KeyChord chord) =>
        _settings.Set(role.Storage, chord == role.Default ? string.Empty : chord.ToStorageString());

    public void ResetChord(ShortcutRole role) => _settings.Reset(role.Storage.Key);

    public ShortcutState GetState(ShortcutRole role) => _states.GetValueOrDefault(role.Id, ShortcutState.Inactive);

    /// <summary>Whether the role should be running given its feature and switches.</summary>
    public bool IsWanted(ShortcutRole role) =>
        _runtime.IsAvailable(role.FeatureId) && role.RequiredEnableKeys.All(_settings.Get);

    /// <summary>Another role already using <paramref name="chord"/>.</summary>
    public ShortcutRole? FindConflict(KeyChord chord, ShortcutRole? except, bool includeInactive)
    {
        foreach (var role in _roles.Values)
        {
            if (except is not null && role.Id == except.Id) continue;
            if (!includeInactive && !IsWanted(role)) continue;
            if (GetChord(role) == chord) return role;
        }

        return null;
    }

    /// <summary>Unregisters every hotkey (shortcut recording). Balanced by <see cref="Resume"/>.</summary>
    public void Suspend()
    {
        if (_suspendCount++ == 0)
        {
            foreach (var registration in _registrations.Values)
            {
                registration.Dispose();
            }

            _registrations.Clear();
        }
    }

    public void Resume()
    {
        if (_suspendCount == 0)
        {
            return;
        }

        if (--_suspendCount == 0)
        {
            SyncAll();
        }
    }

    public void SyncAll()
    {
        foreach (var role in _roles.Values)
        {
            Sync(role);
        }
    }

    private void Sync(ShortcutRole role)
    {
        if (_registrations.Remove(role.Id, out var existing))
        {
            existing.Dispose();
        }

        ShortcutState state;
        if (!IsWanted(role))
        {
            state = ShortcutState.Inactive;
        }
        else if (_suspendCount > 0)
        {
            state = ShortcutState.Active;
        }
        else
        {
            var chord = GetChord(role);
            var actionId = role.ActionId;
            var registration = chord.IsEmpty
                ? null
                : _hotkeys.Register(chord, () => _ = _actions.InvokeAsync(actionId, ActionSource.Shortcut), role.Options);
            if (registration is null)
            {
                state = ShortcutState.RegistrationFailed;
                Log.Warn("shortcuts", $"Could not register {chord} for {role.Id}.");
            }
            else
            {
                _registrations[role.Id] = registration;
                state = ShortcutState.Active;
            }
        }

        if (_states.GetValueOrDefault(role.Id) != state || !_states.ContainsKey(role.Id))
        {
            _states[role.Id] = state;
            StatesChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose()
    {
        foreach (var registration in _registrations.Values)
        {
            registration.Dispose();
        }

        _registrations.Clear();
        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }
    }
}
