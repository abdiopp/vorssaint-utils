// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Platform;

namespace Rivet.Core.Modules.Scratchpad;

/// <summary>
/// Window facts the floating tools need for outside-click dismissal (the
/// scratchpad, and the camera preview reuses it): clicks on the on-screen
/// keyboard (osk.exe or the touch keyboard) must not close a pad the user is typing into.
/// </summary>
public interface IScratchpadPlatform
{
    bool IsOnScreenKeyboardAt(PixelPoint point);
}
