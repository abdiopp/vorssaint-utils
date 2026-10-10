; SPDX-License-Identifier: GPL-3.0-or-later
; Per-user installer (no administrator rights). Build on Windows with Inno Setup 6:
;   iscc installer\Rivet.iss /DAppVersion=0.1.0 /DArch=x64 /DSourceDir=..\publish\win-x64
; AppName/AppId/Exe follow the product identity in Directory.Build.props.

#ifndef AppName
  #define AppName "Rivet"
#endif
#ifndef AppId
  #define AppId "Rivet"
#endif
#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef Arch
  #define Arch "x64"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish\win-" + Arch
#endif
#define AppExe AppId + ".exe"
; Inno Setup architecture identifiers: "x64compatible" (x64, also Arm64 via emulation) or "arm64".
#if Arch == "arm64"
  #define ArchId "arm64"
#else
  #define ArchId "x64compatible"
#endif
#define AppUserModelId AppId + ".Desktop"

[Setup]
AppId={#AppId}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppName} contributors
AppPublisherURL=https://github.com/abdiopp/rivet
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed={#ArchId}
ArchitecturesInstallIn64BitMode={#ArchId}
OutputDir=..\dist
OutputBaseFilename={#AppId}-{#AppVersion}-win-{#Arch}-setup
SetupIconFile=..\src\Rivet.App\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=force
RestartApplications=no
MinVersion=10.0.19041
LicenseFile=..\..\LICENSE

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "de"; MessagesFile: "compiler:Languages\German.isl"
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "fr"; MessagesFile: "compiler:Languages\French.isl"
Name: "it"; MessagesFile: "compiler:Languages\Italian.isl"
Name: "ja"; MessagesFile: "compiler:Languages\Japanese.isl"
Name: "ko"; MessagesFile: "compiler:Languages\Korean.isl"
Name: "ptbr"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "sk"; MessagesFile: "compiler:Languages\Slovak.isl"
Name: "tr"; MessagesFile: "compiler:Languages\Turkish.isl"
Name: "uk"; MessagesFile: "compiler:Languages\Ukrainian.isl"

[Tasks]
Name: "autostart"; Description: "Start {#AppName} when I sign in"; Flags: checkedonce
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; The AppUserModelID on the Start menu shortcut lets Windows deliver toasts for an unpackaged app.
Name: "{userprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; AppUserModelID: "{#AppUserModelId}"
Name: "{userdesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#AppId}"; ValueData: """{app}\{#AppExe}"" --autostart"; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
; Silent updates relaunch the app themselves.
Filename: "{app}\{#AppExe}"; Flags: nowait; Check: WizardSilent

[UninstallDelete]
; Settings and user data in %APPDATA% stay unless the user removes them; caches and logs go.
Type: filesandordirs; Name: "{localappdata}\{#AppId}\cache"
Type: filesandordirs; Name: "{localappdata}\{#AppId}\logs"
Type: filesandordirs; Name: "{localappdata}\{#AppId}\temp"

[UninstallRun]
Filename: "{cmd}"; Parameters: "/C taskkill /IM {#AppExe} /F"; Flags: runhidden; RunOnceId: "StopApp"
; The sensor helper, when the user installed it from Settings, lives under Program Files with a
; scheduled task. Removing it needs administrator rights, so Windows asks once.
Filename: "{app}\{#AppId}.SensorHelper.exe"; Parameters: "--uninstall"; Verb: "runas"; Flags: shellexec waituntilterminated; Check: SensorHelperInstalled; RunOnceId: "RemoveSensorHelper"

[Code]
function SensorHelperInstalled: Boolean;
begin
  Result := FileExists(ExpandConstant('{commonpf64}\{#AppId}\SensorHelper\{#AppId}.SensorHelper.exe'))
    and FileExists(ExpandConstant('{app}\{#AppId}.SensorHelper.exe'));
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    { Toast identity and URI scheme registered by the app at runtime. }
    RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\AppUserModelId\{#AppUserModelId}');
    RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\' + Lowercase('{#AppId}'));
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run', '{#AppId}');
  end;
end;
