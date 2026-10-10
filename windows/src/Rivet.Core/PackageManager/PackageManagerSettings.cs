// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Settings;

namespace Rivet.Core.Maintenance.PackageManager;

/// <summary>Package manager (winget) preferences. New on Windows; Homebrew keys have no meaning here.</summary>
public static class PackageManagerSettings
{
    /// <summary>
    /// The person accepted winget's source agreements in the app (the
    /// Microsoft Store source's terms, which also send the PC's two-letter
    /// region). Until then nothing passes --accept-source-agreements.
    /// Per machine: asked again on another PC.
    /// </summary>
    public static readonly Setting<bool> SourceAgreementsAccepted = new("wingetSourceAgreementsAccepted", false, machineState: true);

    /// <summary>Source filter of the package lists: all, winget, msstore or local (installed only).</summary>
    public static readonly Setting<string> SourceFilter =
        new("packageManagerSourceFilter", "all", Sanitize.OneOfStrings("all", "all", "winget", "msstore", "local"));
}
