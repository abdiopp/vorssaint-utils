# Displays (Windows)

Spec: `docs/specs/03-system-monitor.md` §3.19, §4.7, §6.13, §7 (brightness rows),
§8.4; `docs/specs/05-app-shell.md` §3.4.3 and §6.1 (shortcuts). Module class:
`Rivet.App.Features.Displays.DisplaysModule`. Feature id: `brightness`, enable key
`brightnessControlEnabled` ("Control displays", default off).

Code: `src/Rivet.Core/Displays` (service, math, worker, settings, platform
interfaces), `src/Rivet.Platform.Windows/Displays`, `src/Rivet.Platform.Fake/Displays`,
`src/Rivet.App/Features/Displays` (section, row, Settings page, OSD, overlays),
tests in `tests/Rivet.Core.Tests/Displays` (≈ 40 cases) and
`tests/Rivet.App.Tests/Displays` (snapshots, registrations, shortcut stepping).

## Status

| Area | State |
|---|---|
| Display list with friendly names, built-in first then left to right, mirrors folded, virtual displays skipped | Done |
| Laptop panel brightness through WMI (`WmiMonitorBrightness` / `WmiSetBrightness`) | Done |
| External monitors through DDC/CI (dxva2, VCP 0x10): live / write-only / dead classification, write-only cache | Done |
| Software dimming: click-through, capture-excluded black overlay per display | Done |
| "Dim the picture" (forced software) and "Extra dimming" (extended) choices | Done |
| Serial worker, slider coalescing, ≥ 50 ms DDC pacing, 0.5 s settle, 3 s wake settle, 3 s trust window | Done |
| Brightness OSD (196 × 154, 16 segments, 0.1 / 1.0 / 0.2 s) | Done |
| Shortcuts `displayBrightnessDecrease` / `Increase` (Ctrl+Alt+Win+− / =), key steps, follow the pointer | Done |
| Panel section `brightness` (Order 5, visible only while "Control displays" is on) and Settings page `displays` | Done |
| Display on/off, keyboard backlight, extra brightness (XDR), brightness-key interception | Not ported (see below) |

## Implemented

* **`BrightnessService`** (Core) owns every display's route and level. All
  hardware work runs on `SerialBrightnessWorker` (one background thread), so
  enumeration, WMI and DDC calls (40–100 ms each) never touch the UI thread
  and commands to a monitor never overlap. Snapshots (`DisplayStatus`) are
  published to the UI thread with a version check so an older snapshot never
  overwrites a newer one.
* **Rebuilds** happen on start, when the panel or Settings appear (`Refresh`,
  at most every 2 s, keeps probes and re-reads readable levels), 0.5 s after
  the first `WM_DISPLAYCHANGE` of a storm, and 3 s after resume
  (`PBT_APMRESUMEAUTOMATIC`). A display number now used by another monitor
  (different path key) is probed again and never inherits the old level.
