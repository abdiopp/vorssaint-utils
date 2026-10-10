// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Shortcuts;
using Windows.Win32;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Rivet.Platform.Windows.Shell;

/// <summary>Key labels from the active keyboard layout (letters and punctuation), with fixed names for the rest.</summary>
public sealed unsafe class WindowsKeyNames : IKeyNameProvider
{
    public string NameOf(int virtualKey)
    {
        var printable = virtualKey is >= VirtualKeys.A and <= VirtualKeys.Z
            or >= VirtualKeys.D0 and <= VirtualKeys.D9
            or >= VirtualKeys.OemSemicolon and <= VirtualKeys.OemTilde
            or >= VirtualKeys.OemOpenBrackets and <= VirtualKeys.OemQuotes
            or 0xE2;
        if (!printable)
        {
            return VirtualKeys.DefaultName(virtualKey);
        }

        var layout = PInvoke.GetKeyboardLayout(0);
        var scan = PInvoke.MapVirtualKeyEx((uint)virtualKey, MAP_VIRTUAL_KEY_TYPE.MAPVK_VK_TO_VSC, layout);
        var state = stackalloc byte[256];
        var buffer = stackalloc char[8];
        // Flag 0x4: do not change the keyboard state (dead keys stay pending in the real input).
        var count = PInvoke.ToUnicodeEx((uint)virtualKey, scan, state, buffer, 8, 0x4, layout);
        if (count > 0)
        {
            var text = new string(buffer, 0, count).Trim();
            if (text.Length > 0 && !char.IsControl(text[0]))
            {
                return text.ToUpperInvariant();
            }
        }

        return VirtualKeys.DefaultName(virtualKey);
    }
}
