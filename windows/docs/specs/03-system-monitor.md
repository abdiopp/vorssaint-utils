# 03 — System Monitor, Energy & Displays — Functional Specification (Windows port)

Source of truth: Vorssaint macOS app (Swift/SwiftUI/AppKit), repository `vorssaint-utils`, commit `b6d8d2db` (main). Everything below was derived from the Swift sources and their unit tests; a Windows developer should be able to implement the area from this document alone.

Terminology used throughout:

| Term | Meaning on macOS | Windows equivalent (proposed) |
|---|---|---|
| **Panel** | The popover opened by clicking the menu bar icon; it has sections (Keep awake, Displays, System, Network, Disks, Power, Fan Control, …) | Tray flyout window |
| **Menu bar readout** | Live metric "blocks" drawn next to the app icon in the macOS menu bar | Tray icon(s), tray tooltip, optional taskbar overlay window (see §3.16, §7) |
| **Detail panel** | A per-metric popover opened by clicking a *separate* menu bar metric item | Flyout opened from a per-metric tray icon |
| **Hub** | Feature Hub: every feature family can be installed/uninstalled (`featureAvailable.<id>` keys). An uninstalled family never samples, renders or alerts | Same |
| **Island** | The Dynamic Island / notch surface (separate spec). It is a *consumer* of the monitor and keep-awake; mentioned only where it affects behavior | Out of scope |
| **Foreground** | Any monitor surface is visible (panel section, detail panel, Settings island preview, island) | Flyout or detail visible |
| **Background** | Only menu bar readouts and/or alerts need data | Only tray readouts/alerts need data |

Priority tags used in this document: **P0** = MVP, **P1** = next, **P2** = nice to have, **D** = defer, **X** = drop (Mac-only).

---

## 1. Overview

This area turns the app into a lightweight system monitor plus a set of energy/display utilities:

1. **System monitor** — CPU (total + per logical core), GPU, memory and memory pressure, temperatures (CPU/GPU/battery), fan RPM (only with Fan Control), network throughput + session totals + local IPv4 + on-demand speed test, disks (capacity, I/O, SMART, eject protection), power (system draw, adapter, battery flow, charge, time remaining, health, cycles), peripheral (Bluetooth/HID) battery levels, connected external USB devices, uptime, and per-app breakdowns (CPU, GPU, memory, "energy", network).
2. **History graphs** — 120-sample ring buffers per series rendered as sparklines with an optional scale label.
3. **Alerts** — optional notifications: sustained high CPU, sustained hot CPU, sustained hot battery, critical memory pressure, low disk space, low battery; one shared repeat cooldown.
4. **Menu bar readouts** — up to 15 pinnable metrics rendered as compact two-line blocks or vertical usage bars next to the icon (one combined item or one item per metric).
5. **Keep awake** — timed/indefinite/"until time" sessions, automation (external display / on power / selected apps running), pause when locked, pointer jiggle, battery protection, closed-lid mode (disables system sleep), optional dimming of the built-in display while the lid is closed.
6. **Display brightness** — one slider per display via the system pipeline (built-in/Apple displays), DDC/CI (external monitors), or software gamma dimming (anything else); brightness keys that follow the pointer, finer key steps, global shortcuts, an on-screen display (OSD), display on/off, keyboard backlight slider. "Extra brightness" (XDR HDR headroom) is Mac-only.
7. **Bluetooth on sleep** — turn Bluetooth off on sleep and back on at wake (only if the app turned it off).
8. **Fan control** (beta, summarized) — manual RPM / temperature curves through a privileged helper.

### 1.1 Architecture (logical components)

```
                      ┌────────────────────────── consumers ─────────────────────────┐
 panel sections ──┐   │ menu bar readouts   detail panels   alerts   island (Mac)    │
 detail panels ───┤   └──────────────▲──────────────▲────────────▲──────────────────┘
 menu bar pins ───┼─► SamplingPlan ──┴──► SystemMonitor (timer + worker) ──► Snapshot (immutable, published on UI thread)
 enabled alerts ──┤                        │ per-metric samplers (CPU, cores, GPU, memory, network,
 hub availability ┘                        │ disk, power, SMC temps/fans, peripheral battery, USB)
                                           ▼
            On-demand services: ProcessUsageService (per-app lists), NetworkAddressService (local IP),
            SpeedTest (Cloudflare), DiskProtectionService (eject), MonitorAlertService (notifications)

 Independent services: KeepAwakeManager, BrightnessService (+ BrightnessOSD), BluetoothSleepService,
                       FanControlService (+ privileged helper), ExtraBrightnessService (Mac-only)
```

### 1.2 Design principles to preserve

1. **Zero idle cost.** The sampler timer exists only while a consumer needs data, and only the metric families that a consumer needs are read. Heavy work (per-app lists, SMART/metadata lookups, Bluetooth battery scans) runs only while visible or at long cache intervals.
2. **Never fabricate a spike.** The first sample of every rate is a baseline (shown as "Measuring…"), gaps longer than a limit reset the baseline, and counters that go backwards yield 0 for that tick.
3. **Hold through hiccups, never age into alerts.** A failed read re-serves the last value for a few ticks, but alert logic keys on the *time of the last real read*, so repeated stale values cannot satisfy a "sustained for 12 s" rule.
4. **Show only what the hardware reports.** Every reading is optional; rows/blocks without data are hidden.
5. **Journal before you change system state.** Disabling sleep, dimming a display, switching a display off, switching Bluetooth off and fan control each persist a recovery marker *before* acting and are undone at the next launch if the app died.

---

## 2. Feature inventory

| # | Capability | Priority | Notes |
|---|---|---|---|
| 1 | Demand-driven sampler with foreground/background cadence | P0 | Core of everything |
| 2 | CPU total usage + history graph | P0 | |
| 3 | CPU per-core bars grouped by core class | P1 | Hybrid Intel/AMD/ARM on Windows |
| 4 | GPU usage + history graph (+ anti-spike smoothing) | P0 | PDH "GPU Engine" |
| 5 | Memory used / app memory, total, compressed, cached files, swap used + pressure traffic light + history | P0 (pressure: heuristic) | No Windows pressure level |
| 6 | CPU / GPU / battery temperatures | P2 / D | Driver/vendor dependent on Windows |
| 7 | Fan RPM telemetry | D | Only with Fan Control |
| 8 | Network download/upload rate (bytes or bits) + graph | P0 | |
| 9 | Network session totals | P0 | |
| 10 | Local IPv4 addresses with interface names | P0 | No public IP in the source app |
| 11 | Internet speed test (latency, download, upload) | P1 | Cloudflare endpoints |
| 12 | Disks: per-volume capacity, used %, available, purgeable | P0 (purgeable: X) | |
| 13 | Disks: read/write rates, session totals, graph | P0 | |
| 14 | Disks: SMART/NVMe health details | P2 | May need elevation |
| 15 | Disks: eject one / eject all external, exclusion list, open in file manager, storage settings | P1 | |
| 16 | Power: system draw, adapter draw/rating, battery flow, charge, time remaining, health, cycles + graphs | P0 (charge/time/health/cycles/flow), D (system draw on AC, adapter) | |
| 17 | Peripheral (Bluetooth/HID) battery levels | P2 | |
| 18 | Connected external USB devices count + list | P1 | |
| 19 | Uptime | P0 | |
| 20 | Per-app breakdowns: CPU, GPU, memory, energy, network | P0 (CPU/memory), P1 (GPU/energy), D (network) | |
| 21 | Activate app from a row; Force Kill (with Kill Process feature) | P1 | |
| 22 | Alerts (6 rules + cooldown) | P0 (CPU, memory, disk, battery), P2 (temperatures) | |
| 23 | Menu bar readouts (15 tokens, values/bars, combine temps, spacing, separate items, hide icon) | P0 (redesigned as tray) | Biggest UX redesign |
| 24 | Metric detail panels | P1 | |
| 25 | Settings → Monitor page | P0 | |
| 26 | Keep awake sessions (presets, indefinite, until time, extend, last pick, auto start, hotkey) | P0 | |
| 27 | Keep awake: allow display sleep, battery protection, notifications | P0 | |
| 28 | Keep awake automation (external display / power / apps running; Any/All) | P1 | |
| 29 | Keep awake: pause while locked | P1 | |
| 30 | Keep awake: move pointer slightly (jiggle) | P1 | |
| 31 | Keep awake: closed-lid mode | P1 (re-implemented via lid-close power setting) | |
| 32 | Keep awake: dim built-in display to zero while lid closed | X | Panel is off when the lid is closed on Windows laptops |
| 33 | Active icon / tint / countdown next to icon | P1 | |
| 34 | Display brightness sliders (internal panel, DDC/CI, software dimming) | P0 (internal + DDC), P1 (software) | |
| 35 | Extended dimming (below hardware minimum), forced software dimming per monitor | P2 | |
| 36 | Brightness keys follow pointer, finer key steps | X / D | Keys are firmware/OS-owned on Windows |
| 37 | Display brightness global shortcuts | P1 | |
| 38 | Brightness OSD overlay | P1 | |
| 39 | Display on/off per display | D | No clean Windows API |
| 40 | Keyboard backlight slider + shortcuts | X | Vendor-specific on Windows |
| 41 | Extra brightness (XDR HDR headroom boost) | X | Mac-only |
| 42 | Bluetooth off on sleep / restore on wake | P1 | |
| 43 | Fan control (manual/curves, privileged helper) | D | |

---

## 3. Detailed behavior per capability

### 3.1 Sampling engine (SystemMonitor)

#### 3.1.1 Consumers and activation

The sampler is a singleton. Consumers register/unregister demand; each change triggers an immediate re-evaluation and (if anything is needed) an immediate refresh.

| Consumer | How it signals | Foreground? |
|---|---|---|
| Panel section on screen (System / Network / Disks / Power) | `setMenuPanelNeeds(needs)`; `.none` when the panel closes or shows a non-monitor section (Keep awake, Utilities, Controls…) | Yes |
| Detail panel for a metric | `setMenuPanelNeeds(kind.monitorNeeds)` (see §3.17.6) | Yes |
| "Full monitor surface" (Settings → island preview) | `panelDidAppear()` / `panelDidDisappear()` — a **depth counter**, not a bool | Yes; counts as all sections visible |
| Island visible / island detail page | Mac-only | Yes |
| Island accessory battery monitoring | Mac-only; needs peripheral battery only | No |
| Any menu bar metric pinned | `setMenuBarActive(anyEnabled)` re-evaluated on every settings change | No |
| Any alert enabled (for an available metric) | `setAlertsActive(true)` | No |

`shouldRun` = any of the above active. `shouldSample` = shouldRun ∧ plan.any. When `shouldSample` becomes false the timer is invalidated.

#### 3.1.2 Sampling plan (which families to read)

`hasBattery` = an internal battery is installed (resolved once per boot). "System visible" etc. means the corresponding panel section is on screen, or the full surface is visible. "Detail(x)" means the detail panel of metric kind x is open.

| Need flag | True when (OR of) |
|---|---|
| `needCPU` | (System visible ∧ `monitorSysCPU`) · Detail(cpu) · island · `menuBarCPU` · `monitorAlertCPU` |
| `needCPUCores` | the panel's System section on screen (not the full surface) ∧ `monitorSysCPU` ∧ `monitorSysCPUCores` — **never** for menu bar, alerts or detail panels; read whenever the section is visible, even before the CPU row is expanded |
| `needMemory` | (System visible ∧ `monitorSysMemory`) · Detail(memory) · island · `menuBarMemory` · `monitorAlertMemory` |
| `needNetwork` | Network visible · Detail(network) · island · `menuBarNetwork` |
| `needDisk` | Disks visible · Detail(disk) · `menuBarDiskUsage` · `menuBarDiskActivity` · `monitorAlertDisk` |
| `needPower` | Power visible · island · `menuBarPower` · [hasBattery ∧ ((Power visible ∧ `monitorSysBattery`) · Detail(battery) · `menuBarBattery` · `menuBarBatteryTime` · `monitorAlertBattery`)] |
| `needPowerDraw` | `menuBarPower` (watts pinned → power read at the base interval even in background) |
| `needPeripheralBattery` | Detail(battery) · island accessories · `menuBarPeripheralBattery` |
| `needGPUUsage` | (System visible ∧ `monitorSysGPU`) · Detail(gpu) · island · `menuBarGPU` |
| `needCPUTemperature` | (System visible ∧ `monitorSysTemps`) · Detail(cpu) · `menuBarCPUTemperature` · `monitorAlertCPUTemperature` |
| `needGPUTemperature` | (System visible ∧ `monitorSysTemps`) · Detail(gpu) · `menuBarGPUTemperature` |
| `needBatteryTemperature` | hasBattery ∧ ((Power visible ∧ `monitorPwrTemperature`) · Detail(battery) · `menuBarBatteryTemperature` · `monitorAlertBatteryTemperature`) |
| `needFanSpeed` | Fan Control installed ∧ fan telemetry available ∧ (full surface · Detail(fan) · `menuBarFanSpeed`) |
| `needConnectedDevices` | full surface · (System visible ∧ `monitorSysConnectedDevices`) · Detail(connectedDevices) · `menuBarConnectedDevices` |

Hub overrides (applied last; an uninstalled family never samples even if pinned/alerting):
- `monitorCPU` off → no CPU, cores, CPU temperature.
- `monitorGPU` off → no GPU usage/temperature.
- `monitorMemory`, `monitorNetwork`, `monitorDisk` off → that family off.
- `monitorPower` off → no power, power draw, peripheral battery, battery temperature.
- `fanControl` off → no fans. `connectedDevices` off → no USB.

Any plan change (settings write, hub toggle, pin/unpin) re-syncs the cadence and refreshes **immediately**, so a newly pinned slow metric does not wait for the next slow wake (up to 60 s).

#### 3.1.3 Cadence (strides) — foreground vs background

Base interval **I** ∈ {1, 2, 5} s (setting "Update every", default 2). Each kind has a target period **T** (seconds), different in foreground and background. `stride = max(1, ceil(T / I))`; a kind is read on ticks where `tick % stride == 0`. The timer period is `I × gcd(strides of all needed kinds)`; the tick counter advances by that gcd per wake and is rounded up onto the new grid whenever the gcd changes (otherwise `tick % stride` could become unreachable). Timer tolerance = 15% of its period.

| Kind | T foreground | T background | Effective background period at I = 1 / 2 / 5 s |
|---|---|---|---|
| cpu, memory, network | 1 | 1 | 1 / 2 / 5 |
| powerDraw (watts pinned) | 1 | 1 | 1 / 2 / 5 |
| gpuUsage | 1 | 10 | 10 / 10 / 10 |
| fanSpeed | 1 | 5 | 5 / 6 / 5 |
| connectedDevices | 2 | 10 | 10 / 10 / 10 |
| power (charge, time, health) | 1 | 15 | 15 / 16 / 15 |
| temperature (all three) | 1 | 15 | 15 / 16 / 15 |
| disk | 1 | 10 | 10 / 10 / 10 (must stay < disk max gap 15 s) |
| peripheralBattery | 15 | 60 | 60 / 60 / 60 |

In foreground every T=1 kind is read every tick (every I seconds); connected devices every 2/2/5 s; peripheral battery every 15/16/15 s. Examples from tests: only temperature pinned → timer wakes once per 15 s; only peripheral battery → once per 60 s; CPU + temperature → every tick.

#### 3.1.4 Refresh mechanics

- One refresh in flight at a time (worker queue, low priority). A refresh requested while one runs sets a "pending" flag; one more refresh runs afterwards.
- **GPU anti-spike:** opening the panel/detail/island makes the immediate refresh *skip GPU*, followed by a deferred refresh 0.9 s later that reads it. Any transient UI animation can call "suppress GPU reads" for 0.9 s (minimum 0.1 s); during that window the previous GPU value is re-served. Reason: the popover animation itself raises GPU utilization.
- **Power source change** (unplug/plug, new time-to-empty estimate): if power is needed and the machine has a battery, refresh immediately.
- **Publish rule:** the snapshot is republished only if at least one kind was actually read this tick, or the plan or the foreground state changed (pure carry-over ticks do not redraw the menu bar).
- **Hold rules** (served when a read fails):
  - CPU usage, GPU usage, fan speeds: last value kept for up to **3** consecutive failed reads, then cleared.
  - Memory: last full reading kept for up to **4** misses and at most **12 s** old. A pressure reading of "unknown" never overwrites a known level.
  - Temperatures: see §3.5 (bridge window).
  - Disk, power, peripheral, USB: last reading re-served on ticks where the stride skipped them.
