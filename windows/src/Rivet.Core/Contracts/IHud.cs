// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Core.Contracts;

public enum HudStyle
{
    Info,
    Success,
    Warning,
    Error,
}

/// <summary>
/// Short floating messages ("Copied", "Saved to Pictures") near the bottom
/// centre of the active screen. Implemented by the app shell.
/// </summary>
public interface IHud
{
    void Show(string message, HudStyle style = HudStyle.Info, string? icon = null, TimeSpan? duration = null);
}
