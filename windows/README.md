# Windows port

The Windows version of the utilities app, built with C# / .NET 10 and
[Avalonia](https://avaloniaui.net) 12. It ships under the working codename
**Rivet** until it gets a real name: the macOS project's trademark policy
reserves its name and icon for official builds, so this port uses its own.
Renaming is one property in `Directory.Build.props` plus
`tools/rename-product.sh` for the code namespaces.

Everything is local-first: no account, no telemetry. The network is used only
for update checks and features that obviously need it.

## Layout

```
windows/
  Directory.Build.props        product identity (name, id, update feed), common settings
  Directory.Packages.props     every NuGet version, pinned in one place
  Rivet.slnx
  src/
    Rivet.Core/                platform-neutral: settings, localization, feature catalog
                               and runtime, shortcuts, actions, cross-module contracts,
                               platform service interfaces, recording take format
    Rivet.Imaging/             SkiaSharp rendering shared by the editors (backdrops,
                               annotations, frame compositor, GIF), cross-platform
    Rivet.Platform.Windows/    Win32/WinRT/Media Foundation/WASAPI implementations
    Rivet.Platform.Fake/       stand-ins so the app runs and renders on macOS/Linux
    Rivet.App/                 the Avalonia app: tray, panel, Settings, feature modules
    Rivet.SensorHelper/        optional elevated helper that reads the CPU's temperature
                               sensors, so the app itself stays a standard-user app
  tests/                       xUnit v3: Core, Imaging, App (headless UI snapshots)
  docs/
    specs/                     functional specs of the macOS app, one per area
    modules/                   per-module status and Windows test checklists
    MODULE_GUIDE.md            how to write a feature module
  tools/i18n/                  extractor that pulls all strings from the macOS sources
```

## Build, run, test

You need the .NET 10 SDK. The Windows target also builds on macOS and Linux
(`EnableWindowsTargeting`), which catches compile errors without a Windows PC.

```bash
# Windows app (on Windows this produces Rivet.exe)
dotnet build src/Rivet.App -f net10.0-windows10.0.22621.0

# Development build with fake platform services (runs on macOS/Linux)
dotnet run --project src/Rivet.App -f net10.0 -- --settings

# All tests; UI snapshots land in tests/artifacts/snapshots/
dotnet test Rivet.slnx

# Health check used by CI
Rivet.exe --selftest
```

Publishing a self-contained build for Windows:

```bash
dotnet publish src/Rivet.App -f net10.0-windows10.0.22621.0 -r win-x64 -c Release --self-contained
```

### Installer

`installer/Rivet.iss` is a per-user [Inno Setup](https://jrsoftware.org/isinfo.php)
installer (no administrator rights; installs to `%LOCALAPPDATA%\Programs`). It
needs Windows, so CI builds it after publishing:

```bash
iscc installer\Rivet.iss /DArch=x64 /DSourceDir=..\publish\win-x64
```

The output name, `Rivet-<version>-win-<arch>-setup.exe`, is what the in-app
updater looks for in GitHub Releases of the repository set in `Directory.Build.props`.

## How it fits together

* **Features.** Every capability is a feature in `Rivet.Core/Features/FeatureCatalog.cs`.
  The Features hub installs and uninstalls them. An uninstalled feature never
  starts and disappears from every surface; its settings are kept.
* **Modules.** Each feature area is a module folder under `src/Rivet.App/Features/`
  (plus matching folders in Core, Imaging, Platform.Windows and Platform.Fake).
  Modules are discovered by reflection, so adding one never edits a shared list.
  See [docs/MODULE_GUIDE.md](docs/MODULE_GUIDE.md).
* **Settings.** JSON in `%APPDATA%\Rivet\settings.json`. Only values the user
  actually changed are stored; defaults live in code next to the feature.
* **Strings.** All 3,253 strings of the macOS app in its 15 languages are embedded
  (`Rivet.Core/Resources/i18n`), extracted with `tools/i18n`. Windows-specific
  strings live in `Resources/i18n-win/<module>.<lang>.json`.
* **Shell.** A notification-area icon opens the panel (a flyout with one tab per
  section). Settings is a normal window with a searchable sidebar.

## What is not ported

Features that exist on macOS because macOS lacks them, while Windows already has
them, are left out: the app switcher, Dock previews and clicks, window snapping and
maximize, Spaces order, quit-on-close, Finder cut/paste and rename, side buttons,
middle click, linear scrolling, pointer acceleration, focus-follows-mouse, sideways
scrolling, the disk image installer, the Music blocker and XDR extra brightness.
Fan control is deferred (it needs a vendor kernel driver on Windows).