- **History rings** (§3.14) are always maintained while the family is sampled, but published (copied into the snapshot) **only in foreground**; in background the snapshot carries empty arrays.

#### 3.1.5 Snapshot contents

| Field | Type / unit |
|---|---|
| `cpuUsage`, `cpuUsageReadAt` | fraction 0…1; uptime seconds of last real read |
| `cpuCoreUsage` | array of optional fractions, one per logical core (nil = no fresh interval yet) |
| `gpuUsage` | fraction 0…1 |
| `memoryUsed`, `memoryAppUsed`, `memoryTotal`, `memoryCompressed`, `memoryCached`, `memorySwapUsed` | bytes |
| `memoryPressure` | normal / warning / critical / unknown |
| `cpuTemperature` (+ `ReadAt`), `gpuTemperature`, `batteryTemperature` (+ `ReadAt`) | °C (always stored in Celsius) |
| `fanSpeeds` | RPM per fan |
| `netDownBytesPerSec`, `netUpBytesPerSec` | bytes/s (nil until a previous sample exists) |
| `netTotalDown`, `netTotalUp` | bytes since the app started watching |
| `power` | PowerReading (§3.9) |
| `peripheralBatteries` (+ observation times) | list (§3.10) |
| `disk` | list of volumes (§3.8) |
| `connectedDevices` | list (§3.11) |
| Histories | cpu, gpu, memory (used), memoryApp, netDown, netUp, diskRead, diskWrite, systemPower (W), battery (fraction) |

### 3.2 CPU

**Total usage.** Read the cumulative CPU tick counters (user, system, idle, nice) of the whole machine.

```
busy  = user + system + nice
total = busy + idle
usage = (busy − busy_prev) / (total − total_prev)       // fraction 0…1
```

- First read after launch (or after the CPU need was off) only stores a baseline → nil.
- If `total` did not increase → nil.
- If the previous read is older than **12.5 s** (sleep, sampling paused): store a new baseline, return nil, and **also drop the held value and its read time** so the UI shows "-" and alerts wait for a fresh reading.

**Per-core usage.** Same formula per logical core using per-processor tick counters (32-bit counters; a small backwards step = reset → nil, a large backwards step = wrap-around → use modular difference). Busy = total − idle delta. Per-core nil when no ticks elapsed. Gap > 12.5 s or a core-count change → baseline (all nil). The sampler is reset whenever per-core reading is not in the plan.

**Core topology** (computed once): group logical cores by performance class. macOS reads `hw.perflevel*` names (e.g. "Super", "Performance", "Efficiency") and maps cores via the device tree; any inconsistency (count mismatch, duplicates, unknown class) falls back to a single group "CPU" containing all cores. Group weights (bar width multiplier): Super 1.5, Performance 1.25, others 1.0.

**Per-core matrix UI** (shown under the CPU row while the CPU row is expanded and "Per core" is on):
- A rounded box (padding 8) with rows of groups. Bars: base width 14 pt × group weight, 4 pt apart, height 60 pt, filled from the bottom proportionally to usage (fill = primary color at 78% opacity; empty track 3.5%); nil value draws "–" and a dashed outline. Animation 0.2 s ease-out (disabled with reduced motion).
- Under each group: "<Class name> ×<count>" (e.g. "Performance ×6").
- Layout algorithm: groups are packed side by side, 12 pt apart, into rows of the available width; a group that cannot keep 14 pt bars is split into balanced chunks of at most `min(12, (width+4)/(14·weight+4))` cores; each chunk needs `max(70, n·14·weight + (n−1)·4)` pt; spare width is distributed by weighted core count; one bar scale (the tightest) is applied to all segments so slower classes never get wider bars than faster ones. Row height 88 pt + 12 pt spacing.
- Accessibility: each bar "Core N" with value "NN%"; hint "Each bar is one logical core. Its fill height shows utilization, not remaining performance."

**Panel row** (System → "Hardware usage"): `› CPU [usage bar] NN%`. Usage bar is a 5 pt capsule: fill color accent < 60%, yellow < 85%, red ≥ 85%. Value `"%.0f%%"` (locale digits) or "-". Clicking the row expands/collapses the per-app list (and the per-core matrix + a foldable "Apps using CPU" sub-list when per-core is on; that fold starts expanded and while folded no per-app sampling happens). Sparkline 30 pt high, fixed 0…100% scale, accent color.

### 3.3 GPU

- **Usage:** macOS reads the GPU driver's "Device Utilization %" (first accelerator that reports it) → fraction.
- **Smoothing** (always applied to the displayed value and its history):

```
v = clamp(raw, 0, 1)
if prev == nil:      out = v
elif v > prev:       out = min(v, prev + 0.20)      // cap one-tick upward jumps
else:                out = 0.35·prev + 0.65·v       // fall fairly quickly
```
  Test vectors: (0.03 → 0.80) = 0.23; (0.23 → 0.80) = 0.43; (0.60 → 0.10) = 0.275; (nil → 1.4) = 1.0.
- Panel row: `› GPU [bar] NN%`, sparkline in cyan, expands "top GPU apps" list.
- GPU temperature: §3.5.

### 3.4 Memory

Readings (all bytes, each clamped to total physical memory):

| Reading | macOS formula | Shown as |
|---|---|---|
| total | physical RAM | denominator |
| app memory | (internal pages − purgeable pages) × page size (0 if negative) | "App Memory" |
| memory used | app + (wired + compressor + tag-storage pages) × page size | "Memory Used" (matches Activity Monitor) |
| compressed | compressor pages × page size | "Compressed" |
| cached files | external (file-backed) pages × page size | "Cached files" |
| swap used | swap usage "used" | "Swap used" (row hidden if unavailable) |
| pressure | kernel pressure level: 1 → normal, 2 → warning, 4 → critical, else unknown | traffic-light pill |

- **Metric choice** setting "Measure memory as": `used` (Memory Used, default) or `app` (App Memory). It drives the percentage, the "X / Y" text, the menu bar value and which history ring is graphed; unknown values fall back to `used`.
- Panel block "Memory": row `› Pressure [pill] 12.3 GB / 16 GB` (byte formatter in *memory* style: binary units labelled GB/MB), then indented secondary rows Compressed / Cached files / Swap used (each only if available), sparkline (mint, fixed 0…100%), expandable per-app memory list.
- Pressure pill: 7 pt dot with glow + label, capsule background at 13% of the color. Colors/labels: normal green "Normal", warning yellow "Caution", critical red "Critical", unknown secondary "-".
- Menu bar memory with value unavailable keeps its slot and shows `--%`.

### 3.5 Temperatures

All temperatures are stored in °C; display converts to the chosen unit (°C/°F, `F = C × 9/5 + 32`), rounded to integers: panel `"%.0f °C"`, compact `"%.0f°"`.

macOS reads the SMC (System Management Controller) once-enumerated keys:

| Sensor | Keys | Selection |
|---|---|---|
| CPU | keys starting `Tp` or `Te` (plus `Tf` on M3 family) | See algorithm below |
| GPU | keys starting `Tg` | max of values with 1 < v < 125, then must be ≥ 10 °C |
| Battery | keys matching `^TB[0-9]T$` | max of values with 1 < v < 125 |

**CPU sensor algorithm** (`displayedCPUTemperature`):
1. Platform from the CPU brand string: "Apple M1…M5" families have a verified per-core key set; other "Apple …" chips are "unmapped"; anything else "generic" (the A18 Pro is deliberately treated as generic).
2. Valid readings: 10 ≤ v < 125 (NaN/∞ rejected).
3. If the platform has a core key set and any valid reading comes from it → **max of core-set readings**.
4. Otherwise → **max of all valid CPU-family readings** (this fallback must stay: some Macs of a mapped generation carry none of the mapped keys).
5. Per tick the sampler reads only the core-set keys; it sweeps the remaining CPU-family keys only when the core set yields nothing.

**Stabilization / bridge** (all three temperatures): a reading is accepted if `v > 1 ∧ v ≥ minimum ∧ v < 125` (minimum 10 °C for CPU/GPU, 1 for battery) and becomes the cache with a fresh timestamp. Otherwise the cached value is served if it has ≤ 4 consecutive misses and its age ≤ bridge, where `bridge = max(12 s, 2.2 × temperatureStride × I)` (so ~35 s in background at I=2). The cache timestamp moves only on real reads — that timestamp is what alerts use.

UI: System → "Temperatures": two tiles (CPU, GPU), each a rounded cell with icon + label and the value in 15 pt semibold rounded digits, or "-". If both are nil: "Sensors unavailable on this Mac". Battery temperature is a row in the Power section ("Battery temperature").

### 3.6 Fan speed telemetry

Only when the Fan Control feature is installed (beta, off by default) **and** the machine reports a valid fan count (1…8) and every fan's actual RPM is valid (0…20,000). Values: actual RPM per fan. Menu bar value "1200/1350" (rounded, slash-joined) with label "RPM"; detail rows "Fan N" / "N RPM".

### 3.7 Network

#### 3.7.1 Throughput

- Source: cumulative **64-bit** received/sent byte counters summed over "real" interfaces. Excluded interface name prefixes (macOS): `lo, gif, stf, awdl, llw, nan, utun, bridge, ap, anpi, p2p, XHC, vmenet, tap, tun` (loopback, AirDrop, VPN tunnels, bridges, virtual NICs) so a VPN does not double-count the physical NIC traffic.
- `rate = Δbytes / elapsed` for down and up; a decreasing counter yields 0 for that direction (interface reset).
- No previous sample, or previous older than **10 s** → baseline (rates nil = "Measuring…").
- If the counter read fails, the reading is "unavailable" (rates nil) but the previous sample is **not** replaced, so the next successful read averages over the short gap (tested: reads at t=2 and t=5 with failures in between yield the average rate over 3 s).
- macOS-only quirk: if the inbound counter freezes while outbound advances on 2 consecutive samples, download is computed from per-process socket counters instead until the inbound counter moves again. Not needed on Windows.

#### 3.7.2 Session totals ("This session")

- `totalDown`, `totalUp` = sum of every positive counter delta **observed while the network family was being sampled**, since app launch; kept in memory only (not persisted, reset on relaunch).
- Deltas across a baseline reset (gap > 10 s, e.g. sampling paused because nothing needed network, or sleep) are **not** added — traffic during unsampled periods is not counted. Counter resets add nothing. Formatting uses binary units (§6.10).
- Panel: "This session   ↓1.2 GB  ↑340 MB".

#### 3.7.3 Panel "Network" section (default block order: speed, apps, totals, addresses, test)

- **Live speed:** two columns — `↓ <rate>` "Download" (accent) and `↑ <rate>` "Upload" (green); rate text 13.5 pt rounded semibold, numeric transitions; "Measuring…" when nil. A unit chip toggles every live network speed in the app (panel, menu bar, island): label "B/s" or "bit/s"; tooltip "Network speed unit · B/s → bit/s".
- Graph (if "Graphs" on and ≥ 2 samples): download filled (accent) and upload line (green, fill 8%), sharing one auto-scaled ceiling (§6.8).
- **Apps using network:** up to 6 rows "↓320K ↑12K" (compact units), "Measuring…" while warming up, "No apps using network now" when empty (§3.13.5).
- **Totals** (§3.7.2).
- **IP addresses:** "Local IPv4" with one line per address `192.168.1.23 (Wi-Fi)`; selectable text; the value is omitted when nothing is connected. Read only while this block is visible: on appear and on every snapshot.
- **Speed test** (§3.7.5).

#### 3.7.4 Local IP

- Enumerate interface addresses; keep **IPv4 only**, interface up and running, and interface passing the same exclusion filter as throughput. Format numeric host; append the system's display name for the interface in parentheses ("Wi-Fi", "Ethernet") when known; de-duplicate; sort ascending (string sort).
- **There is no public-IP lookup in the source app** (by design: "no request leaves the machine"). If the Windows port wants one, it is new scope and needs a privacy decision.

#### 3.7.5 Speed test (user-triggered)

| Phase | Request(s) | Measurement |
|---|---|---|
| Latency | 5 sequential `GET https://speed.cloudflare.com/__down?bytes=0` | RTT = completion time − start time, ms; result = **minimum** of the 5. Any transport error or non-2xx status → fail |
| Download | Repeated `GET https://speed.cloudflare.com/__down?bytes=90000000` back-to-back, **one request at a time** (no parallelism), until a **5 s** time box expires | Count response body bytes received since the phase started (time box starts before the first request, so it includes connection/TTFB time). Mbps = bytes × 8 / elapsed / 1,000,000. When the time box fires, the in-flight request is cancelled |
| Upload | One `POST https://speed.cloudflare.com/__up`, `Content-Type: application/octet-stream`, body = 100,000,000 zero bytes; same 5 s time box | Bytes counted = total body bytes sent so far (bytes handed to the network stack). Finishes at the time box or on completion |

- HTTP client: ephemeral session (no cookies/cache), cache bypass, request timeout 20 s. 90 MB per download chunk because the endpoint returns almost nothing for ≥ 100 MB.
- Error handling: a non-2xx response in a transfer phase fails the test and the error body is not counted; a completion error with zero bytes transferred fails; a cancelled request is not an error. Stale callbacks from cancelled tasks/time boxes are ignored (generation counter). After failure, no later phase is requested.
- States: `idle → latency → download → upload → done` or `failed(message)`; `cancel()` → idle. Starting is ignored while running. Results reset at start.
- UI: button "Speed test" (first time) / "Test again"; while running a small spinner + "Testing…"; when both results exist: `↓123 ↑45.6 Mbps` (each value `%.0f` if ≥ 100 else `%.1f`); below it "Latency: 12 ms" (rounded) or, on failure, "Test failed" in orange. No user data is sent (upload body is zeros).

### 3.8 Disks

#### 3.8.1 Volume list

- Mounted, local, non-hidden volumes with total capacity > 0. Volume name = localized name ?? name ?? last path component (or the path if empty).
- Capacities:
  - `rawFree` = available capacity (positive preferred).
  - `free` = "available for important usage" (includes space the OS would purge) for writable volumes, else rawFree.
  - `purgeable` = free − rawFree when positive (shown only if ≥ 500,000,000 bytes) — **no Windows equivalent; drop**.
  - `used` = total − rawFree (fallbacks from the metadata tool); `usedFraction = used/total` clamped 0…1.
- Metadata (macOS `diskutil info -plist`, 5 s timeout): BSD/whole-disk identifiers, file system label, internal/removable/ejectable flags, container sizes, SMART. Cached 30 s while foreground; reused up to 1 h in background (identity only changes on remount).
- File system label mapping: apfs→"APFS", hfs→"HFS+", exfat→"exFAT", ntfs→"NTFS", msdos→"FAT32"/"FAT16"/"FAT12"/"FAT" (from verbose name), cd9660→"ISO 9660", other 2–6 alphanumeric tokens with a letter → uppercased (e.g. "UDF"), anything else → no tag.
- Sort: internal volumes first, then the system volume first, then natural name order.
- Ejectable = not internal ∧ (ejectable ∨ removable) ∧ has a device identifier.

#### 3.8.2 I/O activity

- Cumulative bytes read/written per **physical whole disk**; volumes map to their physical disk (APFS volumes map to their physical store). Rates and session totals are keyed by disk ID; `uniqueIODevices` de-duplicates volumes sharing a disk.
- `rate = Δ/elapsed`, decreasing counter → 0; previous older than **15 s** → baseline (nil). Session totals accumulate positive deltas while sampled (in memory only).
- Snapshot-level graph series: sum of read rates and sum of write rates over unique I/O devices (pushed only if at least one rate exists).

#### 3.8.3 SMART / NVMe health (per volume's disk)

