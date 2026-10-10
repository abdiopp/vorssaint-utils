// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.App.Modules;

/// <summary>What feature modules may ask of the app shell.</summary>
public interface IAppShell
{
    /// <summary>Opens Settings, optionally on a page (and revealing a feature in the Features hub).</summary>
    void OpenSettings(string? pageId = null, string? revealFeatureId = null);

    /// <summary>Shows the tray panel, optionally on a section.</summary>
    void ShowPanel(string? sectionId = null);

    void ClosePanel();

    bool IsPanelOpen { get; }

    /// <summary>
    /// While any holder is registered, clicks in other apps do not close the
    /// panel (scans, confirmations). Dispose the token to release it.
    /// </summary>
    IDisposable KeepPanelOpen(string reason);

    /// <summary>Quits the app (restoring every system setting the app changed).</summary>
    void Quit();
}
