// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Diagnostics;
using Rivet.Core.Features;

namespace Rivet.Core.Actions;

/// <summary>Where an action was started from (some actions behave slightly differently).</summary>
public enum ActionSource
{
    Panel,
    Shortcut,
    Tray,
    CommandBar,
    QuickPanel,
    RadialMenu,
    Island,
    Other,
}

public sealed record ActionContext(ActionSource Source);

/// <summary>
/// Something the user can run: a tool launch ("screenshot.capture"), a
/// toggle, a page to open. Panel tiles, shortcuts, the tray menu, the
/// Command Bar, the quick panel and the radial menu all invoke actions by id.
/// </summary>
public sealed record AppAction
{
    public required string Id { get; init; }

    /// <summary>Owning feature; the action is hidden and inert while the feature is uninstalled.</summary>
    public required string FeatureId { get; init; }

    public required string TitleKey { get; init; }

    public string? SubtitleKey { get; init; }

    /// <summary>Fluent UI System Icons symbol name.</summary>
    public required string Icon { get; init; }

    /// <summary>Extra search words (fixed English tokens match in every language).</summary>
    public IReadOnlyList<string> Keywords { get; init; } = [];

    /// <summary>Opens an overlay or window, so the panel should close first.</summary>
    public bool ClosesPanel { get; init; } = true;

    /// <summary>Hides the action from lists (Command Bar, quick panel, radial menu) while false, e.g. "Stop recording" when idle.</summary>
    public Func<bool>? IsVisible { get; init; }

    /// <summary>For on/off actions: whether the thing is on now, so lists can show it lit (quick panel live dot).</summary>
    public Func<bool>? IsOn { get; init; }

    public required Func<ActionContext, Task> Run { get; init; }
}

public sealed class ActionRegistry(FeatureRuntime runtime)
{
    private readonly Dictionary<string, AppAction> _actions = new(StringComparer.Ordinal);

    public event EventHandler? Changed;

    public void Register(AppAction action)
    {
        _actions[action.Id] = action;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public AppAction? Get(string id) => _actions.GetValueOrDefault(id);

    /// <summary>Actions whose feature is installed.</summary>
    public IReadOnlyList<AppAction> Available => _actions.Values.Where(a => IsEnabled(a) && (a.IsVisible?.Invoke() ?? true)).ToList();

    /// <summary>Actions with an empty feature id belong to the shell and are always available.</summary>
    public bool IsEnabled(AppAction action) => action.FeatureId.Length == 0 || runtime.IsAvailable(action.FeatureId);

    public IReadOnlyList<AppAction> All => _actions.Values.ToList();

    public async Task<bool> InvokeAsync(string id, ActionSource source)
    {
        if (!_actions.TryGetValue(id, out var action))
        {
            Log.Warn("actions", $"Unknown action '{id}'.");
            return false;
        }

        if (!IsEnabled(action))
        {
            return false;
        }

        try
        {
            await action.Run(new ActionContext(source)).ConfigureAwait(true);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("actions", $"Action '{id}' failed.", ex);
            return false;
        }
    }
}
