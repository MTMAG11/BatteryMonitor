; Inno Setup script for Battery Monitor. release.ps1 (and the release workflow) compile it for you:
;   ISCC /DMyAppVersion=1.1.0 /DArch=x64 installer\BatteryMonitor.iss
; It installs the self-contained exe, so it works on a PC with nothing else installed.
;
; Do NOT change AppId: it is how Windows tells "an upgrade of Battery Monitor" from "a different app".

#ifndef MyAppVersion
  #define MyAppVersion "1.1.0"
#endif
#ifndef Arch
  #define Arch "x64"
#endif
#define MyAppName "Battery Monitor"
#define MyAppExe  "BatteryMonitor.exe"
#define MyRepo    "https://github.com/MTMAG11/BatteryMonitor"

[Setup]
AppId={{F9478347-8BE3-4924-A9E6-1C30C7BB8B51}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher=MTMAG11 (fork of lucatze/BatteryMonitor)
AppPublisherURL={#MyRepo}
AppSupportURL={#MyRepo}/issues
AppUpdatesURL={#MyRepo}/releases
VersionInfoVersion={#MyAppVersion}

; Per-user install by default (no admin prompt); the wizard offers "install for all users" if wanted.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes

OutputDir=..\release
OutputBaseFilename=BatteryMonitor-win-{#Arch}-setup
SetupIconFile=..\BatteryMonitor\Resources\AppIcon.ico
UninstallDisplayIcon={app}\{#MyAppExe}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
MinVersion=10.0.17763
; The app is closed gracefully by the [Code] section below (it hides to the tray when asked to "close").
CloseApplications=no

#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; Flags: unchecked
Name: "startup"; Description: "Start Battery Monitor when I sign in to Windows (as the corner overlay)"; Flags: unchecked

[Files]
Source: "..\release\BatteryMonitor-win-{#Arch}-standalone.exe"; DestDir: "{app}"; DestName: "{#MyAppExe}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"; Tasks: desktopicon

[Registry]
; Only written if the "start with Windows" box is ticked; removed again on uninstall.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "BatteryMonitor"; \
  ValueData: """{app}\{#MyAppExe}"" --overlay"; Flags: uninsdeletevalue; Tasks: startup

[Run]
Filename: "{app}\{#MyAppExe}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent

[Code]
// Ask a running copy to quit cleanly (it saves what it has learned), then make sure it's gone.
procedure StopRunningApp(const ExePath: String);
var
  ResultCode: Integer;
begin
  if FileExists(ExePath) then
    Exec(ExePath, '--exit', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(1500);
  // Fallback if it didn't leave: end it.
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#MyAppExe}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopRunningApp(ExpandConstant('{app}\{#MyAppExe}'));
  Result := '';
end;

function InitializeUninstall(): Boolean;
begin
  StopRunningApp(ExpandConstant('{app}\{#MyAppExe}'));
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    if MsgBox('Also delete your Battery Monitor settings and everything it has learned about your laptop?',
              mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      DelTree(ExpandConstant('{userappdata}\BatteryMonitor'), True, True, True);
end;
