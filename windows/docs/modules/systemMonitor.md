# System monitor (Windows)

Spec: `docs/specs/03-system-monitor.md` (§3.1–3.17, §4, §6). Module class:
`Rivet.App.Features.SystemMonitor.SystemMonitorModule`. Feature ids: `monitorCPU`,
`monitorGPU`, `monitorMemory`, `monitorNetwork`, `monitorDisk`, `monitorPower`,
`connectedDevices` (and `killProcess` for Force Kill).

Everything runs as a standard user and the app installs no driver. CPU and GPU
temperatures come from [LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor)
(MPL-2.0, x64 only) with driver-free fallbacks; the processor's own sensors are
the one reading that needs more than a standard user, and an optional elevated
helper process reads them so the app does not have to run as administrator
(see "Temperatures" and "Sensor helper").
The app's own WMI calls go through CsWin32's COM projections (see "Design
decisions").

## Status

| Area | State |
|---|---|
| Central sampler (plan, cadence, hold rules, history, publishing) | Done |
| CPU total, per core, core classes | Done |
| Per-process CPU / GPU / memory / energy lists, Force Kill | Done |
| GPU usage (overall, per adapter, per process), memory, temperature | Done (temperature from NVIDIA/AMD/Intel Arc vendor interfaces, else from the WDDM driver) |
| Memory (used, app, compressed, cached, swap, pressure) | Done (pressure is a documented heuristic) |
| Disks (volumes, activity, eject, exclusions) | Done (SMART not implemented) |
| Network (speed, session totals, local IPv4, speed test) | Done (per-app traffic not available) |
| Power (charge, flow, time, health, cycles, battery temperature) | Done; the section hides on PCs without a battery |
| CPU temperature | Processor sensors with PawnIO + the sensor helper (or the app run as administrator), else ACPI thermal zones (standard user), otherwise an explained "unavailable" |
| Sensor helper (elevated process for the CPU sensors), install/remove in Settings | Done (x64; not run on hardware yet) |
| Connected USB devices, accessory batteries | Done (accessory batteries from the Bluetooth battery property only) |
| Panel sections System 10 / Network 20 / Disks 30 / Power 40, detail views, edit mode | Done |
| Alerts (six rules, 12 s sustain, cooldown, toasts) | Done |
| Readouts: tray tooltip line and the mini monitor window | Done (redesign of the menu bar readouts) |
| Settings page | Done |

## Implemented

### Sampler (`Rivet.Core/SystemMonitor`)

* `SamplingPlanner.Compute` builds the plan from what is visible (panel
  sections, detail views, readouts, alerts, mini monitor) exactly as §3.1.2,
  hub overrides last. Nothing is sampled when nothing needs it: the worker
  thread exits when the plan is empty.
* `SamplingCadence`: target interval (1, 2 or 5 s), per-kind strides
  (background disk under 15 s, GPU at 10 s in the background, battery charge
  slow), gcd wake ticks and tick round-up (§3.1.3).
* `MonitorEngine` reads each kind on its own timestamp (CPU, network and disk
  rates use the time of their own read), applies the hold and bridge rules
  (memory 12 s, temperatures `max(12, 2.2 × stride × interval)`), keeps
  120-sample history rings, and suppresses the GPU anti-spike for 0.9 s when a
  surface opens.
* `SystemMonitorService` runs the engine on one `BelowNormal` background thread
  and publishes snapshots to the UI thread with coalescing (never blocks the
  UI thread). Leases (`AcquireSurface`, `AcquireDetail`) start and stop it.
* `MetricFormat` ports every §6.10 formatter (bytes, rates in B/s and bit/s,
  compact widths, watts, temperatures in °C/°F, battery time, uptime, Mbps)
  with `MidpointRounding.AwayFromZero` like the Swift code.

### Windows sensors (`Rivet.Platform.Windows/SystemMonitor`)

| Metric | Source |
|---|---|
| CPU total | `GetSystemTimes` (all processor groups) |
| CPU per logical processor | `NtQuerySystemInformation(SystemProcessorPerformanceInformation)` per processor group |
| Core classes | `GetLogicalProcessorInformationEx(RelationProcessorCore)` `EfficiencyClass`: highest = Performance, lowest = Efficiency, all equal = one "CPU" group |
| Processes | One `NtQuerySystemInformation(SystemProcessInformation)` snapshot (no handle per process); names from the executable's file description, cached by path |
| GPU | PDH `\GPU Engine(*)\Utilization Percentage` (Task Manager semantics: per adapter, sum per engine, busiest engine), `\GPU Adapter Memory(*)`; adapter names from DXGI; temperature from LibreHardwareMonitorLib (NVAPI, ADL, Intel IGCL), else `D3DKMTQueryAdapterInfo(KMTQAITYPE_ADAPTERPERFDATA)` in tenths of °C |
| Memory | `GlobalMemoryStatusEx`, `GetPerformanceInfo`, page file pages from the kernel, `CreateMemoryResourceNotification(LowMemoryResourceNotification)`, "App Memory" = Σ private working sets and "Compressed" = the Memory Compression process working set (every 4 s at most) |
| Network | `GetIfTable2` 64-bit octets with the counting rule below; addresses from `GetAdaptersAddresses(AF_INET)` |
| Disks | `GetLogicalDrives`, `GetDriveType`, `GetDiskFreeSpaceEx`, `GetVolumeInformation` (with `SEM_FAILCRITICALERRORS` so empty card readers never prompt), bus type from `IOCTL_STORAGE_QUERY_PROPERTY`, disk number from `IOCTL_STORAGE_GET_DEVICE_NUMBER`, activity from `IOCTL_DISK_PERFORMANCE` per physical disk |
| Eject | `CM_Request_Device_Eject` on the disk's parent device node, three tries; "in use" is reported, not forced |
| Battery | `GetSystemPowerStatus` plus the battery class driver (`IOCTL_BATTERY_QUERY_INFORMATION/STATUS`): capacity, rate, cycle count, temperature (rarely implemented by firmware) |
| CPU temperature | LibreHardwareMonitorLib (Intel DTS / AMD Tctl through PawnIO) in the sensor helper or in an elevated app, else PDH `\Thermal Zone Information(*)\High Precision Temperature`, else WMI `root\WMI` `MSAcpi_ThermalZoneTemperature` |
| USB devices | SetupAPI over `GUID_DEVINTERFACE_USB_DEVICE`, minus root hubs, hubs (class 9), billboard (class 17) and non-removable devices |
| Accessory batteries | WinRT `PnpObject` with the Bluetooth battery level property (`{104EA319-…} 2`) |
| Full-screen detection (mini monitor) | `SHQueryUserNotificationState` |

### UI (`Rivet.App/Features/SystemMonitor`)

* Panel sections `system` (Order 10), `network` (20), `disk` (30) and
  `power` (40, visible only with a battery or accessory batteries), each with
  blocks that can be reordered and hidden in edit mode, sparklines over the
  120-sample history, a core matrix, process lists with Force Kill, and
  detail views per metric.
* Speed test against `speed.cloudflare.com` (latency, download, upload; a
  non-2xx answer fails the phase and is never counted).
* Alerts: CPU usage, CPU temperature, battery temperature, memory pressure,
  low disk space (only volumes of at least 10 GB) and low battery; each
  must hold for 12 s (`SustainedGate`), one shared cooldown per kind
  (2–60 min); toasts through `INotificationService`.
* Readouts: the menu bar text has no Windows equivalent, so pinned readouts go
  to one line of the tray icon's tooltip (`ITrayPresence`, source
  `systemMonitor.readouts`, at most 100 characters) and to an optional
  always-on-top **mini monitor** window: draggable, remembers its position
  per machine, optional click-through, hides in full-screen apps.
