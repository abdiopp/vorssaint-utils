// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Diagnostics;
using Rivet.Core.Input;
using Rivet.Core.Shortcuts;
using Rivet.Platform.Windows.Input.SmoothScroll;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Rivet.Platform.Windows.Input.SuperKey;

/// <summary>
/// Keyboard facts for the Super key and key debounce: the repeat delay and
/// rate (Control Panel › Keyboard), the Caps Lock state, physical key state
/// through Raw Input, layout-aware scan-code names, and switching to the next
/// keyboard layout of the foreground window (WM_INPUTLANGCHANGEREQUEST, which
/// Windows applies asynchronously: the very next keystroke may still use the
/// old layout).
/// </summary>
public sealed unsafe class WindowsKeyboardInfo : IKeyboardInfo
{
    private readonly WindowsRawInput _rawInput;

    public WindowsKeyboardInfo(WindowsRawInput rawInput)
    {
        _rawInput = rawInput;
    }

    public int RepeatDelayMs
    {
        get
        {
            uint value = 1;
            // SPI_GETKEYBOARDDELAY: 0 (≈250 ms) … 3 (≈1 s).
            return PInvoke.SystemParametersInfo(SYSTEM_PARAMETERS_INFO_ACTION.SPI_GETKEYBOARDDELAY, 0, &value, 0)
                ? (int)(Math.Min(value, 3) + 1) * 250
                : 500;
        }
    }

    public int RepeatIntervalMs
    {
        get
        {
            uint value = 31;
            // SPI_GETKEYBOARDSPEED: 0 (≈2.5 repeats/s) … 31 (≈30 repeats/s).
            if (!PInvoke.SystemParametersInfo(SYSTEM_PARAMETERS_INFO_ACTION.SPI_GETKEYBOARDSPEED, 0, &value, 0))
            {
                return 33;
            }

            var rate = 2.5 + (Math.Min(value, 31) * 27.5 / 31);
            return (int)Math.Round(1000 / rate);
        }
    }

    public bool IsCapsLockOn => (PInvoke.GetKeyState(VirtualKeys.Capital) & 1) != 0;

    public bool? IsPhysicallyDown(int virtualKey) => _rawInput.IsKeyDown(virtualKey);

    public IDisposable TrackPhysicalKeys() => _rawInput.TrackKeyboard();

    public void SelectNextInputSource()
    {
        try
        {
            var window = PInvoke.GetForegroundWindow();
            if (window.IsNull)
            {
                return;
            }

            var thread = PInvoke.GetWindowThreadProcessId(window, null);
            var current = PInvoke.GetKeyboardLayout(thread);
            var count = PInvoke.GetKeyboardLayoutList(0, null);
            if (count < 2)
            {
                return;
            }

            var layouts = new HKL[count];
            fixed (HKL* list = layouts)
            {
                count = PInvoke.GetKeyboardLayoutList(count, list);
            }

            var handles = layouts.Take(count).Select(h => (nint)h.Value).ToList();
            var next = InputSourceCycle.Next<nint>(handles, current.IsNull ? null : (nint)current.Value);
            if (next is { } hkl)
            {
                PInvoke.PostMessage(window, PInvoke.WM_INPUTLANGCHANGEREQUEST, default, new LPARAM(hkl));
            }
        }
        catch (Exception ex)
        {
            Log.Warn("input", "Could not switch the input source.", ex);
        }
    }

    public int ScanToVirtualKey(int scanId)
    {
        var window = PInvoke.GetForegroundWindow();
        var layout = PInvoke.GetKeyboardLayout(window.IsNull ? 0 : PInvoke.GetWindowThreadProcessId(window, null));
        var code = (uint)(scanId & 0xFF) | ((scanId & 0x100) != 0 ? 0xE000u : 0u);
        return (int)PInvoke.MapVirtualKeyEx(code, MAP_VIRTUAL_KEY_TYPE.MAPVK_VSC_TO_VK_EX, layout);
    }
}