| Field | Source (NVMe vendor keys) | Display |
|---|---|---|
| Status | overall SMART status string (e.g. "Verified") | "Status" |
| Total read / written | data units (low + high<<32) × 512,000 bytes | `diskBytesPrecise` ("14.88 TB") |
| Temperature | raw > 150 → Kelvin (−273.15, valid −40…125), else °C valid 1…125 | in chosen unit |
| Health | 100 − "percentage used", clamped 0…100 | "NN%" |
| Power cycles, Power on hours | raw | integers |
| Unsafe shutdowns, Media errors | parsed | **not displayed** |

If no field is available: "SMART unavailable for this disk".

#### 3.8.4 Panel "Disks" section

- If no volume: "No mounted disks found."
- **Disk selector:** caption "Select disk"; adaptive grid (96–138 pt) of buttons: drive icon (internal/external), name (truncated middle), "NN% used"; ejectable disks have an inline eject button with states idle (eject icon) / ejecting (spinner) / ready (green check) / failed (red eject); tooltips "Eject" / "Ejecting…" / "Ready to remove" / "Could not eject". Selection persists while the disk stays mounted; default = first disk.
- Blocks for the selected disk (default order: usage, activity, smart, protection, tools):
  - **Disk usage:** title row (icon, name, tags: file system + "Internal"/"External"); 6 pt usage bar (accent < 75%, yellow < 90%, red ≥ 90%, animated 0.4 s); "NN% used" … "X GB available"; "used / total" (decimal units) and "X GB purgeable".
  - **Live activity:** "Read" (accent) / "Write" (pink) columns using `bytesPerSec` (binary units) or "Measuring…"; graph (read filled, write line) with byte-step ceiling; "This session ↓ <read> ↑ <written>" in decimal units.
  - **SMART:** rows above.
  - **External protection:** buttons "Eject" (disabled if not ejectable or ejecting) and "Eject all" (disabled if no ejectable non-excluded disk); caption "Eject before unplugging." / state text, or "No external disk ready to eject." for non-ejectable disks.
  - **Tools:** disk name + "Open" (opens the mount point in the file manager) and "Storage" (opens the OS storage settings).

#### 3.8.5 Eject and exclusions

- Eject one: mark "ejecting", unmount-and-eject; on failure fall back to a lower-level "unmount whole disk then eject" whose failure message (dissenter text) is kept; result state ready/failed.
- Eject all: every ejectable disk, de-duplicated by whole-disk ID, **minus excluded volumes**.
- Exclusion list (Settings, "Excluded drives"): entries are matched case-insensitively against the volume **name, volume UUID or mount path**. Add from a menu of currently mounted ejectable drives not already excluded, or "Other drive name…" free text ("Drive or volume name"); remove with a minus button; count badge; caption "Drives in this list are never unmounted when using Eject all disks." The list is trimmed and de-duplicated case-insensitively on save. (Also used by the Quick Toggles "Eject disks" action.)

### 3.9 Power & battery

#### 3.9.1 PowerReading

| Field | Meaning / macOS source | Formula / validity |
|---|---|---|
| `systemWatts` | total machine draw (SMC `PSTR`) | accept 0 < W < 1000; fallback when no sensor: if **not** on external power and batteryWatts < 0 → `−batteryWatts`; else nil |
| `adapterWatts` | real-time adapter input (SMC `PDTR`) | 0 < W < 1000; shown only when external power connected |
| `adapterMaxWatts` | charger rating (battery `AdapterDetails.Watts`) | > 0 |
| `batteryWatts` | signed battery flow | `V(mV)/1000 × I(mA)/1000` using Amperage (or InstantAmperage); + charging, − discharging; only if V > 0 and I ≠ 0 |
| `chargePercent` | `round(CurrentCapacity / MaxCapacity × 100)` | |
| `timeRemainingSeconds` | OS time-to-empty estimate (minutes, from the power source whose state is "Battery Power") | valid only if not on external power, not charging, and 1 ≤ minutes < 10,080 (7 days); negative = "still calculating" → nil |
| `healthPercent` | Preferred: OS "Maximum Capacity" from `system_profiler SPPowerDataType -json` (`sppower_battery_health_maximum_capacity`, string "93%" or number; nested keys accepted; placeholders rejected), refreshed at most every **30 min** off the hot path (5 s timeout). Fallback: `min(100, fullCharge / design × 100)` with fullCharge = NominalChargeCapacity ?? FullChargeCapacity ?? AppleRawMaxCapacity | 1…100 |
| `cycleCount` | battery cycle count | |
| `isCharging`, `externalConnected`, `hasBattery` | flags | `hasBattery` = internal battery installed (desktops report a battery service with "installed = false") |

#### 3.9.2 Power state wording

`state(isCharging, externalConnected, hasBattery)`: charging → "Charging"; else external → "Plugged in" (covers full / held at a charge limit); else hasBattery → "On battery"; else "Power metrics unavailable on this Mac".

#### 3.9.3 Panel "Power" section (default block order: charge, temperature, system, adapter, battery, remaining, health, peripherals)

| Block | Shown when | Content |
|---|---|---|
| Charge | hasBattery ∧ `monitorSysBattery` ∧ charge known | `⚡/🔋 Battery [bar] NN%` — icon bolt when charging or on external power, else battery; bar tint red < 20%, yellow < 40%, else green; battery-charge sparkline (green, 0…100%); "Apps using significant energy" disclosure (§3.13.4) |
| Battery temperature | hasBattery ∧ `monitorPwrTemperature` ∧ value | "Battery temperature" + "NN °C" |
| System | `monitorPwrSystem` ∧ systemWatts | bolt (orange) "System" + `MetricFormat.watts` ("8.5 W"/"23 W"); power sparkline with watt-step ceiling |
| Adapter | `monitorPwrAdapter` ∧ external ∧ adapterWatts | plug "Adapter" + watts; caption "<rated> max" (e.g. "96 W max") or "Plugged in" |
| Battery | hasBattery ∧ `monitorPwrBattery` ∧ batteryWatts | "Battery" + `abs(watts)`; caption "Charging" (green, flow ≥ 0) / "On battery" |
| Remaining | hasBattery ∧ `monitorPwrTimeRemaining` ∧ not external ∧ not charging | clock (green) "Battery time remaining" + "3h 42m"; caption "System estimate", or "..." with "Calculating…" |
| Health | `monitorPwrHealth` ∧ healthPercent | heart (pink) "Battery health" + "93%"; caption "<n> Cycles" |
| Peripherals | `menuBarPeripheralBattery` on ∧ devices exist | "Peripheral battery" heading + ≤ 5 device rows (icon by kind, name, "NN%"), "+N" if more |

If nothing is visible: "Power metrics unavailable on this Mac". Note: peripheral rows in this section appear only while the peripheral battery menu bar metric is enabled (that is what drives their sampling).

### 3.10 Peripheral batteries (Bluetooth/HID accessories)

- Enabled only while needed (§3.1.2). Two sources merged:
  1. **Fast read** (cached 15 s): HID device services exposing a battery percentage property (`BatteryPercent`, `BatteryPercentRemaining`, `BatteryLevel`, `Battery Level`, `Battery`), excluding built-in devices. Name from `Product`/`ProductName`/`DeviceName`. ID from serial/address/location/product/vendor or registry ID.
  2. **Bluetooth read** (every **300 s**, single-flight): system profiler JSON for *connected* Bluetooth devices (`device_batteryLevelMain`/`device_batteryLevel`, else the **minimum** of left/right/case/other components — e.g. AirPods = lowest component), plus a BLE GATT read of Battery Service `0x180F` / Battery Level `0x2A19` from connected peripherals (5 s overall timeout, 1-byte value ≤ 100). Profiler entries win over duplicate GATT readings (by ID and by normalized name).
- Percent parsing: numbers or strings with optional "%", rounded, must be an exact integer 0…100 (NaN, huge, negative → rejected).
- Kind: name/minor type contains keyboard → keyboard; mouse → mouse; trackpad/"track pad" → trackpad; airpods/headphone/headset/buds → audio; else HID usage (page 1 usage 2 → mouse; page 1 usage 6 or page 7 → keyboard; page 13 usage 5 → trackpad); else device. Menu labels: KBD, MOU, TRK, AUD, PER.
- De-duplication by ID, then by (lowercased name, kind). Sort: lowest percent first, then kind, then name.
- Each device carries the time it was actually observed (a fresh HID read does not refresh the age of cached Bluetooth data) — used by the island's low-accessory-battery alert (separate spec).
- Menu bar: `<KIND>` label + `"NN%"` of the lowest device + `"+k"` if more devices (k capped at 9), e.g. "MOU 24%+1".

### 3.11 Connected USB devices

- Enumerate USB device nodes; exclude: built-in or non-removable devices, vendor 0 ∧ product 0 (root controllers/hubs), device class 9 (hubs, which enumerate once per bus generation) and class 17 (USB-C billboard devices that only report display mode).
- Name = product string (trimmed; empty → UI fallback "USB Device"); vendor name optional.
- Stable ID: `vendor-product-registryID`, else serial, else `loc<locationID>`, else name. De-duplicate by ID; sort by name (case-insensitive).
- UI: System section row "Connected Devices  N  ›" (opens the device list detail); menu bar "USB" + count (reserved "99"); detail rows: cable icon, name (or "USB Device"), vendor. Empty: "No external devices connected". Count strings: "1 device connected" / "%d devices connected". Hub description: "Count connected external USB peripherals".

### 3.12 Uptime

Wall-clock seconds since boot time (fallback: monotonic uptime). Format: ≥ 1 day `"Nd Nh"`, ≥ 1 h `"Nh Nmin"`, else `"Nmin"` (e.g. 0 → "0min", 3600 → "1h 0min", 93,600 → "1d 2h"). Row: clock icon + "Up for 3d 4h".

### 3.13 Per-app breakdowns (ProcessUsageService)

Opened by expanding a row (CPU, GPU, Memory in System; "Apps using significant energy" in Power; network apps block; detail panels). Rows are computed off the UI thread; while a list is open it refreshes on snapshot updates when `elapsed ≥ 0.8 × refreshInterval` (refreshInterval = monitor interval for CPU/GPU/energy, **4 s** for memory and network). An empty first result shows "Measuring…".

**Row model:** pid (of the owning app), display name, value (CPU/GPU/energy: percent 0–100; memory: bytes; network: bytes/s with separate down/up), optional process start time (identity for kill). Max 60 cached rows per kind.

**App grouping ("responsible process"):** every process is billed to the process responsible for it (helpers roll up into their app), values are **summed** per owner, the owner's localized app name and icon are shown, falling back to the process name, then executable file name, then "pid N".

| Kind | Sampling | Filter / scale | Limit |
|---|---|---|---|
| CPU | Cumulative user+system CPU ns per process; delta over wall time since previous sample: `pct = Δns / (elapsed_ns × logicalCPUs) × 100` (share of the whole machine). Min sample spacing `max(0.5, 0.8 × interval)`; baseline older than `max(30 s, 4 × interval)` → re-prime | drop rows < 0.01%; after grouping, **reconcile**: `scale = min(1, aggregateCPU% / Σrows)`; clamp each 0–100 | 15 in System / detail |
| GPU | Cumulative GPU time (ns) per process from GPU driver clients; `pct = Δns / elapsed_ns × 100`, min 100 | drop < 0.05%; group; reconcile with aggregate GPU% | 15 |
| Memory | Candidates ranked by `ps` RSS (top `max(10 × limit, 120)`), value replaced by the kernel **physical footprint** (what Activity Monitor shows; RSS over-reports), then grouped (summed) | > 0 | 15 |
| Energy | `topCPU(max(3 × limit, 12)) + topGPU(same)` summed per owner | keep ≥ 2%; sort desc; clamp 0–100 | 15 |
| Network | Per-process cumulative bytes in/out sampled every **5 s** by a background loop while a "lease" is active (lease 12 s, renewed on every request; closing the view shortens it to ≤ 4 s); rates from deltas (gap ≤ 30 s, counter decrease → 0); grouped; value = down + up | > 0; sort desc | 6 in panel, 15 in detail |

Values: CPU/GPU/energy `"%.1f%%"`; memory byte formatter (memory style); network `"↓<compact> ↑<compact>"` in the chosen unit.

**Row interactions (`ProcessUsageRow`):** 14 pt app icon, name (truncated middle; tooltip full name), value (secondary, monospaced digits).
- Click → bring the app to the foreground, only if it is a regular GUI app still running.
- Context menu "Force Kill" — only when the separate **Kill Process** feature (beta, off by default) is installed; disabled unless the row has a start time, that start time still matches the live process (PID reuse guard), and neither executable nor row name is protected. Confirmation dialog: title "Force Kill <name>?", buttons "Force Kill" (destructive) and "Cancel" (Escape). Kill = forced terminate (SIGKILL; GUI apps via force-terminate); permission denied → one administrator prompt that re-checks the process start time before killing. Protected: pid ≤ 1, the app itself, kernel_task, launchd, WindowServer, loginwindow.
- Identity safety: a row only gets a start time if the process existed before the sample started.
- "Open Activity Monitor" button (arrow icon) next to "Hardware usage" and in detail process cards.

### 3.14 History graphs & sparklines

- **Rings:** capacity **120** per series; append on each *fresh* read only (carry-over ticks do not push), drop oldest beyond capacity. Effective time window = 120 × read period (e.g. foreground I=2 → 4 min; background GPU 10 s → 20 min).
- **Series:** CPU (fraction), GPU (fraction), memory used/total and app/total (fractions), net down/up (B/s), disk read/write (B/s, summed), system power (W), battery charge (fraction).
- **Published only in foreground**; a graph renders only with ≥ 2 points.
- **Scaling:** CPU/GPU/memory/battery fixed 0…1 with ceiling label "100%". Network: `ceiling = networkGraphCeiling(max(peakDown, peakUp, 1))` (byte steps of 1024, or rounding in bits with 1000 steps when the unit is bits and dividing by 8 back to bytes); label via the network rate formatter ("2.0 KB/s", "20 Kbps"). Disk: `graphCeiling(max(peakRead, peakWrite, 1), 1024)`, label `bytesPerSec`. Power: `graphCeiling(peak, 1000)`, label in watts ("20 W"). See §6.8.
- **Drawing:** filled area under a polyline; fill = linear gradient from color at 16% (top) to 0% (bottom); line width 1.5 pt, round caps/joins; x spans the full width evenly (index/(n−1)); optional zero baseline (secondary color at 28%, 1 pt). Two-series graphs (network, disk) overlay the second series with fill 8% and share the ceiling.
- **Scale label** (setting "Show graph scale", default on): dashed line at the top (1 pt, dash 2/3, secondary 40%) and the ceiling text (9 pt, monospaced digits) in a small rounded material chip at the top-left (over the oldest samples).
- Heights: 30 pt in panel sections, 38 pt in detail panels. Colors: CPU/network-down/disk-read accent, GPU cyan, memory mint, network-up green, disk-write pink, power orange, battery green.
- Per-graph toggles: `monitorGraphCPU`, `…GPU`, `…Memory`, `…Network`, `…Disk`, `…Power`, `…Battery` (all default on).

### 3.15 Alerts (MonitorAlertService)

- Active only while at least one alert is enabled **and** its metric family is installed (power alerts additionally need an internal battery). Enabling any alert requests notification permission; Settings shows "Notifications for Vorssaint are off in System Settings, so alerts cannot appear." (orange) when denied (checked 1 s after a toggle).
- Evaluated on **every published snapshot** (so at background cadence when no UI is open).
- **Cooldown** per alert kind (`monitorAlertCooldownMinutes` ∈ {2, 5, 15, 30, 60}, default 15): a kind that fired less than cooldown ago is suppressed. Last-sent times are in memory only (reset on relaunch). One disk alert at a time (kind-level, not per disk).