* **Routes**:
  1. *System*: the built-in panel (`DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL`,
     eDP, UDI embedded, LVDS) when WMI answers. The WMI instance is paired
     with the display through its device path; a single instance pairs with
     the panel. The first "not supported" answer stops further WMI queries.
  2. *DDC*: external displays with a physical monitor handle
     (`GetPhysicalMonitorsFromHMONITOR`). The probe reads VCP 0x10
     (`GetVCPFeatureAndVCPFeatureReply`): a reply → **live**; no reply →
     **unknown** (slider seeded from this session's last value, else 50 %),
     and the first write decides: accepted → **write-only** (the connection is
     remembered in `brightnessDDCWriteOnlyPaths`, ≤ 16), rejected → **dead**,
     the display moves to the software route without losing the requested
     level. A failed write on a write-only connection forgets the verdict
     and rebuilds.
  3. *Software*: everything else (HDMI adapters, TVs, panels without WMI).
* **Writes**: `SetLevel` clamps, publishes the new value at once and queues
  one pending write per display, so a slider drag folds into one write of
  the newest value. DDC value = `round(level × max)` (max 0 → 100), up to 4
  retries 20 ms apart inside the channel, and at least 50 ms between whole
  commands to one monitor. Panel writes go to WMI in whole percent.
* **Key steps**: standard 1/16, half 1/32, quarter 1/64 (`brightnessKeyStep`);
  levels between grid points snap to the next grid point. Inside the 3 s
  trust window a step uses the remembered level; after a pause a readable
  display (WMI panel, live DDC) is read first on the worker, and presses
  that arrive meanwhile are added after the read.
* **Extra dimming** (`brightnessExtendedDimmingPaths`, live DDC only):
  `level < 0.25` → hardware 0, picture `level / 0.25`; otherwise hardware
  `(level − 0.25) / 0.75`, picture 1. The picture is restored before any
  brighter hardware write, a failed write never raises the hardware level,
  and drags inside the software range send no DDC command.
* **Dim the picture** (`brightnessForcedSoftwarePaths`, offered for
  write-only/unknown DDC monitors): moves the display to the overlay;
  turning it off removes the overlay, forgets the write-only verdict and
  probes again.
* **Reconnection**: a dimmed display that disappears and returns is
  re-dimmed with at least 25 % so the picture is never stuck black.
* **Overlays** (`DimmingOverlays`, UI thread): one borderless, top-most,
  no-activate, click-through, capture-excluded window per dimmed display,
  sized to the monitor in its own DPI; opacity = `(1 − factor) × 0.92`.
* **OSD** (`BrightnessOsd`): centred on the display being changed, sun icon,
  percentage, 16 segments filled `ceil(level × 16)`; fades in 0.1 s, stays
  1.0 s after the last change, fades out 0.2 s; click-through, no
  activation, excluded from capture. Shown for slider and shortcut changes
  when "Show brightness when adjusting" is on.
* **Shortcuts**: roles `displayBrightnessDecrease` / `displayBrightnessIncrease`
  (actions `displayBrightness.decrease` / `.increase`), default
  Ctrl+Alt+Win+− / Ctrl+Alt+Win+=, registered only while "Control displays"
  and "Use display brightness shortcuts" are on. They act on the primary
  display, or on the display under the pointer when "Shortcuts follow the
  pointer" (`brightnessKeysEnabled`) is on.
* **UI**: panel section (row per display: icon, name, route caption, "NN %",
  slider, dimming choice chip, red error line; "Show brightness when
  adjusting"; Options with pointer following and the shortcut switch with
  the current chords; "No display found." / "Looking for displays…").
  Settings page: "Control displays", the same rows with wider sliders and
  explanations, More options (OSD, follow pointer, key steps), the shortcut
  switch and both roles, and the spec's protocol caption.

## Not implemented (and why)

| Item | Reason |
|---|---|
| Display on/off (§3.19.6) | Deferred by the spec: Windows can only detach a display by changing the topology (`SetDisplayConfig`), or put a monitor in standby with DDC VCP 0xD6, which many monitors treat as "no signal" and some never wake from. Without the restore journal being proven on hardware this is too risky. Rows have no power button. |
| Inactive displays shown as "Off" | Windows enumerates active monitors only; detached displays are not listed. |
| Brightness keys follow the pointer / key interception / quarter-step forwarding | Dropped by the spec: laptop brightness keys are handled by firmware and Windows and cannot be intercepted reliably. The setting key now means "the shortcuts follow the pointer". |
| Eased ("smooth") panel steps | WMI has no smooth API; panel writes land at once (`WmiSetBrightness` timeout 0). |
| Keyboard backlight slider and shortcuts | Dropped: vendor-specific only. |
| Extra brightness (XDR) | Dropped: Mac-only hardware. |
| Reacting to brightness changed outside the app (Fn keys, Windows quick settings) | Not subscribed to `WmiMonitorBrightnessEvent`; the level is re-read whenever the panel or Settings open and before a shortcut step after 3 s. |
| Gamma-ramp dimming | Not used: Windows rejects ramps far from identity without an admin registry change and ignores them in HDR. Overlays are used instead. |

## Deviations from macOS

* **Software dimming never reaches pure black**: the darkest overlay is 92 %
  opaque (the spec's gamma route scales to 0). A black, click-through,
  top-most window over every monitor would leave the user unable to see how
  to undo it.
* Overlays dim everything on the display, including the tray panel and
  Settings (they are click-through, so everything stays usable).
* The DDC framing, checksums and I²C pacing inside a command are done by
  Windows' Monitor Configuration API; only the ≥ 50 ms command spacing and
  the retries are the app's.
* Path key = the display's device interface path (monitor + connector), or
  `gdi:\\.\DISPLAYn` when Windows reports none; fingerprint = EDID
  manufacturer and product code.
* "Accessibility permission" rows do not exist (no permission is needed).

## Design decisions

* **WMI** goes through `WmiClient` (CsWin32 COM projections `IWbemLocator`,
  `IWbemServices`), shared with the system monitor, because
  `System.Management` is not referenced. Calls are only made from the
  brightness worker.
* **Restoring state**: overlays exist only in this process; stopping the
  feature (`Sync(false)`), uninstalling and quitting remove them, and a crash
  removes them with the process, so no write-ahead marker is needed. The
  hardware brightness (DDC and panel) is left exactly as the user set it.
* **Monitor handles** are reopened on every full rebuild, since HMONITORs
  change with the configuration, and destroyed (`DestroyPhysicalMonitors`)
  on stop.

## Risks

* DDC/CI behaviour varies by monitor, dock, KVM and adapter (as on macOS).
  Some monitors answer reads slowly (> 100 ms) or only after wake settles;
  some accept writes and ignore them (write-only path, "Dim the picture").
* Some drivers expose a physical monitor handle for the built-in panel; the
  built-in panel is never sent DDC commands.
* `WmiMonitorBrightnessMethods` sometimes needs the display adapter's own
  driver (not "Microsoft Basic Display Adapter"); without it the panel falls
  back to the overlay.
* Overlays can be hidden by exclusive full-screen games (they draw above
  top-most windows).
* `ExcludeFromCapture` (`WDA_EXCLUDEFROMCAPTURE`) needs Windows 10 2004+, which
  is the project minimum.
* Never run on real hardware here: the dxva2 and WMI paths are type-checked
  only.

## Windows manual test checklist

1. Install Displays in the Features hub; open Settings → Energy and display →
   Displays and turn on "Control displays". The Displays tab appears in the
   panel; turn it off and the tab disappears.
2. Laptop: the built-in row says "Adjusted by Windows" and shows the same
   level as Windows quick settings. Drag the slider: the panel follows
   smoothly; Windows quick settings show the new value.
3. External DisplayPort/HDMI monitor with DDC/CI enabled in its menu: the row
   says "Adjusted on the monitor (DDC/CI)" with the monitor's own level.
   Drag fast: the monitor follows without flicker or signal loss; its own OSD
   menu shows the new value.
4. Change the brightness with the monitor's buttons, wait 3 s, press the
   increase shortcut: the step starts from the monitor's new level.
5. Monitor behind an adapter or a TV that ignores DDC: after the first drag
   the row switches to "Dims the picture" and the screen darkens via the
   overlay. Screenshots (Win+Shift+S) and screen recordings are not darkened.
   Clicks go through the overlay.
6. On a monitor that accepts writes but never answers reads, "Dim the
   picture" is offered; turn it on (overlay), off (overlay gone, re-probed).
7. On a readable monitor, turn on "Extra dimming" in Settings: below 25 %
   the monitor stays at its minimum and the picture darkens further; going
   back above 25 % removes the overlay before the monitor brightens.
8. Turn on "Use display brightness shortcuts": Ctrl+Alt+Win+− / = change the
   primary display by 1/16 (try Half and Quarter steps). Turn on "Shortcuts
   follow the pointer": the display under the pointer changes instead.
9. Turn on "Show brightness when adjusting": the OSD appears centred on the
   changed display, fades out about a second after the last change, is not
   in screenshots and never takes focus.
10. Unplug a dimmed monitor and plug it back: it comes back dimmed (at least
    25 %). Sleep and wake the PC: rows rebuild about 3 s after wake.
11. Rearrange or change resolution: one rebuild, overlays still cover the
    right monitors at mixed DPI (100 % + 150 %).
12. Dim a display with the overlay, then turn off "Control displays", then
    quit the app, then kill it from Task Manager while dimmed: every time the
    overlay is gone and hardware levels stay where they were.

## Requests for shared code

1. ~~**Panel visibility refresh**~~: done at integration — the module calls
   `PanelRegistry.Invalidate()` when `brightnessControlEnabled` changes.
2. ~~**`ShortcutRoleRow`**~~: done at integration (the Settings page uses the shared row).
   Spec 05 §6.1 also asks for the display-brightness rows to run the "additional
   conflicts" check, which still belongs in that shared row.
