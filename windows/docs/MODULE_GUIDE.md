# Writing a feature module

A module is one feature area (screenshots, system monitor, clipboard, ...). It
lives in a set of folders named after it and plugs into the app through a few
registries. Nothing outside your folders needs to change to add one.

## 1. Folders you own

Replace `<Module>` with your module's PascalCase name and `<module>` with its
camelCase id.

| Path | What goes there |
|---|---|
| `src/Rivet.App/Features/<Module>/` | the `IFeatureModule`, views, view models, windows |
| `src/Rivet.Core/<Module>/` | platform-neutral logic, models, settings definitions, the module's platform interfaces |
| `src/Rivet.Imaging/<Module>/` | SkiaSharp rendering and image algorithms |
| `src/Rivet.Platform.Windows/<Module>/` | Win32/WinRT implementations, a `NativeMethods.txt`, an `IPlatformRegistrar` |
| `src/Rivet.Platform.Fake/<Module>/` | fakes for development and tests, an `IPlatformRegistrar` |
| `src/Rivet.Core/Resources/i18n-win/<module>.en-US.json` | new strings and Windows rewordings |
| `tests/Rivet.Core.Tests/<Module>/`, `tests/Rivet.Imaging.Tests/<Module>/`, `tests/Rivet.App.Tests/<Module>/` | tests |
| `docs/modules/<module>.md` | status, deviations from the macOS app, and a manual test checklist for Windows |

**Do not edit files outside these folders.** Shared code (Core contracts, the
shell, `Directory.Packages.props`, project files) is integrated centrally. If
you need a shared change, work around it inside your folders where possible and
describe the exact change you need in `docs/modules/<module>.md` under
"Requests for shared code".

## 2. Build and test

```bash
export DOTNET_ROOT=/opt/homebrew/opt/dotnet/libexec   # macOS with Homebrew .NET
dotnet build src/Rivet.App -f net10.0                       # dev build (fake platform)
dotnet build src/Rivet.App -f net10.0-windows10.0.22621.0   # the real Windows build
dotnet test Rivet.slnx                                      # all tests
```

Both targets must compile with no errors. The Windows target compiles on macOS,
so Win32 and WinRT code is type-checked even without a Windows PC; it cannot
run there, which is why the fake platform exists.

UI tests render real views headlessly and save PNGs to
`tests/artifacts/snapshots/`. Open them and check the layout: that is the only
way to see the UI before it runs on Windows.

## 3. The module class

```csharp
// src/Rivet.App/Features/Cleaner/CleanerModule.cs
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Modules;
using Rivet.Core.Actions;
using Rivet.Core.Features;

namespace Rivet.App.Features.Cleaner;

public sealed class CleanerModule : IFeatureModule
{
    public string Id => "cleaner";

    public void ConfigureServices(IServiceCollection services)
    {
        // Your own services. Platform implementations come from your registrars.
        services.AddSingleton<CleanerService>();
    }

    public void Initialize(ModuleContext context)
    {
        // Start/stop work with the feature's availability and enable keys.
        context.Features.RegisterController(FeatureIds.Cleaner, context.Get<CleanerService>());

        context.Actions.Register(new AppAction
        {
            Id = "cleaner.scan", FeatureId = FeatureIds.Cleaner,
            TitleKey = "Strings.cleanerName", Icon = "Sparkle",
            Run = _ => context.Get<CleanerService>().ScanAsync(),
        });

        context.Panel.AddTile(new PanelTileDescriptor
        {
            Id = "cleaner", FeatureId = FeatureIds.Cleaner, TitleKey = "Strings.cleanerName",
            Icon = "Sparkle", Order = 30, SettingsPageId = "cleaner",
            CreateHostedView = sp => new CleanerPanelView(sp),   // mini tool inside the panel
        });

        context.SettingsPages.Add(new SettingsPageDescriptor
        {
            Id = "cleaner", TitleKey = "Strings.cleanerName", Icon = "Sparkle",
            Category = SettingsCategory.AppManagement, FeatureIds = [FeatureIds.Cleaner],
            CreateView = sp => new CleanerSettingsPage(sp),
            KeywordKeys = ["Strings.cleanerScheduleTitle"], Keywords = ["temp", "cache"],
        });
    }
}
```

The class needs a public parameterless constructor. `Initialize` runs on the UI
thread once, before features sync.

### What you can contribute