| Rule | Trigger | Threshold (default, allowed, step) | Sustain | Title / body (English) |
|---|---|---|---|---|
| High CPU | `cpuUsage ≥ threshold/100` | 90% (50–100, step 5) | 12 s gate | "High CPU" / "CPU stayed above %d%% for a few seconds." |
| High CPU temperature | `cpuTemperature ≥ threshold` °C | 90 °C (70–105, step 5) | 12 s gate | "Hot CPU" / "CPU reached %@." (temperature formatted in the chosen unit, e.g. "194 °F") |
| High battery temperature | `batteryTemperature ≥ threshold` °C (battery Macs only) | 40 °C (30–50, step 5) | 12 s gate | "Hot battery" / "Battery reached %@." |
| Critical memory pressure | pressure == critical | — | none | "Critical memory" / "Memory pressure reached the critical level." |
| Low disk space | first volume (in display order) with total ≥ 10,000,000,000 bytes and `free/total × 100 < threshold` | 10% (5–30, step 5) | none | "Low disk space" / "%@ has less than %d%% free." (volume name, threshold) |
| Low battery | has battery ∧ **not charging** ∧ charge ≤ threshold (not gated on external power: a machine on AC that is not charging, e.g. held at a charge limit, can still fire) | 15% (5–50, step 5) | none | "Low battery" / "Battery is at %d%%." |

Out-of-range stored thresholds fall back to the default (not clamped).

**Sustained gate** (one per CPU usage, CPU temperature, battery temperature):

```
shouldAlert(reading, threshold, readAt):
  if reading == nil or readAt == nil or reading < threshold: reset(); return false
  if readAt == lastReadingAt: return false            // same reading re-served: never ages
  lastReadingAt = readAt
  if heldSince == nil: heldSince = readAt; return false
  return readAt − heldSince ≥ 12 s
```
`readAt` uses a monotonic clock that stops during sleep (macOS system uptime; Windows: `QueryUnbiasedInterruptTime`), so a sleep in the middle cannot pass for a sustained stretch. Dropping below the threshold restarts the window. Gates reset when the rule is disabled or alerts stop. Consequence: in background, temperature is read every 15 s, so a temperature alert needs two hot reads ≥ 12 s apart (~15 s).