* Actions `systemMonitor.toggleMiniMonitor` and `systemMonitor.openTaskManager`.
* Settings page `monitor`: readings, panel items, graphs, readouts (tokens,
  order, preview, bar colours), alerts and excluded drives.

## Not implemented (and why)

| Item | Reason |
|---|---|
| Per-app network traffic (Network → Apps) | Needs ETW kernel network events, which require administrator rights. The block is not offered. |
| SMART disk health | Needs `IOCTL_SMART_*`/`MSStorageDriver_FailurePredict*`, which require administrator rights. Not offered. |
| Power adapter (watts, voltage) and whole-system watts on AC | No Windows API. The "System" row shows the battery rate on battery and is hidden on AC. |
| The processor's own temperature without PawnIO | Core sensors are model-specific registers: reading them needs a kernel driver. The app never installs one; the user installs PawnIO (pawnio.eu) and the sensor helper, and until then the app falls back to ACPI thermal zones or explains what is missing. |
| The sensor helper for several Windows accounts at once | The helper's task and pipe admit the one account that installed it. Another account installs it again (which moves the grant). |
| CPU and GPU sensors on ARM64 | LibreHardwareMonitorLib executes x64 CPUID code, so it is not loaded on ARM64. Thermal zones and the WDDM driver temperature still work there. |
| Fan RPM and fan control | Deferred by the spec (needs EC/SuperIO access). |
| Real app icons in process lists | Rows use a generic icon. Needs an icon extraction service (see requests). |
| Accessory batteries from vendor protocols (Logitech, Razer, …) | Only what Windows exposes through the Bluetooth battery property. |

