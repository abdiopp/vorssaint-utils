// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Settings;

namespace Rivet.Core.Maintenance.Processes;

/// <summary>Kill Process and Port Manager preferences (keys match the macOS app).</summary>
public static class ProcessSettings
{
    public static readonly Setting<bool> CommandBarEnabled = new("killProcessCommandBarEnabled", true);

    public static readonly Setting<bool> GroupRelated = new("killProcessGroupRelated", true);

    public static readonly Setting<string> SortBy =
        new("killProcessSortBy", "cpu", Sanitize.OneOfStrings("cpu", "cpu", "memory", "name", "pid"));

    public static readonly Setting<bool> SortAscending = new("killProcessSortAscending", false);

    /// <summary>Windows only: also list UDP sockets (macOS lists TCP listeners only).</summary>
    public static readonly Setting<bool> PortManagerShowUdp = new("portManagerShowUdp", false);
}