Settings UI (Settings → Monitor → "Alerts"): one tile per alert (checkmark toggles; the options half has a stepper "CPU above 90%", "Temperature above 90 °C", "Free space below 10%", "Battery below 15%"), then "Repeat the same alert after" (2 minutes / 5 minutes / 15 minutes / 30 minutes / 1 hour; disabled while no alert is on), then the caption "Alerts fire when their selected limits are reached. CPU use and temperature alerts ignore spikes shorter than about 12 seconds. The repeat setting only limits repeats of the same alert." Battery alerts appear only on machines with a battery. (The panel's System section has an Alerts block too, but it is filtered out of the panel; alerts are configured in Settings only.)

### 3.16 Menu bar readouts (→ Windows tray)

#### 3.16.1 Tokens (MenuBarMetric)

Default order (user-reorderable by drag in Settings): `cpu, cpuTemperature, gpu, gpuTemperature, memory, battery, batteryTime, batteryTemperature, peripheralBattery, network, diskUsage, diskActivity, connectedDevices, power, fanSpeed`. Each has its own boolean key (all **off** by default). A token renders only if enabled ∧ its family is installed ∧ hardware supports it (battery, batteryTime, batteryTemperature need an internal battery; fanSpeed needs telemetry). Families: cpu/cpuTemperature → CPU; gpu/gpuTemperature → GPU; memory; network; diskUsage/diskActivity → Disk; connectedDevices; battery/batteryTime/batteryTemperature/peripheralBattery/power → Power; fanSpeed → Fan Control.

#### 3.16.2 Information content per token (values mode)

Each block is two lines: a small **label** on top and the **value** below (dense style: label 6.6 pt medium; value 12 pt semibold, monospaced digits; block height 21 pt). Minimum reserved value text (standard spacing) keeps widths stable.

| Token | Label | Value | Reserved | Hidden when |
|---|---|---|---|---|
| cpu | `CPU` | `NN%` (rounded, clamped) | `100%` | no reading |
| cpuTemperature | `CPU°C` / `CPU°F` | `NN°` | `999°` | no reading |
| gpu | `GPU` | `NN%` | `100%` | no reading |
| gpuTemperature | `GPU°C` | `NN°` | `999°` | no reading |
| memory | `RAM` | `NN%` of the chosen memory metric, or `--%`; optional colored pressure dot (green/yellow/red/grey, Ø 4.8 pt, 4 pt gap) before the value when style is `dot`/`both`; style `dot` shows only the dot | `100%` | never (keeps its slot) |
| battery | battery glyph + `NN%` | glyph: bolt when charging or on external power, else level 100/75/50/25/0 for ≥85/60–84/35–59/10–34/<10; value color red/orange when the low/early battery warning applies (§3.16.5) | `100%` | no charge |
| batteryTime | `BAT` | `"3h 42m"`; `"..."` while on battery and still calculating | `99h 59m` | on external power / charging / no battery |
| batteryTemperature | `BAT°C` | `NN°` | `999°` | no reading |
| peripheralBattery | `KBD`/`MOU`/`TRK`/`AUD`/`PER` | `NN%` or `NN%+k` | `100%+9` | no devices |
| network | — (two stacked right-aligned lines) | `↓1.2M` over `↑320K` (bytes) or `↓9.6Mb` over `↑2.1Mb` (bits); order swapped when "Upload above download" | fixed width for 5 chars after the arrow | rates nil |
| diskUsage | `DSK` | style percent `NN%` / free `X GB` / used `X GB` (decimal units) of the **primary disk** (first internal, else first) | `100%` or `1000 GB` | no disk |
| diskActivity | — (two stacked lines) | `R<compact>` over `W<compact>` (summed over unique physical disks, bytes) | fixed | no rates |
| connectedDevices | `USB` | count | `99` | never |
| power | `PWR` | `NNW` (rounded) | `99W` | no system watts |
| fanSpeed | `RPM` | `1200/1350` | `20000` per fan joined by `/` | no telemetry |

Stacked rate blocks: monospaced 9.2 pt semibold, line height 10 pt, block height 20 pt.

**Combine usage and temperature** (values mode only, default on): CPU + CPU temperature become one block `CPU` / `42% 61°`; same for GPU; battery charge + battery temperature become `BAT` / `80% 31°` (keeping the battery warning color). If only the temperature of a pair is enabled, the label gets the unit suffix (`CPU°C`). Combined reserved width `100% 999°`.

#### 3.16.3 Bars mode ("Usage display: Bars")

CPU, GPU, RAM and DSK (percent style only) are drawn as a vertical gauge instead of text; everything else stays numeric. Gauge (dense): label letters stacked vertically in a 6 pt column (6.1 pt semibold), optional pressure dot (Ø 4.4 pt) for RAM, then a rounded outline 9 × 18 pt (stroke 1.15 pt, label color) whose inner area fills bottom-up quantized to **15 steps**; a missing value draws a horizontal dash. Fill color by level: normal `#64D2FF`, elevated `#FFD60A` from 70%, critical `#FF453A` from 90% (colors and the two thresholds configurable; medium 1–99, high > medium, ≤ 100). Combine-temperatures is not available in bars mode (a temperature next to a bar is a separate block).

#### 3.16.4 Layout options

- **Spacing:** `compact` (default) vs `standard`. Glue between blocks: standard " " (dense), compact a hair space (U+200A). Compact also sizes each value by its own digit count (each digit replaced by "8"), with a floor of 2 digits and a per-label session high-water mark, so a block widens once (e.g. 9% → 100%) and never wobbles.
- **Separate metrics into their own items** (default off): each token (or combined CPU/GPU/battery group when combining) becomes its own status item with its own click target; tooltip = metric title. A separate item whose reading disappears stays (blank) for up to **5** consecutive empty renders before being removed. Click → that metric's detail panel; right click → context menu.
- **Hide the app icon while metrics are shown** (default off): hides the glyph when metrics render in the main item (combined mode), or hides the whole main item (separate mode) when at least one metric item shows a reading and the main item has no title. The icon always returns for signals: update available, mic muted badge, or a running keep-awake session whose active icon looks different from the idle one (any tint other than "No color", or an icon other than the app logo — true with the defaults, since the default tint is orange). Signals are frozen while a panel is anchored to an item, so the item does not move under an open panel. Caption: "The icon returns by itself when metrics leave the bar and when there is something to signal (an update ready, “Keep awake” running or the microphone muted)."
- Custom app glyph: optional system symbol name (`menuBarIconSymbol`, empty = app logo).
- Fonts: single-line title 11.6 pt monospaced system font (medium), stacked 9.4 pt semibold.

#### 3.16.5 Battery warning tint

The battery block's value and glyph turn **red** when charge ≤ the island's low threshold (default 10, 1–99) and **amber** when the early warning is on and charge ≤ early threshold (default 20, ≥ low+1) — only when `notchLowBatteryTint` is on (default **off**), the charge is known, not on external power, and `notchLowBatteryMenuBar` (default on) allows it in the menu bar. Temperature-only blocks never inherit the warning.

#### 3.16.6 Main item: countdown, tooltip, icon, clicks

- Title = keep-awake countdown (if active and "Show remaining time next to the icon") + two spaces + metric blocks (combined mode). Countdown: timed → `H:MM` if ≥ 1 h else `N min` (min 1); indefinite → `∞`. Refreshed by a 30 s timer (tolerance 5 s) only while a timed session with countdown is shown.
- Tooltip: idle "Vorssaint: normal sleep"; timed "Vorssaint: awake until 14:30"; indefinite "Vorssaint: awake indefinitely"; automatic session → the condition text (§3.18.6).
- Icon: idle → app glyph (monochrome template); keep-awake active → chosen active icon (app logo, coffee cup, eye, moon, lightbulb) tinted with the chosen color (orange default, green, blue, purple, pink, none = monochrome); update available → blue glyph; mic muted → red slashed-mic badge appended.
- Left click → panel. Right click → context menu, or toggle keep awake when "Right-click the menu bar icon to toggle Keep Awake" is on. Context menu: "Enable keep awake"/"Disable keep awake"; when inactive "Activate for…" submenu (15 minutes, 30 minutes, 1 hour, 2 hours, 4 hours, 8 hours, Indefinitely); other features' items ("Cleaning Mode", "Uninstall an app…", "Open shelf" when those features are on); separator; "Settings…"; "About Vorssaint"; "Check for updates…"; separator; "Quit Vorssaint".

#### 3.16.7 Windows rendering guidance (information content must match)

The Windows notification area shows only a small square icon (16×16 logical px at 100% scale) and a tooltip (max 127 characters); deskbands/toolbars are not supported on Windows 11. Recommended mapping:
1. **Main tray icon** = app glyph / keep-awake state (same states as §3.16.6), tooltip = keep-awake status + one-line summary of pinned metrics (e.g. `CPU 42% · RAM 61% · ↓1.2M ↑320K`).
2. **"Separate items" mode → one tray icon per pinned token**, each rendering the value as 1–2 lines of tiny text or a bar (bars mode maps naturally to a 16 px vertical gauge with the same color thresholds), tooltip with the full value and title; click opens that metric's detail flyout. Use the same reserve/“never wobble” rules; the network/disk blocks need 2 lines → consider showing only one direction per icon or alternating.
3. Optional **taskbar overlay** (borderless always-on-top window aligned to the taskbar, like third-party traffic monitors) that reproduces the multi-block strip exactly; must handle taskbar position, auto-hide, multiple monitors, DPI, full-screen apps. Higher risk; P2.
4. Countdown → tooltip text and/or a small badge on the tray icon.

### 3.17 Panel monitor sections & detail panels

Common section behavior: collapsible header with title ("System", "Network", "Disks", "Power"); **edit mode** (pencil) lets the user drag blocks to reorder (stored as comma-joined block IDs) and hide/show blocks inline ("Hide from panel"/"Show in panel"; hidden blocks remain as dimmed rows in edit mode); a reset action restores default order and visibility. Whole sections can be hidden (`monitorShowSystem/Network/Disk/Power`). Unavailable families remove their blocks entirely.

#### 3.17.1 System (default order: temps, usage, memory, uptime, connectedDevices)
Temperatures tiles (§3.5), "Hardware usage" with CPU/GPU rows (§3.2–3.3) and the "Open Activity Monitor" button, Memory block (§3.4), uptime row (§3.12), Connected Devices row (§3.11). Toggles: `monitorSysTemps`, `monitorSysCPU` (+ graph + "Per core"), `monitorSysGPU` (+ graph), `monitorSysMemory` (+ graph), `monitorSysUptime`, `monitorSysConnectedDevices`.

#### 3.17.2 Network
See §3.7.3.

#### 3.17.3 Disks
See §3.8.4.

#### 3.17.4 Power
See §3.9.3.

#### 3.17.5 Expansion rules
Only one breakdown (CPU / GPU / Memory) is expanded at a time; collapsing or the section disappearing clears its rows. Energy and network lists are independent.

#### 3.17.6 Detail panels (from a separate menu bar item)

| Kind | Needs | Summary (big value 28 pt rounded / secondary line) | Graph (38 pt) | Detail rows | Process card |
|---|---|---|---|---|---|
| cpu | cpu + cpu temp | `42%` / CPU temperature or "Temperatures" | CPU | Hardware usage, Temperatures (or "Sensors unavailable on this Mac"), Up for | "Hardware usage" (CPU apps, 15) |
| gpu | gpu + gpu temp | `12%` / GPU temperature | GPU | Hardware usage, Temperatures | GPU apps |
| memory | memory | `61%` / `9.8 GB / 16 GB` | chosen memory history | Memory Used/App Memory "X / Y", Pressure (pill), Compressed, Cached files, Swap used | memory apps |
| network | network | `1.2M` (compact down) / "Upload 320K" | down+up | Download, Upload, This session | network apps (+ speed test card) |
| disk | disk | primary disk `NN%` / "X GB available" | read+write | `<name>` NN% used, available, purgeable (≥ 500 MB), Read, Write | — |
| battery | power+battery+peripheral+battery temp (or peripheral only on desktops) | `NN%` / power state (or lowest peripheral) | battery charge | Charge, Battery (flow · state), Temperatures, Battery health, Cycles; then up to 5 peripherals or "Peripheral battery: No devices found" | — |
| power | power | `NNW` / "Plugged in"/"On battery" | system power | System, Adapter, Battery, Battery time remaining (or "Calculating…") | "Apps using significant energy" |
| fan | fan speed | `1200 RPM` / "Fan 1" | — | "Fan N: N RPM" or "Sensors unavailable on this Mac" | — |
| connectedDevices | USB | count / "N devices connected" | — | device rows | — |

Token → detail mapping: cpu/cpuTemperature → cpu; gpu/gpuTemperature → gpu; memory; network; diskUsage/diskActivity → disk; battery/batteryTemperature/peripheralBattery → battery; batteryTime/power → power; fanSpeed → fan; connectedDevices.

### 3.18 Keep awake (KeepAwakeManager)

#### 3.18.1 Session model

State: `isActive`, `endDate` (nil = indefinite), `sessionTrigger` (manual | automation), `sessionMinutes` (preset that started it; nil for "until" and automation), `activeAutomationConditions`.

| Action | Behavior |
|---|---|
| `activate(minutes)` | minutes sanitized to {0, 15, 30, 60, 120, 240, 480} (else 0 = indefinite). end = now + minutes. **Records the pick**: `defaultDuration = minutes`, `keepAwakeSwitchUsesUntil = false` |
| `activate(until: date)` | ignored if date ≤ now. `sessionMinutes = nil`. Records `keepAwakeSwitchUsesUntil = true`, `keepAwakeUntilTime = date` |
| `startLastPick()` (main switch, hotkey, menu toggle, command bar, radial menu, quick launcher) | if last pick was "until" and the saved time is still in the future → `activate(until:)`; otherwise `activate(minutes: defaultDuration)`. A passed end time never rolls to tomorrow here |
| `toggle()` | active → stop (if the session is automatic or automation conditions currently hold, suppress automation until its conditions clear); inactive → `startLastPick()` |
| `extend(minutes)` | only for timed sessions: `end = max(end, now) + minutes` (panel chips +15m, +30m, +1h) |
| `deactivate(reason)` | release everything; reasons manual / timer / battery / quit. Notifications only for **timer** ("Session ended" / "Time is up. The Mac will sleep normally again.") and **battery** ("Vorssaint disabled" / "Low battery. Normal sleep was restored to protect the charge.") |
| Auto start | "Keep Awake when Vorssaint opens" → on launch (after crash recovery) `activate(minutes: defaultDuration)` |
| Hub removal | uninstalling Keep Awake ends a running session and stops automation monitoring |

"Until" time resolution: the picker edits only hour and minute; at start the next occurrence after now is used (`resolvedUntilDate`: today if still ahead, else tomorrow; DST-safe).

#### 3.18.2 While active

- **System sleep prevention** assertion always ("PreventUserIdleSystemSleep", reason "Vorssaint: keep the Mac awake").
- **Display sleep prevention** assertion ("PreventUserIdleDisplaySleep", "Vorssaint: keep the display on") unless "Allow the display to sleep" is on ("Keeps the Mac awake while the display follows its normal sleep timer."). Toggling the option applies live.
- **Battery protection:** check at start and every **30 s** (tolerance 5 s): if limit > 0, on battery power, and charge ≤ limit → end with reason battery. Limits: Never (0), 5%, 10% (default), 15%, 20%. The panel then shows (orange) "Battery at N%. Plug in or lower the battery limit to start" instead of the hint "Click a chip to start. Click it again to stop".
- **End timer** fires at `endDate`: if a manual session ends and the automation would itself start a session now (conditions satisfied under the current Any/All mode, not suppressed, battery protection allows), the session is **handed over** to an automatic indefinite session (no notification); otherwise end with reason timer.
- **Pointer jiggle** ("Move pointer slightly", needs Accessibility): every N minutes (1, 2, 5 default, 10, 15; timer tolerance min(10 s, 10%)) post a mouse move of 1 px (right; else left; else down; else up; staying ≥ 2 px inside the current display's bounds) and 80 ms later move back to the original position only if the pointer is still within 2 px of the target (user did not move it).

#### 3.18.3 Pause while locked

Option "Pause while the Mac is locked" ("Follows normal sleep rules while locked and resumes the remaining session after you unlock."): on screen lock, release assertions, undo closed-lid mode, stop battery watch and jiggle (session stays logically active). On unlock: if the end time already passed → timer handling (handover or end); if the session is automatic and conditions no longer hold → end; otherwise re-apply everything. Starting a session while locked starts it paused.

#### 3.18.4 Automation

Conditions (tiles): **External display** ("Active while an external display is connected"), **Power** ("Active while connected to power"), **Applications** ("Active while a selected app is running"; editable list "Selected apps", "Add an app…", "Remove"; caption "Keep Awake starts while any of these apps is open, even in the background.").

- Matching: external display = any online display that is not built-in (debounced 0.35 s after a display configuration change; last known value reused if the query fails); power = battery snapshot not on battery (false on desktops without a battery snapshot — note: the "Power" condition never matches on a desktop); apps = any running process whose app identity is in the list (observed via the running-apps list, debounced 0.1 s).
- Enabled set excludes "Applications" while its list is empty.
- Mode (`keepAwakeAutomationRequireAll`): **Any** (default) — satisfied when ≥ 1 enabled condition matches; **All** — satisfied when matching == enabled. The Any/All segmented control appears only when > 1 condition is selected. Captions: "Starts when any selected condition is active." / "Starts only when every selected condition is active."
- Action: satisfied ∧ no session → start automatic indefinite session (only if battery protection allows); not satisfied ∧ automatic session running → end it (manual reason, no notification); a manual session is never ended by automation.
- Manual stop of an automatic session (or of any session while conditions hold) → automation suppressed until conditions stop being satisfied. A deliberate change to automation preferences clears the suppression.
- While locked with pause-when-locked on, automation does not start sessions.
- Automation is evaluated only after launch recovery (§3.18.5) has finished, and never while the app is quitting.
- Status text for automatic sessions: single condition → its "Active while…" text; several → "Active because an automatic condition is met".
- Monitoring resources exist only while their condition is enabled (display change observer, power source notification, running apps observer).

#### 3.18.5 Closed-lid mode ("Keep going with the lid closed")

macOS: while a session is active and the preference is on, the app runs `pmset disablesleep 1` through a password-free sudo rule it installs once with administrator approval (`/etc/sudoers.d/vorssaint-clamshell`, rule restricted to `pmset disablesleep 1|0`, granted by uid); it runs `disablesleep 0` when the session ends, pauses, or the app quits. Robustness rules (keep the *intent* on Windows):
- Persist a "sleep disabled" recovery marker **before** submitting the change; clear it only after a confirmed restore; on launch, if the marker is set, read the current state and restore (prompting for admin only if the passwordless path fails).
- If restoring finds the lid already closed and the system policy allows lid sleep (no lid-applicable assertions active), request system sleep, retrying up to 10 times 0.5 s apart (synchronously on quit). Otherwise the machine would stay awake in a bag until the battery dies.
- Setup failures turn the preference back off and show "Couldn’t turn on closed-lid mode. Try again."; one automatic re-setup per user attempt.
- Captions (state machine): "Configuring…" · "Couldn’t turn on closed-lid mode. Try again." · "Sleep fully disabled. Mind the power" (active) · "Applied whenever “Keep awake” is active" (preferred, no session) · "Ready. Toggles without a password" · "Will ask for the administrator password once". Explanation: "“Keep going with the lid closed” fully disables sleep while “Keep awake” is active and is reverted automatically when the session ends or the app quits. Prefer using it plugged in."
- Sub-option "Dim the display to zero" (Mac-only, X): while closed-lid mode is active, on lid close save the built-in panel brightness (only if > 0) and set it to 0; on lid open / option off / mode end restore it (6 attempts 0.5 s apart; persisted marker for crash recovery).

#### 3.18.6 Panel card "Keep awake"

- Header row: status line ("Ends in 1 h 05 min" ticking every second / "Active until you turn it off" / automation status / "The Mac follows its normal energy rules") + extend chips (+15m, +30m, +1h, only for timed sessions) + main switch (starts last pick / toggles off).
- Duration chips: `15m 30m 1h 2h 4h 8h ∞` + "Until…" chip (one row, or 4 + 3 + Until on narrow widths). Clicking a chip starts that preset (switching from any other session); clicking the highlighted chip stops it. The highlighted timed chip shows a live countdown `58:12` / `1:05:12` (chip widths reserve "0:00:00"). The Until chip shows the end time while its session runs; its popover has a time field, hour (0–23) and minute wheels (scroll, click neighbor, arrow keys; wrapping), a summary line "Today · 2h 5m" (refreshed every 30 s) and "Start".
- Hint line (always present to avoid height jumps): "Click a chip to start. Click it again to stop" or the battery note.
- "Options" disclosure: active icon + color picker, "Allow the display to sleep", "Keep Awake when Vorssaint opens", "Automation" disclosure (badges for enabled conditions, "Off" when none; tiles, Any/All, app list, "Pause while the Mac is locked"), "Move pointer slightly" (+ interval, permission button).
- Divider, closed-lid row with caption.

#### 3.18.7 Settings → Energy → Keep awake

Card 1: session row (title "Keep awake", status/countdown, switch), "Default duration" chips (15 minutes … 8 hours, Indefinite; choosing one sets the default and makes the switch start it), "Automation" section (caption, tiles, Any/All, app list), "Pause while the Mac is locked". Card 2 "Options": "Keep Awake when Vorssaint opens" (caption "Starts a session with the default duration."), "Right-click the menu bar icon to toggle Keep Awake" ("Replaces the right-click context menu."), "Show remaining time next to the icon", "Allow the display to sleep", "Move pointer slightly" + "Interval" (`N min`) + Accessibility row, active icon/color pickers, "Disable when battery drops below" (battery machines; "Keeps a forgotten session from draining the MacBook battery."), "Keep going with the lid closed" (+ "Dim the display to zero").

Global shortcut: "Enable shortcut for “Keep awake”" (on by default), default ⌃⌥⌘K (`control+option+command:40`) → `toggle()`.

### 3.19 Display brightness (BrightnessService)

Feature switch "Control displays" (`brightnessControlEnabled`, default **off**; caption "Brightness and on or off controls for the built-in and external displays, here and in the menu bar panel."). While off: no observers, routes, I2C work or taps (pending display restorations still observe wake/lid until done).

#### 3.19.1 Display list & routes

On start, on panel/Settings appear, on display configuration change (one fixed 0.5 s settle window from the first event — change storms are coalesced), and 3 s after wake (connections must settle), the service rebuilds its routes on a serial worker:
1. For each online display: skip mirrors (the source's slider controls them), virtual and AirPlay displays. Inactive displays get a row "Off" with no slider.
2. **System route**: if the OS brightness API answers for the display (built-in panel, Apple displays) → `system`. A reading taken while the panel sleeps is not trusted (the remembered level is used instead).
3. **DDC route** for remaining external displays: match the display to an I2C service by identity score (EDID vendor/product/manufacture week+year/image size chunks +1 each, connection path match +10, product name +1, serial +1; greedy best-first; if exactly one display and one service remain they are paired). Probe luminance:
   - reply parsed → **live** (readable): slider from the monitor's own value.
   - writes accepted but no reply → **write-only**: slider works, seeded from this session's last value (0.5 initially); the connection path is cached as write-only (max 16 entries) so it is not probed again.
   - writes rejected → **dead** → software route.
4. **Software route** (gamma dimming) for everything left: capture the display's unmodified gamma curve once (re-captured while not dimmed so profile/night-shift changes become the new baseline), and re-apply a dim this app had applied (a display returning from a connection gap gets at least 25% so a black screen is recoverable by replugging).
5. Displays without any brightness route are still listed (for the on/off button) when display switching is available.

Path key = display fingerprint (vendor:model:serial) + connection location; used for write-only cache, forced-software and extended-dimming preferences.

#### 3.19.2 Writes

- `setBrightness(value, display, showOSD, smooth)`: clamp 0…1, update the published slider value immediately, remember the level (with the monitor fingerprint so a reused display number never inherits it), and queue one pending write per display (a slider drag folds into a single write of the newest value).
- System route: key steps ease like the OS keys (the "smooth" API takes a *change* from the currently reported level; if refused or it doesn't land within 0.001, write the level directly); slider writes land at once.
- DDC route: `deviceValue = round(value × max)` (max 0 → 100), Set VCP 0x10. Pacing: ≥ 50 ms between whole commands to the same display (issue: monitors drop signal on faster streams); inside a command: 10 ms before each of 2 write cycles, retry up to 4 times with 20 ms pauses. Probe: request, wait 50 ms, read 11-byte reply.
- A failed write on a write-only path forgets its cache and forces a rebuild.
- Software route: scale the baseline gamma table by `factor = value` (linear, 0 = black); value ≥ 0.999 restores the exact baseline.
- **Remembered level trust window 3 s:** a key step within 3 s of the last known level steps from it; after a pause, a readable DDC display is read first (off the UI thread; key presses arriving meanwhile are accumulated and added) so steps start from the monitor's real level (it has its own buttons).
- On quit / feature off: restore every gamma curve this app dimmed (only if the display still has the same monitor fingerprint) and switch back on displays this app switched off.

#### 3.19.3 Dimming choices (per monitor & connection) — P2

- **"Dim the picture"** (forced software dimming): offered for write-only DDC monitors (a channel that accepts writes but may silently ignore them); moves the display to the gamma route. Turning it off restores the picture, forgets the write-only verdict and re-probes.
- **"Extra dimming"** (extended dimming): offered in Settings for readable DDC monitors; the lowest 25% of the slider dims the picture after the monitor reaches its hardware minimum:

```
level < 0.25 : hardware = 0,                    picture = level / 0.25
level ≥ 0.25 : hardware = (level − 0.25) / 0.75, picture = 1
```
  The picture is restored before any brighter hardware write; a failed restore never raises hardware brightness; drags within the software range skip redundant DDC writes.

#### 3.19.4 Keys, steps, shortcuts

- "Brightness keys follow the pointer" (`brightnessKeysEnabled`, needs Accessibility): brightness keys change the display under the pointer (external DDC/software displays stepped by the app; the built-in keeps native handling unless the app's OSD replaces the system's).
- "Brightness key steps": Standard (1/16 = 0.0625), Half steps (1/32), Quarter steps (1/64). Option+Shift with a key = quarter step. Finer steps for presses left to the system are forwarded as the system's own quarter steps (2 for half, 1 for quarter).
- External keyboards: dedicated brightness key codes and F14/F15 (when the OS maps them to brightness) are handled the same way.
- "Use display brightness shortcuts" (off by default): default ⇧⌘- (decrease) / ⇧⌘= (increase); acts on the display under the pointer when pointer following is on, otherwise the primary display; one step = 1/16 limited by the key step setting. Shortcut conflicts with system shortcuts are reported ("macOS rejected this shortcut. Choose another one.").
- Keyboard backlight (X on Windows): slider in the panel (built-in keyboard), shortcuts ⌥⌘- / ⌥⌘= (off by default), step 1/16.

#### 3.19.5 OSD ("Show brightness when adjusting")

Shown only when the feature has a route that supports it. Centered on the display being changed: 196 × 154 pt rounded (radius 24) dark material panel, sun icon (39 pt), percentage (30 pt semibold rounded) with a small "%", and 16 segments (152 × 7 pt; filled = `ceil(b × 16)` capped 16, 0 → none; filled 70% white, empty 12%). Fade in 0.10 s, stays **1.0 s** after the last update, fade out 0.20 s; click-through, all spaces/full-screen, excluded from screen capture. Shown only for the newest write of the current rebuild generation.

#### 3.19.6 Display on/off (D)

Power button per display row: turns a display off/on without changing the saved arrangement. Refuses to turn off the last drawable (online ∧ active ∧ non-virtual) display ("At least one display must stay on."); the built-in panel cannot be enabled with the lid closed ("Open the lid to turn on the built-in display." — remembered and completed when the lid opens). Displays switched off are journaled (IDs + fingerprints) before the change and switched back on at the next launch, on wake, and when the feature stops; if all displays disappear (cable pulled) one app-disabled display (built-in preferred) is restored. A replacement monitor that inherits a display ID is never enabled by recovery. Errors: "Display switching is unavailable on this Mac.", "Could not change this display."

#### 3.19.7 UI

- Panel "Displays" section: per display: icon (laptop/display), name, "NN%" or "Off", power button; slider (disabled while a power change is pending); dimming choice button ("Dim the picture"/"Extra dimming" with check when chosen); red failure text; keyboard light row; "Show brightness when adjusting" switch (if supported); "Extra brightness" switch (if installed); "Options" disclosure: "Brightness keys follow the pointer" + caption + permission link, display brightness shortcuts. "No display found." when empty.
- Settings → Energy → Displays: same rows with a wider slider and "NN%", plus "More options": keys follow pointer, "Brightness key steps", display brightness shortcuts (with shortcut recorders), "Show brightness when adjusting", Accessibility permission row, caption "External displays are adjusted through the same protocol as their own buttons. When the connection cannot carry it, as with HDMI adapters, the slider dims the picture instead, so brightness control works either way."
- Extra brightness (X): XDR panels only; intensity slider 10–100% step 5; caption "Uses the display’s HDR headroom to go past the maximum brightness. Uses more battery and the Mac can run warm."; unsupported: "Available only on XDR displays, such as the ones on the 14 and 16 inch MacBook Pro."

### 3.20 Bluetooth on sleep

- Options: "Turn Bluetooth off when the Mac sleeps" (default off; caption "Bluetooth already off before sleep is left alone and stays off on wake.") and "Turn Bluetooth back on when the Mac wakes" (default on; "Only when Vorssaint was the one that switched it off."). Hidden with "This Mac has no Bluetooth controller." when no controller.
- On will-sleep: if Bluetooth is on → plan = power off; `restorePending = restoreOnWake`; persist `bluetoothSleepRestorePending` **before** switching off. If already off → nothing, no debt.
- On wake, on launch and on every preference sync (even with the feature off): if a restore is owed and Bluetooth is currently off → switch it on; always clear the debt (Bluetooth the user switched on meanwhile cancels it).

### 3.21 Fan control (summary — D)

Beta feature, off by default, shown as a panel section "Fan Control" and a Settings card. Modes: System (automatic), Manual (cooling level 0–100% in steps of 5 mapping linearly between each fan's min and max RPM), Curve (1–5 curves, one per temperature source — Average SoC, Hottest SoC, Average CPU, Hottest CPU, Hottest GPU — each 2–8 points with temperature 20–110 °C strictly increasing and non-decreasing levels; the requested level is the max over curves, linear interpolation rounded up to steps of 5, with 2 °C hysteresis on the way down). Writes go through a privileged launch daemon over XPC; the app sends a heartbeat every 1 s; the helper's 1 s watchdog returns fans to automatic if the heartbeat is older than 7 s, after 3 verification or temperature failures, on serious/critical thermal state, on sleep, or on disconnect. A recovery marker restores automatic control after crashes; optional "Resume after restart or sleep". Windows: no general fan API (EC/SuperIO/vendor WMI only) → defer; keep only GPU fan RPM via the GPU perf API if desired.

---

## 4. Settings

All keys are per-user preferences. "Bool" defaults of `false` that are not registered behave the same. Sanitizers replace invalid stored values (shown under Range).

### 4.1 Sampling & readings

| Key | Type | Default | Range / values | Meaning |
|---|---|---|---|---|
| `monitorIntervalSeconds` | int | 2 | 1, 2, 5 (else 2) | Base sampling interval "Update every" |
| `temperatureUnit` | string | `celsius` | celsius, fahrenheit | Display unit everywhere (incl. alert thresholds/bodies) |
| `networkSpeedUnit` | string | `bytes` | bytes, bits (only exact "bits" = bits) | Live speed unit (panel, menu bar, lists, graphs) |
| `monitorMemoryMetric` | string | `used` | used, app | Memory figure everywhere |

### 4.2 Menu bar readouts

| Key | Type | Default | Range | Meaning |
|---|---|---|---|---|
| `menuBarCPU`, `menuBarGPU`, `menuBarMemory`, `menuBarNetwork`, `menuBarBattery`, `menuBarPower` | bool | false | | Pin token |
| `menuBarCPUTemperature`, `menuBarGPUTemperature`, `menuBarBatteryTemperature`, `menuBarBatteryTime`, `menuBarDiskUsage`, `menuBarDiskActivity`, `menuBarPeripheralBattery`, `menuBarConnectedDevices`, `menuBarFanSpeed` | bool | false | | Pin token |
| `menuBarTemperature` | bool | — | legacy | Migrated once into the three temperature keys, then removed |
| `menuBarMetricOrder` | string (CSV) | default order (§3.16.1) | known tokens; missing appended; legacy "temperature" expands to 3 | Token order |
| `menuBarMetricAppearance` | string | `values` | values, bars | Usage display |
| `menuBarCombineTemperatures` | bool | true | | Combine usage + temperature (values only) |
| `menuBarSeparateMetrics` | bool | false | | One item per metric |
| `menuBarMetricSpacing` | string | `compact` | standard, compact | Spacing |
| `menuBarHideIconWithMetrics` | bool | false | | Hide glyph while metrics shown |
| `menuBarNetworkUploadFirst` | bool | false | | Upload line above download |
| `menuBarMemoryStyle` | string | `percent` | dot, percent, both | Memory block style ("Pressure dot" switch sets `both`/`percent`) |
| `menuBarDiskStyle` | string | `percent` | percent, free, used | Disk usage value |
| `menuBarUsageBarNormalColor` | `#RRGGBB` | `#64D2FF` | 6 hex digits | Bars normal color |
| `menuBarUsageBarElevatedColor` | `#RRGGBB` | `#FFD60A` | | Bars medium color |
| `menuBarUsageBarCriticalColor` | `#RRGGBB` | `#FF453A` | | Bars high color |
| `menuBarUsageBarMediumThreshold` | int % | 70 | 1–99 | Medium from |
| `menuBarUsageBarHighThreshold` | int % | 90 | > medium, ≤ 100 | High from |
| `menuBarPreset` | string | `dense` | dense only | Legacy (readable preset retired) |
| `menuBarLabelStyle` | string | `compact` | compact, classic | Legacy, no visible effect in dense |
| `menuBarIconSymbol` | string | "" | symbol name | Custom app glyph |
| `showCountdownInMenuBar` | bool | false | | Keep-awake countdown next to icon |
| `micMuteMenuBarIndicator` | bool | true | | (other feature) signal that re-shows the icon |
| `notchLowBatteryTint`, `notchLowBatteryThreshold`, `notchLowBatteryEarly`, `notchLowBatteryEarlyThreshold`, `notchLowBatteryMenuBar` | bool/int | false / 10 / false / 20 / true | 1–99 / 2–100 | Island low-battery warning reused to tint the menu bar battery block |

### 4.3 Panel visibility, items, graphs, order

| Key | Type | Default | Meaning |
|---|---|---|---|
| `monitorShowSystem`, `monitorShowNetwork`, `monitorShowDisk`, `monitorShowPower` | bool | true | Show section in panel |
| `monitorSysTemps`, `monitorSysCPU`, `monitorSysCPUCores`, `monitorSysGPU`, `monitorSysMemory`, `monitorSysUptime`, `monitorSysConnectedDevices` | bool | true | System items |
| `monitorSysBattery` | bool | true | Power → Charge row (named "Sys" historically) |
| `monitorSysAlerts` | bool | true | Alerts block (filtered out of the panel) |
| `monitorNetSpeed`, `monitorNetApps`, `monitorNetTotals`, `monitorNetAddresses`, `monitorNetTest` | bool | true | Network items |
| `monitorDiskUsage`, `monitorDiskActivity`, `monitorDiskSMART`, `monitorDiskProtection`, `monitorDiskTools` | bool | true | Disk items |
| `monitorPwrTemperature` | bool | true (migrated from `monitorSysTemps` if absent) | Battery temperature row |
| `monitorPwrSystem`, `monitorPwrAdapter`, `monitorPwrBattery`, `monitorPwrTimeRemaining`, `monitorPwrHealth` | bool | true | Power items |
| `monitorGraphCPU`, `monitorGraphGPU`, `monitorGraphMemory`, `monitorGraphNetwork`, `monitorGraphDisk`, `monitorGraphPower`, `monitorGraphBattery` | bool | true | Per-graph toggles |
| `monitorGraphScale` | bool | true | "Show graph scale" |
| `panelSystemOrder`, `panelNetworkOrder`, `panelDiskOrder`, `panelPowerOrder` | CSV | "" (= default) | Block order per section |
| `panelSectionOrder`, `panelCollapsedSections`, `panelShowKeepAwake`, `panelShowBrightness`, `panelShowFanControl` | CSV/bool | default / true | Panel-level layout (shared spec) |

### 4.4 Alerts

| Key | Type | Default | Range | Meaning |
|---|---|---|---|---|
| `monitorAlertCPU` | bool | false | | High CPU |
| `monitorAlertCPUThreshold` | int % | 90 | 50–100 step 5 | |
| `monitorAlertCPUTemperature` | bool | false | | Hot CPU |
| `monitorAlertCPUTemperatureThreshold` | int °C | 90 | 70–105 step 5 | |
| `monitorAlertBatteryTemperature` | bool | false | | Hot battery |
| `monitorAlertBatteryTemperatureThreshold` | int °C | 40 | 30–50 step 5 | |
| `monitorAlertMemory` | bool | false | | Critical pressure |
| `monitorAlertDisk` | bool | false | | Low disk |
| `monitorAlertDiskFreePercent` | int % | 10 | 5–30 step 5 | |
| `monitorAlertBattery` | bool | false | | Low battery |
| `monitorAlertBatteryPercent` | int % | 15 | 5–50 step 5 | |
| `monitorAlertCooldownMinutes` | int | 15 | 2, 5, 15, 30, 60 | Repeat same alert after |

### 4.5 Disk

| Key | Type | Default | Meaning |
|---|---|---|---|
| `diskEjectExcludedVolumes` | string array | [] | Names / volume UUIDs / mount paths excluded from "Eject all" (trimmed, case-insensitively unique) |

### 4.6 Keep awake

| Key | Type | Default | Range | Meaning |
|---|---|---|---|---|
| `defaultDurationMinutes` | int | 0 | 0, 15, 30, 60, 120, 240, 480 | Default/last preset (0 = indefinite) |
| `batteryLimitPercent` | int | 10 | 0, 5, 10, 15, 20 (else 10) | Battery protection (0 = never) |
| `keepAwakeAutoStart` | bool | false | | Start on launch |
| `keepAwakeRightClickToggle` | bool | false | | Right click toggles instead of context menu |
| `keepAwakeAllowDisplaySleep` | bool | false | | Only system-sleep assertion |
| `keepAwakeExternalDisplay`, `keepAwakeConnectedToPower`, `keepAwakeRunningApps` | bool | false | | Automation conditions |
| `keepAwakeRunningAppBundleIDs` | string array | [] | trimmed, unique | App identities for the Applications condition |
| `keepAwakeAutomationRequireAll` | bool | false | | false = Any, true = All |
| `keepAwakePauseWhenLocked` | bool | false | | Pause while locked |
| `keepAwakeSwitchUsesUntil` | bool | false | | Last pick was "Until" |
| `keepAwakeUntilTime` | double (ref-date seconds) | 0 | | Last picked end time (0 = none) |
| `keepAwakeMouseJiggleEnabled` | bool | false | | Pointer jiggle |
| `keepAwakeMouseJiggleIntervalMinutes` | int | 5 | 1, 2, 5, 10, 15 (else 5) | Jiggle interval |
| `hotkeyEnabled` | bool | true | | Global keep-awake shortcut on |
| `keepAwakeShortcut` | string | `control+option+command:40` | modifiers + virtual key | ⌃⌥⌘K |
| `keepAwakeIconTint` | string | `orange` | orange, green, blue, purple, pink, none | Active icon color |
| `keepAwakeActiveIcon` | string | `vorssaint` | vorssaint, coffee, eye, moon, light | Active icon |
| `clamshellPreferred` | bool | false | | Closed-lid mode |
| `dimScreenOnLidClose` | bool | false | | Dim built-in while lid closed (X) |

### 4.7 Brightness & Bluetooth

| Key | Type | Default | Meaning |
|---|---|---|---|
| `brightnessControlEnabled` | bool | false | "Control displays" |
| `brightnessKeysEnabled` | bool | false | Keys follow pointer |
| `brightnessOSDEnabled` | bool | false | Show OSD |
| `brightnessKeyStep` | string | `standard` | standard, half, quarter |
| `displayBrightnessShortcutsEnabled` | bool | false | Display brightness shortcuts |
| `displayBrightnessDecreaseShortcut` / `…IncreaseShortcut` | string | `shift+command:27` / `shift+command:24` | ⇧⌘- / ⇧⌘= |
| `keyboardBrightnessShortcutsEnabled` | bool | false | Keyboard backlight shortcuts (X) |
| `keyboardBrightnessDecreaseShortcut` / `…IncreaseShortcut` | string | `option+command:27` / `option+command:24` | ⌥⌘- / ⌥⌘= |
| `brightnessDDCWriteOnlyPaths` | string array (≤ 16) | [] | Cached write-only DDC connection paths |
| `brightnessDDCWriteOnlyPathsRechecked` | bool | — | One-time cache purge marker |
| `brightnessForcedSoftwarePaths` | string array (≤ 16) | [] | "Dim the picture" choices |
| `brightnessExtendedDimmingPaths` | string array (≤ 16) | [] | "Extra dimming" choices |
| `extraBrightnessEnabled` / `extraBrightnessLevel` | bool / int % | false / 100 | XDR boost (X) |
| `bluetoothSleepEnabled` | bool | false | BT off on sleep |
| `bluetoothSleepRestoreOnWake` | bool | true | Restore on wake |

### 4.8 Fan control (D)

`fanControlMode` (system), `fanControlCoolingLevel` (100), `fanControlCurves` (JSON, default one curve Hottest SoC 50 °C→0%, 70 °C→100%), `fanControlResume` (false), `fanControlResumeConfiguration` (""), `fanControlRecoveryNeeded` (false), `fanControlHelperVersion` ("").

### 4.9 Feature availability (hub)

`featureAvailable.<id>`; installed by default: `monitorCPU`, `monitorGPU`, `monitorMemory`, `monitorNetwork`, `monitorDisk`, `monitorPower`, `connectedDevices`, `keepAwake`, `brightness`, `extraBrightness`, `bluetoothSleep`. Not installed by default: `fanControl`, `killProcess`. Hub descriptions: CPU "Processor usage and temperature", GPU "Graphics usage and temperature", Memory "Memory use and pressure", Network "Network speed and usage", Disk "Disk space and activity", Power "Battery, power and charging", Keep awake "Keep the Mac awake on demand". With every monitor family off, the Monitor leaves the panel and the menu bar.

---

## 5. Data & files

### 5.1 Persisted state (besides the settings above)

| Item | Where (macOS) | Purpose | Windows proposal |
|---|---|---|---|
| `vorssDisabledSleep` | prefs | Recovery marker: system sleep override may be active | Same key: "lid action / sleep override owed", plus the **original** power-scheme values to restore |
| `vorssDimmedDisplaySavedBrightness` | prefs | Built-in brightness to restore after closed-lid dimming | X |
| `bluetoothSleepRestorePending` | prefs | Bluetooth restore owed | Same |
| `displaysSwitchedOff` (int array) + `displaysSwitchedOffFingerprints` (map id → fingerprint) | prefs | Displays the app switched off | Same if display on/off is implemented |
| `fanControlRecoveryNeeded`, resume configuration | prefs | Fan recovery | D |
| `/etc/sudoers.d/vorssaint-clamshell` | system file | Password-free `pmset disablesleep` | Not needed (power-scheme API) |
| `/var/run/vorssaint-fan-control.lock` / `.active` | system files (helper) | Fan control ownership | D |
| Histories, session totals, alert cooldown times, remembered brightness levels, gamma baselines, speed test results | memory only | — | Memory only (do **not** persist) |

### 5.2 External processes and endpoints (macOS)

- Processes: `/bin/ps -Aceo pid,rss,comm -m` (memory candidates), `/usr/bin/nettop -P -d -x -J bytes_in,bytes_out -L 1 -s 1` (per-app network, 5.5 s timeout; external-only variant with `-t external`, 1 s), `/usr/sbin/diskutil info -plist <mount>` (5 s), `/usr/sbin/system_profiler SPPowerDataType -json` (5 s, ≤ every 30 min), `/usr/sbin/system_profiler SPBluetoothDataType -json` (2 s, ≤ every 5 min), `/usr/bin/pmset -g` / `sudo -n pmset disablesleep 0|1`. All run with output caps (1–4 MB) and timeouts in a new session.
- Network: only `https://speed.cloudflare.com/__down` and `/__up` (user-triggered). No telemetry, no public IP lookup.

### 5.3 Logging

Unified log categories "display" (brightness route decisions, writes, probes) and "keep-awake" (lid dimming). Windows: ETW/EventSource or a rolling local log file with the same messages; never log personal data.

### 5.4 Permissions (macOS → Windows)

| macOS permission | Used by | Windows |
|---|---|---|
| Notifications | Alerts, keep-awake end/battery notices | Toast notifications (app identity/AUMID required) |
| Accessibility | Pointer jiggle (posting mouse events), brightness key taps, OSD replacing system | Not needed for SendInput / RegisterHotKey |
| Administrator password (once) | Closed-lid sudo rule; admin kill escalation | UAC elevation only where noted (temperature driver, ETW network, SMART, possibly power scheme writes) |
| Login Items approval | Fan control helper | D |

---

## 6. Algorithms & constants (consolidated)

### 6.1 Sampling

- Strides/targets: §3.1.3 table. `stride = max(1, ceil(T/I))`; `wake = gcd(strides)`; `alignedTick = tick rounded up to a multiple of wake`; timer tolerance 15%.
- GPU suppression window 0.9 s (min 0.1 s); deferred GPU refresh 0.9 s after panel open.
- Baseline gap limits: CPU 12.5 s, per-core 12.5 s, network 10 s, disk 15 s, per-process CPU/GPU `max(30 s, 4·I)`, per-process network 30 s.
- Hold limits: CPU/GPU/fans 3 misses; memory 4 misses & 12 s; temperatures 4 misses & `max(12, 2.2·stride_temp·I)` s.
- Disk metadata cache: 30 s foreground, 3600 s background. Battery max capacity probe: 1800 s. Peripheral: fast cache 15 s, Bluetooth 300 s, GATT timeout 5 s.
- Network per-app: sample every 5 s, lease 12 s, stop grace 4 s. Process list caches: fresh 5 s (8 s memory), stale 18 s, max 60 rows.

### 6.2 CPU / per-core / per-process CPU

```
cpu      = Δ(user+system+nice) / Δ(user+system+nice+idle)
core_i   = (Δtotal_i − Δidle_i) / Δtotal_i              (32-bit wrap-aware)
proc_pct = Δ(user+system ns) / (elapsed_s × 1e9 × logicalCPUs) × 100
rows    *= min(1, aggregate% / Σrows)                     (reconciliation), then clamp 0..100
```
Test vectors: busy share (100,100,800)→(200,200,1400) = 200/800 = 0.25; process 1.8 s CPU over 2 s on 18 CPUs = 5%.

### 6.3 GPU smoothing — §3.3 (`+0.20` cap up; `0.35/0.65` blend down).

### 6.4 Memory — §3.4 formulas; pressure mapping 1/2/4.

### 6.5 Temperature selection — §3.5 (valid 10 ≤ v < 125 for chips; core set first; family max fallback; bridge).

### 6.6 Sustained alert gate — §3.15 (12 s, distinct read timestamps, reset below threshold).

### 6.7 Network & disk rates

```
rate = (curr − prev) / elapsed   if curr ≥ prev else 0;   elapsed ≤ maxGap else baseline
totals += max(0, curr − prev)    only for samples within maxGap
```

### 6.8 Graph ceiling

```
graphCeiling(peak, step):            // step = 1024 bytes, 1000 for watts/bits
  for p in 0...5:
    for s in [1, 2, 5, 10, 20, 50, 100, 200, 500]:
      c = s × step^p
      if c ≥ peak: return c
  return peak
networkGraphCeiling(peakBytes, bits) = bits ? graphCeiling(peakBytes×8, 1000)/8 : graphCeiling(peakBytes, 1024)
```
Vectors: 1.3 MiB/s → 2 MiB/s; 600 KiB/s → 1 MiB/s; 12.4 W → 20 W; 20 W → 20 W; 2,000 B/s in bits → 2,500 B/s (label "20 Kbps"); 1,500 B/s in bytes → 2,048 ("2.0 KB/s").

### 6.9 Speed test math

`latency = min(RTT of 5 sequential 0-byte GETs)`; `Mbps = bytes × 8 / elapsed / 1e6` with elapsed from phase start to time box (5 s) or completion; chunk 90,000,000 B (download, sequential repeats), body 100,000,000 B (upload, single); timeout 20 s per request.

### 6.10 Formatting rules (locale-aware decimal separator from the user's *region*)

| Formatter | Rule | Examples |
|---|---|---|
| `bytes` (session totals) | base 1024, units B/KB/MB/GB/TB/PB; one decimal under 10, none ≥ 10; B never has decimals; a value that *rounds* to ≥ 1024 is promoted | 0 → "0 B", 1536 → "1.5 KB", 10 KiB → "10 KB", 1,048,064 → "1.0 MB", 1e9 → "954 MB" |
| `bytesPerSec` | same as bytes + "/s" | 1023.6 → "1.0 KB/s" |
| `bytesPerSecCompact` (menu bar, lists) | base 1024, units B/K/M/G/T/P, no space; < 10 → one decimal (re-rounded); never wider than 5 chars | 320 KiB → "320K", 1.2 MiB → "1.2M", 1023.4 → "1023B", 9.96 KiB → "10K" |
| `bitsPerSec` | ×8, base 1000, bps/Kbps/Mbps/Gbps; promote on printed value | 1,500 B/s → "12 Kbps", 1.2 MB/s → "9.6 Mbps", 124.95 → "1.0 Kbps" |
| `bitsPerSecCompact` | ×8, base 1000, b/Kb/Mb/Gb, ≤ 5 chars | 40,000 → "320Kb", 1,245,000 → "10Mb", 1e9 → "8.0Gb" |
| `diskBytes` | base **1000** (Finder style), one decimal under 10 | 245,107,195,904 → "245 GB", 1,000,204,845,056 → "1.0 TB" |
| `diskBytesPrecise` | base 1000; TB/PB with 2 decimals | 14,878,047,232,000 → "14.88 TB" |
| memory byte formatter | system "memory" style (binary, "GB") | "12.3 GB" |
| `watts` / `wattsCompact` | "%.1f W" under 10, "%.0f W" otherwise / "%.0fW" | 8.5 → "8.5 W", 23.4 → "23 W", 8.6 → "9W" |
| `percent` | clamp 0…1, round to integer | 0.125 → "13%", 1.4 → "100%" |
| temperature | "%.0f °C"/"%.0f °F"; compact "%.0f°" | 41 °C in °F → "106 °F", 49.6 → "50°" |
| battery time | `max(1, floor(s/60))` minutes → "Hh Mm" | 13,320 s → "3h 42m", 30 s → "0h 1m" |
| uptime | §3.12 | |
| keep-awake remaining | "H h MM min" / "M min SS s" / "S s" | "1 h 05 min" |
| chip countdown | "H:MM:SS" / "M:SS" | "58:12" |
| menu bar countdown | "H:MM" / "N min" / "∞" | |

### 6.11 Battery

- `batteryWatts = V_mV/1000 × I_mA/1000` (sign of current); `systemWatts` fallback `−batteryWatts` only when discharging and not on external power.
- Time remaining valid 1 ≤ min < 10,080 and not external/charging (65,535 "unavailable" sentinel rejected).
- Health = OS maximum capacity, else `min(100, fullCharge/design × 100)`.
- Menu bar glyph levels: ≥ 85 → full, 60–84 → 75, 35–59 → 50, 10–34 → 25, < 10 → empty; bolt when charging or external.

### 6.12 Keep awake

Durations {15, 30, 60, 120, 240, 480, 0}; battery check 30 s (tol 5 s); jiggle 1 px, return after 80 ms if within 2 px, safe inset 2 px, intervals {1, 2, 5, 10, 15} min; automation debounce 0.35 s (displays), 0.1 s (power, apps); lid-sleep retry 10 × 0.5 s; lid-dimming restore 6 × 0.5 s; countdown timer 30 s.

### 6.13 Brightness

- Key step 1/16; half 1/32; quarter 1/64; trust window 3 s; screen-change settle 0.5 s; wake settle 3 s; OSD 0.10/1.0/0.20 s; 16 OSD segments; reconnection dim floor 0.25; extended dimming range 0.25; write-only cache ≤ 16 entries.
- DDC/CI (reference; Windows' Monitor Configuration API does the framing): VCP luminance code **0x10**; I²C address 0x37 (0x6E write), host sub-address 0x51. Set VCP payload `[0x10, hi, lo]` framed `[0x80|(n+1), n, payload…, checksum]` (n = 3 doubles as the Set-VCP opcode); Get VCP request payload `[0x10]` framed `[0x82, 0x01, 0x10, checksum]`; checksum = XOR of all bytes seeded with 0x6E (⊕ 0x51 for multi-byte payloads — a quirk of the macOS I²C API). Reply: 11 bytes; checksum XOR of bytes 0…9 seeded 0x50 must equal byte 10; max = bytes[6..7], current = bytes[8..9] (big endian). Pacing: write pause 10 ms ×2 cycles, read pause 50 ms, retry pause 20 ms, 4 retries, ≥ 50 ms between commands.
- Software dim: `gamma'(x) = gamma(x) × clamp(value, 0, 1)`.

---

## 7. macOS dependencies → Windows mapping

Legend for "Gap": ✅ good equivalent · ⚠️ partial/heuristic/needs care · ⛔ no supported equivalent (needs driver, admin or drop).

| Capability | macOS mechanism | Windows mechanism | Gap / notes |
|---|---|---|---|
| CPU total | `host_statistics(HOST_CPU_LOAD_INFO)` ticks | `GetSystemTimes` (kernel time includes idle: `busy = (kernel − idle) + user`); optionally PDH `\Processor Information(_Total)\% Processor Utility` to match Task Manager | ✅ Note Task Manager uses "% Processor Utility" (frequency-scaled) — numbers may differ from GetSystemTimes; pick one and document |
| CPU per core | `host_processor_info(PROCESSOR_CPU_LOAD_INFO)` | `NtQuerySystemInformation(SystemProcessorPerformanceInformation)` (per processor idle/kernel/user; per processor group for > 64 LPs) or PDH `\Processor Information(*)\% Processor Time` | ✅ |
| Core classes | `hw.perflevel*` + device tree | `GetLogicalProcessorInformationEx(RelationProcessorCore)` → `EfficiencyClass` (higher = faster); map highest class → "Performance", lowest → "Efficiency"; all equal → one "CPU" group | ✅ |
| GPU usage | IOAccelerator `PerformanceStatistics["Device Utilization %"]` | PDH `\GPU Engine(*)\Utilization Percentage` (instances `pid_<pid>_luid_<luid>_phys_0_eng_<n>_engtype_<type>`): per adapter, sum per engine type, take the max across types (Task Manager semantics); or `D3DKMTQueryStatistics` running time per node | ⚠️ Expensive (hundreds of instances) — keep the background stride (10 s), reuse one PDH query, choose adapter (discrete vs integrated) or show max |
| GPU per process | IOAccelerator user clients `accumulatedGPUTime` | Same PDH counter grouped by `pid_` (already a utilization %) | ✅ (no reconciliation needed but keep it) |
| GPU temperature (+ GPU fan) | SMC `Tg*` | `D3DKMTQueryAdapterInfo(KMTQAITYPE_ADAPTERPERFDATA)` → `D3DKMT_ADAPTER_PERFDATA.Temperature` (documented as tenths of °C — verify on target drivers) and `FanRPM` on WDDM 2.4+ drivers (what Task Manager shows); vendor SDKs (NVML, AMD ADLX, Intel IGCL) as fallback | ⚠️ Driver-dependent; usually discrete GPUs only |
| CPU temperature | SMC `Tp*/Te*/Tf*` | No supported user-mode API. Options: LibreHardwareMonitorLib (MSR/SuperIO/EC via kernel driver — historically WinRing0, which Microsoft Defender now flags/blocks as a vulnerable driver; newer builds use PawnIO), requires **admin** and a signed driver; WMI `MSAcpi_ThermalZoneTemperature` (admin, ACPI zone, often static/not CPU) | ⛔ Biggest gap — make optional (separate elevated helper service) or defer |
| Battery temperature | SMC `TB?T` | `IOCTL_BATTERY_QUERY_INFORMATION` level `BatteryTemperature` (tenths of Kelvin) | ⚠️ Rarely implemented by ACPI batteries → usually unavailable |
| Fan RPM | SMC `FNum`, `F{i}Ac` | LibreHardwareMonitor (SuperIO/EC) or vendor WMI; GPU fan via ADAPTERPERFDATA | ⛔ Defer with fan control |
| Memory total/used | `host_statistics64` VM pages | `GlobalMemoryStatusEx` (total, available, load); `GetPerformanceInfo` (commit, cache, kernel); "Memory Used" ≈ Total − Available (Task Manager "In use") | ✅ |
| App memory | internal − purgeable pages | No equivalent. Options: drop the option, or ≈ Σ private working sets (`SYSTEM_PROCESS_INFORMATION.WorkingSetPrivateSize`), or In-use − kernel pools | ⚠️ |
| Compressed | compressor pages | Working set of the "Memory Compression" process (from `NtQuerySystemInformation(SystemProcessInformation)`), or undocumented store info | ⚠️ |
| Cached files | external pages | PDH `\Memory\Standby Cache *` (Reserve + Normal Priority + Core) or `\Memory\Cache Bytes`; `GetPerformanceInfo.SystemCache` | ✅ |
| Swap used | `vm.swapusage` | `Win32_PageFileUsage.CurrentUsage` (MB) or PDH `\Paging File(_Total)\% Usage` × size, or `NtQuerySystemInformation(SystemPageFileInformation)` | ✅ |
| Memory pressure | `kern.memorystatus_vm_pressure_level` | No equivalent. Heuristic: `CreateMemoryResourceNotification(LowMemoryResourceNotification)` signaled → critical; else commit ratio (CommitTotal/CommitLimit) and available-memory % thresholds → warning (define e.g. ≥ 85% commit or < 10% available) | ⚠️ Define and document thresholds; alert "Critical memory pressure" depends on it |
| Network counters | `sysctl NET_RT_IFLIST2` 64-bit `if_data64` | `GetIfTable2` → `MIB_IF_ROW2.InOctets/OutOctets` (64-bit); include rows with `InterfaceAndOperStatusFlags.HardwareInterface && !FilterInterface`, `OperStatus == Up`, types Ethernet/802.11/WWAN; exclude loopback, tunnels, virtual switches/VPN adapters | ⚠️ Must filter LightWeight-Filter/QoS child interfaces or traffic is double counted; `NotifyIpInterfaceChange` for changes |
| Local IPv4 | `getifaddrs` + SystemConfiguration names | `GetAdaptersAddresses(AF_INET)` → unicast addresses + `FriendlyName` ("Wi-Fi", "Ethernet") | ✅ |
| Per-app network | `nettop` CSV | ETW (Microsoft-Windows-Kernel-Network / TCPIP send/recv events with PID & size) — real-time sessions need **admin** (or Performance Log Users); alternative `GetPerTcpConnectionEStats` (admin to enable, TCP only) | ⛔ Defer or elevated helper; keep UI hidden when unavailable |
| Speed test | URLSession | `HttpClient` (HTTP/1.1 or 2), sequential requests, progress via stream reads / upload progress content | ✅ Upload "bytes sent" may overstate on slow links (buffered) — same as macOS |
| Volumes | `FileManager.mountedVolumeURLs` + `statfs` + `diskutil` | `GetLogicalDriveStrings`/`FindFirstVolume`, `GetVolumeInformation` (label, FS name, serial), `GetDiskFreeSpaceEx`, `GetDriveType`, `IOCTL_STORAGE_QUERY_PROPERTY(StorageDeviceProperty)` (`BusType`, `RemovableMedia`) for internal/external; volume GUID path = stable ID | ✅ Purgeable ⛔ (drop) |
| Disk I/O | IOBlockStorageDriver / IOMedia `Statistics` | `IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS` (volume → physical disk) + `IOCTL_DISK_PERFORMANCE` (cumulative BytesRead/BytesWritten per disk, works on a zero-access handle) or PDH `\PhysicalDisk(*)\Disk Read/Write Bytes/sec` | ✅ Integrate PDH rates for totals if using PDH |
| SMART / NVMe health | `diskutil` SMART vendor keys | `IOCTL_STORAGE_QUERY_PROPERTY` with `StorageDeviceProtocolSpecificProperty` / NVMe Health log (0x02): CompositeTemperature (K), PercentageUsed, DataUnitsRead/Written (×512,000 B), PowerCycles, PowerOnHours, UnsafeShutdowns, MediaErrors; status from `MSFT_PhysicalDisk.HealthStatus`; SATA via `MSFT_StorageReliabilityCounter` | ⚠️ Some queries need **admin**; non-NVMe/USB bridges often unsupported |
| Eject | `NSWorkspace.unmountAndEjectDevice` / DiskArbitration | Lock/dismount volume (`FSCTL_LOCK_VOLUME`, `FSCTL_DISMOUNT_VOLUME`) then `CM_Request_Device_Eject` on the disk's removable parent devnode (veto type/name → failure text); `IOCTL_STORAGE_EJECT_MEDIA` for media | ✅ |
| Open in Finder / Storage settings | `NSWorkspace.open` | `ShellExecute` on the drive root / `ms-settings:storagesense` | ✅ |
| Battery charge/state | IOPS + AppleSmartBattery | `GetSystemPowerStatus` (AC line, percent, `BatteryLifeTime` s) + `Windows.Devices.Power.Battery.AggregateBattery.GetReport()` or `IOCTL_BATTERY_QUERY_STATUS` (PowerState flags, Capacity mWh, Voltage mV, Rate mW signed) | ✅ "Charging vs plugged-in-not-charging" from `BATTERY_CHARGING`/`BATTERY_POWER_ON_LINE` flags |
| Battery flow (W) | V × I | `BATTERY_STATUS.Rate` (mW, negative = discharge) or `BatteryReport.ChargeRateInMilliwatts` | ✅ |
| Time remaining | IOPS time-to-empty | `GetSystemPowerStatus.BatteryLifeTime` / `BatteryEstimatedTime` (unknown = −1/`BATTERY_UNKNOWN_TIME` → "Calculating…") | ✅ |
| Health / cycles | system_profiler max capacity; Nominal/Design | `IOCTL_BATTERY_QUERY_INFORMATION(BatteryInformation)` → `FullChargedCapacity / DesignedCapacity`, `CycleCount` (often 0 = unknown) | ✅ No OS-smoothed "maximum capacity" |
| System power (W) on AC | SMC `PSTR` | No general API. On battery: discharge rate. Energy Meter Interface (EMI, `IOCTL_EMI_GET_MEASUREMENT`) on some hardware (e.g. RAPL package via vendor driver) | ⛔ Hide "System" on AC when unavailable |
| Adapter draw / rating | SMC `PDTR`, `AdapterDetails.Watts` | None | ⛔ Show "Plugged in" only |
| Power change events | `IOPSNotificationCreateRunLoopSource` | `RegisterPowerSettingNotification(GUID_ACDC_POWER_SOURCE, GUID_BATTERY_PERCENTAGE_REMAINING)` / `WM_POWERBROADCAST PBT_APMPOWERSTATUSCHANGE` | ✅ |
| Peripheral batteries | HID properties, system_profiler BT, CoreBluetooth GATT 0x180F | BLE: `Windows.Devices.Bluetooth` + `GattServiceUuids.Battery`/`BatteryLevel` for paired connected devices; Classic/HFP: device property `{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2` (undocumented "Bluetooth battery level"); HID: usage page 0x06 usage 0x20 (Battery Strength) | ⚠️ Vendor protocols (e.g. Logitech) unsupported |
| USB devices | IOUSBHostDevice registry | `SetupDiGetClassDevs(GUID_DEVINTERFACE_USB_DEVICE)` / CfgMgr32; VID/PID from hardware IDs, `DEVPKEY_Device_BusReportedDeviceDesc`, `DEVPKEY_Device_Manufacturer`; exclude root hubs, hubs (class 0x09), billboard (0x11), composite-interface children (`&MI_xx`), internal devices (`DEVPKEY_Device_RemovalPolicy` = expect-no-removal); `CM_Register_Notification` for changes | ✅ |
| Uptime | wall clock − `kern.boottime` (includes sleep) | `GetTickCount64` (biased: includes sleep/hibernation, like macOS) or now − `Win32_OperatingSystem.LastBootUpTime` | ✅ Fast Startup resumes from hibernation, so "uptime" can span user-visible shutdowns |
| Monotonic "read at" clock (alerts, gaps) | `ProcessInfo.systemUptime` (stops during sleep) | `QueryUnbiasedInterruptTime` (excludes sleep) | ✅ |
| Process CPU / memory | `proc_pid_rusage` (CPU ns, phys footprint), `ps` | `NtQuerySystemInformation(SystemProcessInformation)` (one call: per-process Kernel+User time, `WorkingSetPrivateSize`, PID, parent PID, image name) | ✅ Use private working set (Task Manager "Memory") |
| App grouping | `responsibility_get_pid_responsible_for_pid` | No equivalent. Group by: packaged app (AUMID/package family), else executable path/name (chrome.exe children → one row), else parent chain up to the process owning a visible top-level window | ⚠️ Document the heuristic |
| App names / icons | NSRunningApplication | `QueryFullProcessImageName` + file version `FileDescription`; `SHGetFileInfo`/`ExtractIconEx` | ✅ |
| Activate app | `NSRunningApplication.activate` | Find main top-level window of the process → `SetForegroundWindow` (with `AllowSetForegroundWindow`/foreground lock workarounds) | ⚠️ |
| Force kill | `kill(SIGKILL)` / forceTerminate + admin prompt | `OpenProcess(PROCESS_TERMINATE)` + `TerminateProcess`; identity via `GetProcessTimes` creation time; access denied → elevated helper / `runas` | ✅ Protected: Idle(0), System(4), smss, csrss, wininit, winlogon, services, lsass, dwm, own process |
| Notifications | UserNotifications | Toast notifications (Windows App SDK `AppNotificationManager` or shortcut with AUMID) | ✅ Check "notifications disabled" via `ToastNotifier.Setting` |
| Keep awake assertions | `IOPMAssertionCreateWithName` (system / display) | `PowerCreateRequest` + `PowerSetRequest(PowerRequestSystemRequired)` and `PowerRequestDisplayRequired` (unless display may sleep); `PowerClearRequest` on release. (Alternative `SetThreadExecutionState(ES_CONTINUOUS\|ES_SYSTEM_REQUIRED[\|ES_DISPLAY_REQUIRED])` on a persistent thread) | ✅ Reason string visible in `powercfg /requests` |
| Closed-lid mode | `pmset disablesleep` via sudoers | Power scheme lid action: `PowerGetActiveScheme` + `PowerRead/WriteACValueIndex` and `…DCValueIndex` for `GUID_SYSTEM_BUTTON_SUBGROUP` (4f971e89-eebd-4455-a8de-9e59040e7347) / `GUID_LIDCLOSE_ACTION` (5ca83367-6e45-459f-a27b-476b1d01c936): 0 Do nothing, 1 Sleep, 2 Hibernate, 3 Shut down; `PowerSetActiveScheme` to apply. Journal original values first; restore on end/quit/next launch | ⚠️ May need elevation under policy; Modern Standby differences; GP-managed machines may forbid |
| Sleep after restore if lid closed | `IOPMSleepSystem` | `GUID_LIDSWITCH_STATE_CHANGE` power-setting notification gives lid state; `SetSuspendState(FALSE, FALSE, FALSE)` (or `PowerSetRequest` release + let OS act) | ⚠️ |
| Lock/unlock | distributed notifications `com.apple.screenIsLocked/Unlocked` | `WTSRegisterSessionNotification` → `WM_WTSSESSION_CHANGE` (`WTS_SESSION_LOCK`/`UNLOCK`) | ✅ |
| External display condition | `CGGetOnlineDisplayList` + `CGDisplayIsBuiltin` | `QueryDisplayConfig` → output technology `DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL` vs others; `WM_DISPLAYCHANGE` | ✅ |
| Apps running condition | NSWorkspace running apps KVO (bundle IDs) | Executable path/name or AUMID list; poll process list (e.g. every 2–3 s) or WMI `__InstanceCreationEvent WITHIN 2` on `Win32_Process` | ✅ Store exe identity instead of bundle IDs |
| Pointer jiggle | CGEvent mouse moved | `SendInput(MOUSEEVENTF_MOVE)` ±1 px and back | ✅ No permission needed |
| Global hotkeys | Carbon hot keys | `RegisterHotKey` (avoid Win-key combos; propose Ctrl+Alt+Shift+K) | ✅ |
| Built-in brightness | DisplayServices (private) | WMI `root\wmi`: `WmiMonitorBrightness` (read) / `WmiMonitorBrightnessMethods.WmiSetBrightness(timeout, 0–100)`; `WmiMonitorBrightnessEvent` for external changes (OSD) | ✅ Integer 0–100 granularity |
| External DDC/CI | IOAVService I²C (private) | Monitor Configuration API (`dxva2`): `GetPhysicalMonitorsFromHMONITOR`, `GetMonitorBrightness/SetMonitorBrightness` or `GetVCPFeatureAndVCPFeatureReply/SetVCPFeature(0x10)`, `DestroyPhysicalMonitors` | ✅ Calls are slow (~40–100 ms) — keep the serial worker, coalescing and ≥ 50 ms pacing; classify live/write-only/dead by call results |
| Software dimming | `CGSetDisplayTransferByTable` gamma | `SetDeviceGammaRamp` on the display DC — Windows rejects ramps far from identity (dimming floor) unless `HKLM\…\ICM\GdiIcmGammaRange = 256` (admin); ineffective in HDR mode. Robust alternative: per-monitor click-through topmost black overlay with alpha (or Magnification API color effect) | ⚠️ |
| Display on/off | private `CGSConfigureDisplayEnabled` | `SetDisplayConfig`/`ChangeDisplaySettingsEx` detach (changes topology), or DDC VCP 0xD6 power mode (monitor standby) | ⛔ Defer |
| Brightness keys / pointer following | event taps (system-defined events) | Laptop brightness keys are handled by firmware/OS for the internal panel; not interceptable reliably. Offer hotkeys acting on the monitor under the cursor instead | ⛔ Drop key interception |
| OSD | NSPanel | Layered, topmost, click-through, no-activate window per monitor (`WS_EX_LAYERED|WS_EX_TRANSPARENT|WS_EX_TOOLWINDOW|WS_EX_NOACTIVATE`); skip for internal panel changes made via keys (Windows shows its own flyout) | ✅ |
| Keyboard backlight | CoreBrightness (private) | Vendor-specific only | ⛔ Drop |
| Extra brightness (XDR) | Metal EDR overlay | — | ⛔ Drop |
| Bluetooth power | `IOBluetoothPreference*ControllerPowerState` (private) | `Windows.Devices.Radios`: `Radio.RequestAccessAsync`, `Radio.GetRadiosAsync` → `Kind == Bluetooth` → `SetStateAsync(RadioState.Off/On)` | ✅ |
| Sleep/wake events | NSWorkspace will-sleep / did-wake | `PowerRegisterSuspendResumeNotification` / `WM_POWERBROADCAST` (`PBT_APMSUSPEND`, `PBT_APMRESUMEAUTOMATIC`/`RESUMESUSPEND`) | ⚠️ Suspend handlers must be fast (~2 s); Modern Standby delivers suspend differently — test both S3 and S0ix |
| Menu bar items | NSStatusItem attributed titles | `Shell_NotifyIcon` tray icons (16 px, tooltip ≤ 127 chars); optional taskbar overlay window | ⛔ Redesign (§3.16.7) |
| Fan control | SMC writes via privileged daemon (SMAppService + XPC) | No general API | ⛔ Defer |

---

## 8. Porting notes

### 8.1 Drop / defer list

- **Drop (Mac-only):** Extra brightness (XDR), closed-lid display dimming, keyboard backlight control, brightness-key interception / pointer-following keys / system quarter-step forwarding, the inbound-counter-frozen network fallback, purgeable disk space, "Open Activity Monitor" (replace with "Open Task Manager"), menu bar allowance/placement recovery logic (macOS 26 specifics), the island consumers.
- **Defer:** fan control and fan telemetry, display on/off, per-app network (unless an elevated helper exists), CPU/battery temperatures (unless an optional elevated sensor component is accepted), system power on AC and adapter metrics.
- **Redesign:** menu bar readouts → tray icons/tooltip/overlay; closed-lid mode → lid-close power setting; "App Memory" and memory pressure → defined heuristics.

### 8.2 Risks (detail)

1. **Hardware sensors require kernel access.** CPU package/core temperatures, battery temperature and fan RPM have no supported Windows API; LibreHardwareMonitor-style access needs admin plus a signed kernel driver that security products may flag. Ship as an optional, separately elevated component or omit; the CPU-temperature alert depends on it.
2. **Readout surface mismatch.** Windows has no menu-bar-like text area; tray icons are 16 px squares hidden in overflow by default on Windows 11 (users must pin them), tooltips are short, and deskbands are gone. Overlaying the taskbar is fragile across Windows builds, DPI, multi-monitor and auto-hide.
3. **Per-app attribution.** Network per process needs ETW (admin); there is no "responsible process" concept, so grouping helpers into apps is heuristic; GPU per-process via PDH is accurate but costly; energy impact is a heuristic on both platforms.
4. **Display control fidelity.** DDC/CI support varies by monitor/dock/adapter (same as macOS) but the API is slow; gamma-based software dimming is clamped by Windows and inert under HDR (overlay dimming recommended); internal brightness is 0–100 integer via WMI; display power toggling changes topology.
5. **Power semantics.** Keep-awake via power requests is solid, but closed-lid mode means editing the active power scheme (policy/elevation, crash-safe restore, Modern Standby behavior), sleep/resume notifications differ between S3 and S0ix, and whole-system power draw on AC is unavailable on most PCs.
6. **Memory model differences.** No pressure level; "Memory Used" vs "In use" semantics differ; compressed/app memory need approximations — alerts and the traffic light must use documented thresholds.
7. **Notifications identity.** Toasts require an app identity (packaged or AUMID shortcut); unpackaged builds silently drop toasts otherwise.

### 8.3 Suggested implementation order

1. **Sampler core** (plan, strides/gcd cadence, coalescing, hold rules, snapshot, history rings) + pure formatting module (§6.10) — port the unit tests first (§8.4).
2. CPU total/per-core, memory, network counters + session totals + local IP, disk volumes + I/O, battery/power (charge, flow, time, health, cycles), uptime.
3. Tray main icon + flyout with System / Network / Disks / Power sections, sparklines, edit mode/order.
4. Alerts + toasts (CPU, memory heuristic, disk, battery), cooldown, sustained gate.
5. Keep awake core (sessions, presets, until, extend, assertions, battery protection, notifications, hotkey, auto start, context menu, countdown/tooltip/icon tint).
6. Per-app CPU/memory lists (+ activate, force kill if that feature is ported), then GPU usage/per-app, energy.
7. Tray readouts (per-metric icons, bars mode, tooltip summary), detail flyouts.
8. Keep-awake automation, pause when locked, jiggle; Bluetooth on sleep.
9. Brightness: WMI internal + DDC/CI external, OSD, shortcuts; then software dimming (overlay), forced software/extended dimming.
10. Speed test, USB devices, SMART, eject + exclusions, peripheral batteries.
11. Optional elevated components: temperatures, per-app network, closed-lid mode (if elevation needed), fan control.

### 8.4 Behavior contracts to port as tests (from the Swift test suites)

- Formatting vectors in §6.10 and §6.8 (MetricsFeatureTests), including locale decimal comma and the "compact rate never wider than 5 chars" sweep.
- Sustained gate scenarios: single spike no alert; 99 at t=100/108/112 → alert at 112; same `readAt` repeated 60× never alerts; dropping below restarts the window; nil reading/nil time never alerts.
- CPU reader: first read nil; regular read 0.25; read after a 60 s gap returns nil and clears the held value and time; next read resumes.
- Cadence: tick alignment, gcd wake ticks, background strides (disk < 15 s), watts pinned honor the base interval, battery charge alone stays slow.
- Network sampler: failures don't replace the previous sample (short-gap averaging), long gaps baseline, counter reset → 0 and totals preserved.
- History: order, capacity drop, never exceeds capacity, hidden graphs publish empty arrays but keep the ring.
- Speed test: latency non-2xx/non-HTTP fails with nothing scheduled; download 404 and post-data 503 fail without counting error bodies; upload 503 fails after download; success completes; stale time-box callbacks change nothing; requests stop at the failed phase.
- Peripheral merge rules (lowest AirPods component, profiler beats GATT duplicate, one row per identity, invalid percent rejected, menu "MOU 24%+1").
- USB exclusions (root hub, hub class 9, billboard 17, built-in, non-removable) and stable IDs.
- Battery time: −1, 65,535, external, charging → nil; formatting "3h 42m", "1h 0m", "0h 1m".
- Keep-awake: last-pick rules (until vs preset, passed end time → saved duration), timer handoff only when automation would start (All vs Any), battery protection outranks handoff, manual stop suppresses automation; closed-lid restore/recovery ordering and bounded sleep retries.
- Brightness: step from monitor-read level after pause, eased vs direct writes, extended-dimming split, software-dimming choice lifecycle, display restoration journal (never enable a replacement monitor, never disable the last display).