## Deviations from macOS

* **Readouts** are a tooltip line plus the mini monitor instead of menu bar
  text. Token, order, style and colour settings keep the macOS keys.
* **Section visibility** uses the shell's `PanelHiddenItems` instead of the
  macOS `monitorShow*` keys.
* **"Open Activity Monitor"** became "Open Task Manager".
* **Process grouping**: Windows has no "responsible process", so helpers are
  grouped by executable name under the topmost process of that name.
* **Force Kill safety** (`ProcessSafety`), the Windows counterpart of the
  macOS list (kernel_task, launchd, WindowServer, loginwindow): PIDs 0–4,
  this app, `System`, `Registry`, `Memory Compression`, `Secure System`,
  `smss`, `csrss`, `wininit`, `winlogon`, `services`, `lsass`, `LsaIso` and
  `dwm` are never offered. Rows without a verified start time cannot be
  killed, and the kill re-checks the process creation time so a reused PID is
  never killed. There is no elevation prompt (macOS asks for an administrator
  once): killing a process of another user or an elevated app fails with an
  error HUD.

## Design decisions

* **CPU %**: `GetSystemTimes`/kernel times give "% Processor Time". Task
  Manager shows the frequency-scaled "% Processor Utility", which reads higher
  at turbo clocks. We keep the time-based figure (it matches macOS semantics
  and the per-core matrix).
* **Memory pressure heuristic** (`MemoryPressureHeuristic`):
  critical when Windows signals low memory, or available < 5 %, or commit ≥ 95 %
  of the limit; warning when available < 15 % or commit ≥ 85 %; otherwise
  normal. The "Critical memory pressure" alert uses the critical level.
* **Network counting rule**: an interface counts when it is up, a hardware
  interface, not a filter (QoS/WFP lightweight filters would double count),
  not a tunnel, of type Ethernet, Wi-Fi or mobile broadband, and its
  description does not mark it virtual (Hyper-V, vEthernet, VMware,
  VirtualBox, TAP/TUN, WireGuard/Wintun, loopback, Npcap, WAN Miniport,
  OpenVPN, ZeroTier, Tailscale, NordLynx, Docker). Local IPv4 addresses use
  the same rule. Counter resets give 0 for that sample and keep the totals.
* **Sensor helper** (`src/Rivet.SensorHelper`, `SensorHelperClient`,
  `SensorHelperProtocol`). A separate small executable
  (`Rivet.SensorHelper.exe`) that references only LibreHardwareMonitorLib.
  It ships next to the app and shares its .NET runtime files.
  * **Install** (Settings → Monitor → Processor sensors, one UAC prompt):
    the app starts the bundled helper elevated with `--install --user <SID>`.
    It copies itself and exactly the files its `.deps.json` lists (about
    85 MB with the runtime) to `%ProgramFiles%\Rivet\SensorHelper` and
    registers the on-demand scheduled task `\Rivet\SensorHelper`, which runs
    that copy as SYSTEM. **Remove** runs `--uninstall` (task, folder); the
    Inno uninstaller does the same when the helper is installed.
  * **Why Program Files**: the task runs with system rights, so what it runs
    must not be replaceable by a standard-user process. The app's own folder
    under `%LOCALAPPDATA%` is; Program Files is not.
  * **Start and stop**: the task's access list lets the installing account
    read and start it (`GRGX`), nothing else. The app starts it with
    `schtasks /Run` when a CPU temperature is wanted; the helper exits after
    90 s without a request, so nothing runs while no temperature is shown.
  * **Pipe** `Rivet.SensorHelper`: one client at a time; its access list
    admits SYSTEM, administrators and the installing account. The only
    request is the word `cpu`; the answer is `ok <version>`, one line per
    sensor and `end`. Line lengths are bounded on both sides. The helper
    takes no other input, writes nothing outside its folder (a log,
    `helper.log`, capped at 256 KB) and enables only the library's CPU group.
    The client connects with identification-level impersonation and accepts
    the pipe only when SYSTEM or the administrators group owns it, so a
    program of the same user cannot take the name and feed numbers.
  * **Versions**: the helper is not replaced by app updates. A different
    protocol version makes the app treat it as not installed and Settings
    offers Install again.
  * **ARM64**: not shipped and not offered (the sensor library is x64 only).
