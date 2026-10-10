// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.App;
using Rivet.Core.Diagnostics;
using Rivet.Core.Settings;
using Rivet.Core.Util;

namespace Rivet.Core.Features;

/// <summary>
/// Brings one feature's services in line with its availability and its own
/// switches. <see cref="Sync"/> must be idempotent: it runs at launch, on
/// install/uninstall and whenever an enable key changes.
/// </summary>
public interface IFeatureController
{
    void Sync(bool available);
}

/// <summary>A controller built from a delegate, for small features.</summary>
public sealed class DelegateFeatureController(Action<bool> sync) : IFeatureController
{
    public void Sync(bool available) => sync(available);
}

/// <summary>
/// Install state of every feature ("availability", a layer above each
/// feature's enable keys). An uninstalled feature never starts at launch and
/// disappears from every surface; its settings are kept so reinstalling
/// restores it exactly. Ported from the macOS <c>FeatureRuntime</c>.
/// </summary>
public sealed class FeatureRuntime
{
    private readonly ISettingsStore _settings;
    private readonly Dictionary<string, List<IFeatureController>> _controllers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Setting<bool>> _availability = new(StringComparer.Ordinal);
    private readonly HashSet<string> _loadedThisSession = new(StringComparer.Ordinal);
    private readonly HashSet<string> _offerableThisSession = new(StringComparer.Ordinal);
    private readonly Func<FeatureDescriptor, bool> _hardwareSupports;
    private bool _launched;

    public FeatureRuntime(ISettingsStore settings, Func<FeatureDescriptor, bool>? hardwareSupports = null)
    {
        _settings = settings;
        _hardwareSupports = hardwareSupports ?? (static _ => true);
        foreach (var feature in FeatureCatalog.All)
        {
            _availability[feature.Id] = new Setting<bool>(feature.AvailabilityKey, feature.InstalledByDefault);
            var captured = feature;
            if (feature.EnableKeys.Count > 0)
            {
                _settings.Observe(feature.EnableKeys.Select(k => k.Key), () =>
                {
                    if (_launched && IsAvailable(captured.Id))
                    {
                        RunControllers(captured.Id);
                    }
                });
            }
        }
    }

    /// <summary>Raised after any availability change (on the UI thread).</summary>
    public event EventHandler? Changed;

    /// <summary>Increments on every change so views can cheaply notice.</summary>
    public int Revision { get; private set; }

    public bool IsAvailable(string featureId) =>
        _availability.TryGetValue(featureId, out var setting) && _settings.Get(setting);

    public bool IsAvailable(FeatureDescriptor feature) => IsAvailable(feature.Id);

    /// <summary>Installed and switched on (or installed with no switch at all).</summary>
    public bool IsEngaged(string featureId)
    {
        var feature = FeatureCatalog.Find(featureId);
        if (feature is null || !IsAvailable(featureId))
        {
            return false;
        }

        return feature.EnableKeys.Count == 0 || feature.EnableKeys.Any(_settings.Get);
    }

    public bool IsAnyAvailable(params string[] featureIds) => featureIds.Any(IsAvailable);

    public int AvailableCount => FeatureCatalog.All.Count(IsAvailable);

    /// <summary>Features that can be installed now (hardware allows it) or already are.</summary>
    public int InstallableCount => FeatureCatalog.All.Count(f => IsAvailable(f) || _hardwareSupports(f));

    public bool CanInstall(FeatureDescriptor feature) => IsAvailable(feature) || _hardwareSupports(feature);

    /// <summary>Some feature loaded this session is now uninstalled; a restart frees it.</summary>
    public bool NeedsRestartToUnload => _loadedThisSession.Any(id => !IsAvailable(id));

    public void RegisterController(string featureId, IFeatureController controller)
    {
        if (!_controllers.TryGetValue(featureId, out var list))
        {
            _controllers[featureId] = list = [];
        }

        list.Add(controller);
        if (_launched)
        {
            SafeSync(featureId, controller, IsAvailable(featureId));
        }
    }

    /// <summary>
    /// On a clean install (not onboarded, no step chosen) write explicit
    /// availability for every feature: Essentials on, the rest off. Runs on
    /// every launch until the user finishes the first-run choice.
    /// </summary>
    public void PrepareFirstRunAvailability()
    {
        if (_settings.Get(ShellSettings.HasOnboarded) || _settings.Get(ShellSettings.OnboardingStep) != 0)
        {
            return;
        }

        var essentials = FeaturePresets.Essentials.Features.ToHashSet(StringComparer.Ordinal);
        foreach (var feature in FeatureCatalog.All)
        {
            _settings.Set(_availability[feature.Id], essentials.Contains(feature.Id));
        }
    }

    /// <summary>Runs the controllers of available features. Unavailable ones never instantiate.</summary>
    public void SyncAtLaunch()
    {
        _launched = true;
        foreach (var feature in FeatureCatalog.All)
        {
            if (IsAvailable(feature))
            {
                _loadedThisSession.Add(feature.Id);
                _offerableThisSession.Add(feature.Id);
                RunControllers(feature.Id);
            }
        }
    }