| Registry (`context.…`) | Use |
|---|---|
| `Features.RegisterController(id, IFeatureController)` | `Sync(bool available)` is called at launch, on install/uninstall and when an enable key changes. Must be idempotent. Start hooks/timers when available and switched on, stop them otherwise. |
| `Actions.Register(AppAction)` | anything runnable: panel tiles, shortcuts, tray menu, Command Bar, quick panel and radial menu all invoke actions by id. Use ids like `"<module>.<verb>"`. |
| `Shortcuts.Register(ShortcutRole)` | a global hotkey; it registers only while the feature is installed and its `RequiredEnableKeys` are on. Never call `RegisterHotKey` yourself. |
| `Panel.AddTile` / `AddSection` / `AddToggle` | Utilities rows (launch an overlay tool via `ActionId`, or host a mini tool via `CreateHostedView`), whole panel tabs, Controls switches. |
| `SettingsPages.Add` | a Settings page. |
| `TrayMenu.Add` | a tray right-click menu entry (negative `Order` puts it above Settings). |
| `Search.Add(ISearchProvider)` | Command Bar results. |
| `Hud.Show(...)` | short "Copied" / "Saved" messages. |

Services available from DI (`context.Get<T>()` or constructor injection):
`ISettingsStore`, `FeatureRuntime`, `ActionRegistry`, `ShortcutManager`,
`IAppShell` (open Settings, show/close the panel, keep it open), `ITrayPresence`
(tray tint, badge, tooltip line), `IHud`, `AppPaths`, `Localizer`, and the
platform services in `Rivet.Core.Platform`: `IScreenService`, `IInputHooks`,
`IClipboardService`, `IShellService`, `INotificationService`, `IThemeService`,
`IWindowChrome`, `IPlatformInfo`, `IFocusHandoff`, `IKeyNameProvider`.

Cross-module contracts live in `Rivet.Core.Contracts`: `ICaptureSelector`,
`IRecentCaptures`, `ICaptureOutput`, `IScreenshotEditor`, `IRecordingEditor`,
`IShelfIntake`, `ISearchProvider`, `IHud`. The owning module implements one; others
resolve it with `GetService<T>()` and must cope with it being absent (the owning
feature may be uninstalled, or not integrated yet).

## 4. Settings

Define settings next to your code, with keys identical to the macOS app where
the setting exists there (the specs list every key and default):

```csharp
// src/Rivet.Core/Cleaner/CleanerSettings.cs
using Rivet.Core.Settings;

// Not Rivet.Core.Cleaner: FeatureCatalog uses `using static FeatureIds`, and a
// namespace named like a feature id (Cleaner, Uninstaller, …) would shadow it.
namespace Rivet.Core.Maintenance.Cleaner;

public static class CleanerSettings
{
    public static readonly Setting<string> ScheduleFrequency =
        new("cleanerScheduleFrequency", "off", Sanitize.OneOfStrings("off", "off", "daily", "weekly", "monthly"));
    public static readonly Setting<bool> ScheduleNotify = new("cleanerScheduleNotify", true);
    public static readonly Setting<long> LastRunUnix = new("cleanerLastRunUnix", 0L, machineState: true);
}
```

* Read with `settings.Get(CleanerSettings.ScheduleNotify)`, write with `Set`.
* Watch with `settings.Observe(callback, CleanerSettings.ScheduleFrequency, …)`.
* Bind to UI with `settings.Bind(setting)` (`SettingProperty<T>`, notifies on the UI thread).
* Mark machine-specific state with `machineState: true`; it never goes into backups.
* Store structured data as a record type (`Setting<List<MyRule>>`); it is saved as JSON.
* Sanitize every value: the file is user-editable.

## 5. Strings

* Reuse the macOS keys whenever the wording fits Windows: `L.Get("Strings.cleanerName")`.
  The specs quote the English text; search `src/Rivet.Core/Resources/i18n/en-US.json`
  for the key.
* New text, or a Windows rewording of an existing key ("menu bar" → "system tray",
  "Mac" → "PC", "Finder" → "File Explorer", "⌘" → "Ctrl", "System Settings" →
  "Windows Settings", "Trash" → "Recycle Bin"), goes into
  `src/Rivet.Core/Resources/i18n-win/<module>.en-US.json`. New keys are named
  `win.<module>.<name>`. Overriding an existing key there replaces its English text
  only; the other 14 languages keep their translation until a translation pass.