* **WMI** goes through `WmiClient`, a small client on CsWin32's COM types
  (`IWbemLocator`, `IWbemServices`, `IEnumWbemClassObject`), because the
  shared projects do not reference `System.Management`. It sets the proxy
  blanket on every proxy, serializes calls and is only used from background
  threads. The Displays module reuses it for panel brightness.
* **Temperatures** (`HardwareSensors`, `WindowsTemperatureSensor`,
  `TemperatureSelection`). Sources are tried in order and one that never
  answered in three reads is dropped for the session:
  * GPU: LibreHardwareMonitorLib reads NVAPI, ADL and Intel IGCL, which
    answer a standard user. The figure is the adapter's "GPU Core" sensor
    (otherwise its hottest sensor), the hottest adapter wins. Fallback: the
    temperature the WDDM driver gives Windows (what Task Manager shows).
  * CPU: LibreHardwareMonitorLib reads Intel's digital thermal sensors and
    AMD's Tctl/Tdie through the [PawnIO](https://pawnio.eu) driver. The
    figure is "CPU Package" on Intel, "Core (Tctl/Tdie)" on AMD, otherwise
    the hottest core. PawnIO is a separate, signed driver the user installs;
    its device opens only for an elevated process. The app reads it in its
    own process when it runs as administrator, and otherwise asks the sensor
    helper (below). Without PawnIO the library's CPU sensors are not polled.
    Fallback: the hottest ACPI thermal zone (a zone named after the CPU
    wins) from the "Thermal Zone Information" performance counters, which a
    standard user may read, then the same zones from WMI.
  * The panel explains a missing CPU temperature: install PawnIO and the
    helper (`NeedsSensorDriver`), install the helper (`NeedsSensorHelper`),
    run as administrator (`NeedsAdministrator`, ARM64 thermal zones only), or
    not reported at all.
  * While the helper answered within the last minute, a missed read returns
    nothing instead of a thermal zone, so the sampler's bridge holds the
    last value and another sensor's figure never flashes in between.
  * The library opens lazily on the sampler thread, with only its CPU and
    GPU groups enabled (the Intel GPU group depends on the CPU group). It
    brings `System.Management`, `HidSharp`, `DiskInfoToolkit`,
    `RAMSPDToolkit-NDD`, `System.IO.Ports` and `Mono.Posix.NETStandard` as
    transitive dependencies; none of them is used by the groups we enable.

## Risks

* The temperature sources were written and built on macOS and have not run
  on hardware here; verify on NVIDIA, AMD and Intel GPUs, and on Intel and
  AMD CPUs with and without PawnIO. `D3DKMT_ADAPTER_PERFDATA` layout and
  units come from the WDK headers.
* ACPI thermal zones are whatever the firmware reports. Laptops usually
  expose a zone close to the CPU; many desktops expose none or a fixed
  placeholder (27.8 °C is common), which then shows as the CPU temperature
  until PawnIO is set up.
* The sensor helper has not run on Windows here. The parts to verify first:
  that a standard account may start a SYSTEM task through the task's
  `GRGX` access entry, that the pipe's owner reads as SYSTEM or
  administrators from the client, and that PawnIO's device opens for SYSTEM.
  `helper.log` in the install folder and the app's log (category `monitor`)
  say which step failed.
* The helper copies the .NET runtime to Program Files (about 85 MB) because
  it cannot depend on a machine-wide .NET install.
* The helper's package files reach the app's output through
  `Rivet.Platform.Windows`, which references the same package; a dependency
  only the helper has would need its own copy step.
* PDH GPU instance lists can hold hundreds of entries on busy PCs; the GPU is
  sampled at most every stride and only while something shows it.
* `NtQuerySystemInformation` structure offsets follow the documented x64
  layout (checked against phnt); ARM64 is untested.
* Battery temperature is almost never reported by firmware; the row then
  shows "unavailable".
* Toasts need the app identity the shell registers (AUMID shortcut or
  package); without it Windows drops them silently.

## Windows manual test checklist

1. Install CPU, GPU, Memory, Network, Disk, Power and Connected devices in the
   Features hub. Open the panel: System, Network, Disks (and Power on a
   laptop) appear in that order.
