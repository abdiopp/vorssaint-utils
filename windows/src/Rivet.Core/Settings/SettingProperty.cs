// SPDX-License-Identifier: GPL-3.0-or-later
using CommunityToolkit.Mvvm.ComponentModel;
using Rivet.Core.Util;

namespace Rivet.Core.Settings;

/// <summary>
/// Bindable view of one setting: <c>IsChecked="{Binding Enabled.Value}"</c>.
/// Change notifications are delivered on the UI thread.
/// </summary>
public sealed class SettingProperty<T> : ObservableObject, IDisposable
{
    private readonly ISettingsStore _store;
    private readonly IDisposable _subscription;

    public SettingProperty(ISettingsStore store, Setting<T> setting)
    {
        _store = store;
        Setting = setting;
        _subscription = store.Observe(setting.Key, () => UiThread.Run(() => OnPropertyChanged(nameof(Value))));
    }

    public Setting<T> Setting { get; }

    public T Value
    {
        get => _store.Get(Setting);
        set => _store.Set(Setting, value);
    }

    /// <summary>Whether a value was explicitly saved.</summary>
    public bool IsSaved => _store.IsSaved(Setting.Key);

    public void Reset() => _store.Reset(Setting.Key);

    public void Dispose() => _subscription.Dispose();
}

public static class SettingPropertyExtensions
{
    public static SettingProperty<T> Bind<T>(this ISettingsStore store, Setting<T> setting) => new(store, setting);
}