* Format strings use printf syntax: `L.Format("hub.activeCountFormat", 3, 44)`.
* Plurals: `L.Plural(count, oneKey, fewKey, manyKey)`.
* In XAML: `Text="{l:Tr screenshot.pageTitle}"` with `xmlns:l="using:Rivet.App.Localization"`.
* The product name is `AppIdentity.DisplayName`. Never write a product name in a string.
* `tests/Rivet.App.Tests/StringKeyTests` fails when a key written in source does not exist.

## 6. Platform code

The pattern: an interface in Core, a Windows implementation, a fake, and one
registrar per platform.

```csharp
// src/Rivet.Core/Cleaner/ICleanerPlatform.cs
public interface ICleanerPlatform { long RecycleBinSize(); bool EmptyRecycleBin(); }

// src/Rivet.Platform.Windows/Cleaner/WindowsCleanerPlatform.cs
public sealed class WindowsCleanerPlatform : ICleanerPlatform { /* SHQueryRecycleBin, SHEmptyRecycleBin */ }

// src/Rivet.Platform.Windows/Cleaner/CleanerRegistrar.cs
public sealed class CleanerRegistrar : IPlatformRegistrar
{
    public void Register(IServiceCollection services) =>
        services.AddSingleton<ICleanerPlatform, WindowsCleanerPlatform>();
}

// src/Rivet.Platform.Fake/Cleaner/CleanerRegistrar.cs  (same shape, registers the fake)
```

### Win32 (CsWin32)

* List the APIs, structs, enums and constants you need in
  `src/Rivet.Platform.Windows/<Module>/NativeMethods.txt` (one per line, `*`
  wildcards allowed for constants like `WM_*`). They appear on `Windows.Win32.PInvoke`
  and in `Windows.Win32.*` namespaces. Many functions have friendly overloads that
  take strings and return `SafeHandle`s; raw overloads take `PCWSTR`, `HWND` and
  pointers (`unsafe`). Pass `HMENU.Null`, not `null`, for handle structs.
* The project allows unsafe code, targets x64 and ARM64, and its minimum is
  Windows 10 2004 (19041). Guard newer APIs with
  `OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)`.
* If CsWin32's projection fights you, a small `[LibraryImport]` declaration in
  your folder is fine.

### WinRT, Direct3D, Media Foundation, audio

* WinRT APIs come with the target framework: `Windows.Graphics.Capture`,
  `Windows.Media.Ocr`, `Windows.Media.Control` (media sessions),
  `Windows.Devices.*`, `Windows.UI.ViewManagement`, `Windows.Storage`.
* Direct3D 11 / DXGI / Media Foundation: Vortice 3.8.3 (`Vortice.Direct3D11`,
  `Vortice.DXGI`, `Vortice.MediaFoundation`).
* Audio: NAudio 3.1.0 (`NAudio.Wasapi`, `NAudio.Core`): WASAPI capture and
  loopback, `MMDeviceEnumerator`, session volumes.
* Imaging: SkiaSharp 3.119 everywhere. Hand pixels around as `PixelBuffer`
  (BGRA, premultiplied) and convert with `Rivet.Imaging.Skia.SkiaConvert`.

### Rules

* **Input hooks:** use `IInputHooks` (one shared low-level keyboard and mouse hook
  for the whole app). Handlers run on the hook thread and must return in a few
  milliseconds; post real work to the UI thread. Never install your own hooks.
* **Hotkeys:** only through `ShortcutRole`s and the `ShortcutManager`.
* **Threads:** the UI thread is Avalonia's dispatcher (`Dispatcher.UIThread.Post`,
  or `UiThread.Post` from Core). Never block it; do heavy work with `Task.Run`.
* **Restore system state.** Anything that changes a system setting must undo it
  when the feature stops and when the app quits. Singletons implementing
  `IDisposable` are disposed at shutdown. Use a write-ahead marker setting so a
  crash is undone on the next launch.
* **Elevation:** the app runs as a standard user. Hooks and `SendInput` do not
  reach windows of elevated apps (UIPI). Detect and explain; do not ask for admin
  unless the spec's feature genuinely needs it.

## 7. Windows and overlays

* Use Avalonia `Window`s. For overlays, HUDs and floating tools, set
  `WindowDecorations = WindowDecorations.None`, `ShowInTaskbar = false`,
  `Topmost = true`, and after `Show()` call
  `WindowInterop.ApplyChrome(window, WindowChromeOptions.ToolWindow | …)`
  (`NoActivate`, `ClickThrough`, `ExcludeFromCapture`, `Topmost`).