2. System: CPU % follows Task Manager's "Processes" CPU column within a few
   points at idle and under load (a `while` loop in PowerShell); the core
   matrix shows one square per logical processor; on a hybrid CPU the P and E
   groups are labelled.
3. Open the CPU list: the busy PowerShell is on top. Install "Force Kill",
   kill it from the list, confirm the dialog: it disappears. Check that
   Desktop Window Manager (`dwm.exe`), `csrss.exe` and Rivet itself offer no
   Force Kill, and that killing an elevated app shows the error HUD.
4. GPU: play a video or a game; usage and per-process GPU match Task
   Manager's GPU column; on an NVIDIA, AMD or Intel Arc GPU the temperature
   appears as a standard user and matches the vendor's own tool within a
   degree or two.
5. Memory: the pressure pill is green at idle. Open many browser tabs or run
   a memory hog: it turns yellow/red at the documented thresholds.
6. Network: download a large file; the speed matches Task Manager's
   Ethernet/Wi-Fi graph. Turn on a VPN or Hyper-V switch and check the
   traffic is not counted twice. Run the speed test (latency, download,
   upload) and cancel one mid-way.
7. Disks: plug a USB stick; it appears with its file system; eject it from
   the panel; with a file open on it the eject reports "in use". Add it to
   the excluded drives and check it disappears. Insert an empty SD reader:
   no "No disk" dialog appears.
8. Power (laptop): charge, charging state, time remaining, health and cycles
   match `powercfg /batteryreport`; unplug and replug the charger: the state
   changes within a few seconds. On a desktop the Power section is absent.
9. Temperatures: as a standard user a laptop shows a CPU temperature from
   its thermal zones (compare with `Get-Counter '\Thermal Zone Information(*)\High Precision Temperature'`,
   tenths of a kelvin); a PC without zones shows the note asking for PawnIO.
   Install PawnIO from pawnio.eu: the note now asks for the sensor helper.
   Settings → Monitor → Processor sensors → Install, accept the UAC prompt:
   the CPU tile shows the package temperature within a few seconds and
   matches LibreHardwareMonitor or HWiNFO, with the app still running as a
   standard user. Check `%ProgramFiles%\Rivet\SensorHelper` exists, Task
   Scheduler shows `\Rivet\SensorHelper` (hidden tasks on) and
   `Rivet.SensorHelper.exe` runs as SYSTEM in Task Manager. Close the panel
   and unpin temperatures: the helper exits within about two minutes. Open
   the panel again: the temperature returns. Decline the UAC prompt on a
   fresh install: nothing changes and no error shows. Remove the helper:
   the folder and the task are gone and the note asks for the helper again.
   Repeat install as a standard (non-administrator) account using an
   administrator's password. Uninstall the app with the helper installed:
   Windows asks once and removes it. Alternatively run the app as
   administrator without the helper: the tile works too. On Windows on ARM
   the app starts, shows thermal zone temperatures and offers no helper.
10. Connected devices: plug a USB mouse; it is listed with its name; the root
    hub and built-in camera are not. Pair Bluetooth earbuds that report a
    battery in Windows Settings; the accessory battery appears in Power.
11. Alerts: set the CPU threshold to 50 %, run a load for 15 s: one toast; no
    second toast before the cooldown ends.
12. Readouts: pin CPU and Memory; hover the tray icon: one tooltip line shows
    them. Turn on the mini monitor, drag it, restart the app: it returns to
    the same place. Turn on click-through: clicks reach the window below.
    Start a full-screen game or presentation: it hides.
13. Close the panel and the mini monitor and unpin readouts: CPU use of
    Rivet drops to ~0 % (the sampler thread exits).

## Requests for shared code

1. **Tray menu submenus**: `TrayMenuItem` has no children, so Keep Awake's
   "Activate for…" presets are flat items (Orders −39…−33). A `Children`
   list would restore the macOS submenu.
2. **Tray icon re-render**: `TrayController` should re-render the icon only
   when the tint or badge changes; the readout tooltip line changes every
   sample and must not cause an icon redraw.
3. **App icons**: a shared service that returns an icon for an executable path
   (`SHGetFileInfo`/`IShellItemImageFactory`, cached) would let the process
   lists show real app icons.
4. **Panel visibility refresh**: `PanelViewModel` re-evaluates
   `PanelSectionDescriptor.IsVisible` only when the registry or feature
   runtime changes. An explicit `PanelRegistry.Invalidate()` (or observing
   visibility keys) would replace the re-`AddSection` workaround used by the
   Displays section.