    /// <summary>Re-runs one feature's controllers (after a permission or option change).</summary>
    public void Sync(string featureId)
    {
        if (_launched)
        {
            RunControllers(featureId);
        }
    }

    /// <summary>
    /// Installs or uninstalls one feature. Installs the hardware cannot
    /// support are refused; uninstalls never are, and an existing install is
    /// never revoked by a later hardware check.
    /// </summary>
    public bool SetAvailable(string featureId, bool available)
    {
        var feature = FeatureCatalog.Get(featureId);
        if (available && !CanInstall(feature))
        {
            return false;
        }

        if (IsAvailable(featureId) == available)
        {
            return true;
        }

        if (available)
        {
            EnableOnFirstInstall(feature);
            _loadedThisSession.Add(featureId);
            _offerableThisSession.Remove(featureId);
        }

        _settings.Set(_availability[featureId], available);
        RunControllers(featureId);
        AfterChange();
        return true;
    }

    /// <summary>
    /// The selected set becomes the installed set (presets, onboarding).
    /// <paramref name="enableKeys"/> are switched on first; features that newly
    /// join also get first-install enabling.
    /// </summary>
    public void ReplaceAvailable(IEnumerable<string> selected, IEnumerable<Setting<bool>> enableKeys)
    {
        foreach (var key in enableKeys)
        {
            _settings.Set(key, true);
        }

        var target = selected.ToHashSet(StringComparer.Ordinal);
        foreach (var feature in FeatureCatalog.All)
        {
            var want = target.Contains(feature.Id) && CanInstall(feature);
            var have = IsAvailable(feature);
            if (want == have)
            {
                continue;
            }

            if (want)
            {
                EnableOnFirstInstall(feature);
                _loadedThisSession.Add(feature.Id);
                _offerableThisSession.Remove(feature.Id);
            }

            _settings.Set(_availability[feature.Id], want);
        }

        foreach (var feature in FeatureCatalog.All)
        {
            RunControllers(feature.Id);
        }

        AfterChange();
    }

    /// <summary>Installs everything the hardware supports, without switching anything on.</summary>
    public void InstallAll()
    {
        foreach (var feature in FeatureCatalog.All.Where(f => !IsAvailable(f) && CanInstall(f)))
        {
            _settings.Set(_availability[feature.Id], true);
            _loadedThisSession.Add(feature.Id);
            _offerableThisSession.Remove(feature.Id);
            RunControllers(feature.Id);
        }

        AfterChange();
    }

    public void UninstallAll()
    {
        foreach (var feature in FeatureCatalog.All.Where(IsAvailable))
        {
            _settings.Set(_availability[feature.Id], false);
            RunControllers(feature.Id);
        }

        AfterChange();
    }

    /// <summary>
    /// Installed features whose switches are off and were never saved on this
    /// PC, minus the ones the user chose to keep. The hub offers them as a
    /// batch only when at least three qualify.
    /// </summary>
    public IReadOnlyList<FeatureDescriptor> NeverSwitchedOn()
    {
        var kept = KeptFeatures();
        return FeatureCatalog.All
            .Where(f => f.OfferWhenNeverSwitchedOn
                        && IsAvailable(f)
                        && f.EnableKeys.Count > 0
                        && !f.EnableKeys.Any(_settings.Get)
                        && !f.EnableKeys.Any(k => _settings.IsSaved(k.Key))
                        && _offerableThisSession.Contains(f.Id)
                        && !kept.Contains(f.Id))
            .ToList();
    }

    public void KeepFeatures(IEnumerable<string> featureIds)
    {
        var kept = KeptFeatures();
        kept.UnionWith(featureIds);
        _settings.Set(ShellSettings.FeatureHubKeptFeatures, string.Join(',', kept.OrderBy(k => k, StringComparer.Ordinal)));
    }

    private HashSet<string> KeptFeatures() =>
        _settings.Get(ShellSettings.FeatureHubKeptFeatures)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// A feature installed for the first time switches on its primary key, but
    /// only if none of its keys was ever saved on this PC.
    /// </summary>
    private void EnableOnFirstInstall(FeatureDescriptor feature)
    {
        if (feature.EnableKeys.Any(k => _settings.IsSaved(k.Key)))
        {
            return;
        }

        foreach (var key in feature.EffectiveInitialEnableKeys)
        {
            _settings.Set(key, true);
        }
    }

    private void RunControllers(string featureId)
    {
        if (!_controllers.TryGetValue(featureId, out var list))
        {
            return;
        }

        var available = IsAvailable(featureId);
        foreach (var controller in list.ToArray())
        {
            SafeSync(featureId, controller, available);
        }
    }

    private static void SafeSync(string featureId, IFeatureController controller, bool available)
    {
        try
        {
            controller.Sync(available);
        }
        catch (Exception ex)
        {
            Log.Error("features", $"Controller for '{featureId}' failed to sync.", ex);
        }
    }

    private void AfterChange()
    {
        Revision++;
        UiThread.Run(() => Changed?.Invoke(this, EventArgs.Empty));
    }
}