* Coordinates: `Window.Position` is in **physical** pixels; `Width`/`Height` are
  DIPs. `Screen.Scaling` gives the DPI scale per monitor. `IScreenService` and
  `IInputHooks` report physical pixels. For full-screen overlays create one window
  per monitor and size each to its monitor's bounds (DIPs = pixels / scaling).
* Skia drawing inside Avalonia: derive from `Rivet.App.Controls.SkiaView` (or handle
  its `Draw` event). Use the same Skia code for the screen and for export.
* Bitmaps: `ImageInterop.ToBitmap(SKImage | PixelBuffer)`.
* Dialogs: `ConfirmDialog.ShowAsync(owner, title, message, confirm, cancel)`.
* File pickers: `TopLevel.GetTopLevel(control).StorageProvider`.

## 8. UI conventions

* Settings pages derive from `Rivet.App.Controls.SettingsPage`: use `Stack`,
  `Header`, `Card`, `Row`, `Toggle`, `Choice`, `Slider`, `ActionButton`, `Note`.
  Bindings created through `Toggle`/`Choice`/`Slider`/`Track` are disposed when the
  page leaves the window.
* Panel content is about 316 DIPs wide (panel 340, padding 12). Use the `card`
  border class, `rowTitle` and `caption` text classes, `Button.row` for rows,
  `ToggleSwitch.compact` for switches, and `sectionTitle` for uppercase headings.
* Colours come from resources (`TextPrimaryBrush`, `TextSecondaryBrush`,
  `AccentBrush`, `PanelCardBrush`, `WarningBrush`, `Metric*Brush`, …) so light and
  dark themes both work. Never hard-code a colour that depends on the theme.
* Icons: `<ic:SymbolIcon Symbol="Camera"/>` (`xmlns:ic="using:FluentIcons.Avalonia"`).
  Names are Fluent UI System Icons; code that stores names as strings converts with
  `IconConverter.Parse`. `ShellRenderTests.Every_catalog_icon_name_exists` checks
  every page and section icon.
* Every icon-only button gets a tooltip and an `AutomationProperties.Name`.
* Fixed English tokens in search keywords are fine ("PID", "winget").

## 9. Quality bar

* Port the behaviour in your spec faithfully. Where macOS and Windows differ, do
  what a Windows user expects and record the deviation in `docs/modules/<module>.md`.
* Port the spec's test vectors and constants as unit tests.
* Render your main views in a snapshot test and look at the PNGs.
* No fake success: if something cannot work on a PC (missing hardware, missing
  OS support), show a clear "not available" state and say so in the module doc.
* Keep the style of the existing code: SPDX header first, file-scoped namespaces,
  nullable enabled, small classes, comments that explain why.
* Finish with `docs/modules/<module>.md`: what is implemented, what is not, known
  risks, and a step-by-step manual test checklist for a real Windows PC.

## 10. Conventions added during integration

* **Main settings page.** When several pages list a feature in `FeatureIds`, set
  `PrimaryFor = [featureId]` on the one the Features hub and search should open.
* **Live tiles.** `PanelTileDescriptor.LiveTitle`, `LiveIcon` and `LiveCaption` are
  re-read every second while the panel shows them; call `PanelRegistry.Invalidate()`
  after a state change that should rebuild the panel at once. Use `Accessory` for
  the small secondary button under a row (e.g. "Recent captures").
* **Action visibility.** `AppAction.IsVisible` hides an action from lists (Command
  Bar, quick panel, radial menu) while it does not apply; invoking it still works.
* **Backups.** `SettingsBackup.RegisterExportSanitizer(key, value => …)` rewrites a
  value before export — strip absolute paths that only exist on this PC, or return
  null to leave the key out. Plain machine state uses `machineState: true`.
* **Backgrounds.** Store `BackdropStyle` values with
  `Rivet.Imaging.Backdrop.BackdropCodec` (macOS field names). The screenshot editor
  and the recording editor share `screenshotBackdropPresets`.
* **Bitmaps.** Avalonia 12.1.3 crops bitmaps created at a DPI other than 96:
  `ImageInterop` always makes 96-DPI bitmaps; size `Image` controls yourself
  (pixels ÷ scale). PNG files we write carry 96 × capture scale as their DPI.
* **Errors and confirmations.** Use `IHud.Show(message, HudStyle.Error)` for a failed
  action and `HudStyle.Success` for "Copied"/"Saved"; no beeps. The caller of
  `ICaptureOutput.SaveAsync` shows the "Saved" message.
