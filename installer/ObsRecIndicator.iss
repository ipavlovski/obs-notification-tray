; Inno Setup script - build with package.cmd (runs build.cmd first).
; Per-user install: no admin rights needed.

#define AppName "OBS Rec Indicator"
#define AppExe "ObsRecIndicator.exe"
#define AppVersion GetVersionNumbersString(AddBackslash(SourcePath) + "..\bin\" + AppExe)

[Setup]
AppId={{6F3C2A1E-8B4D-4E7A-9C51-2D0F7B6A9E34}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
PrivilegesRequired=lowest
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=ObsRecIndicator-Setup-{#AppVersion}
SetupIconFile=..\assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2
SolidCompression=yes

[Tasks]
Name: "startup"; Description: "Start automatically when I sign in to Windows"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; Flags: unchecked

[Files]
Source: "..\bin\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\config.sample.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Same value the app's "Start with Windows" checkbox writes.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "ObsRecIndicator"; ValueData: """{app}\{#AppExe}"""; Tasks: startup
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "ObsRecIndicator"; Flags: deletevalue; Tasks: not startup
; Always remove it on uninstall, even if it was turned on from the settings window.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "ObsRecIndicator"; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#AppExe}"; Description: "Start {#AppName} now"; Flags: postinstall nowait skipifsilent

[Code]
const
  AppMutex = 'Local\ObsRecIndicator.Instance';

{ Asks a running copy to exit (it checks once per poll interval) and waits for it. }
procedure QuitRunningApp();
var
  ExePath: String;
  ResultCode, Tries: Integer;
begin
  ExePath := ExpandConstant('{app}\{#AppExe}');
  if not FileExists(ExePath) or not CheckForMutexes(AppMutex) then
    exit;
  Exec(ExePath, '--quit', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Tries := 0;
  while CheckForMutexes(AppMutex) and (Tries < 100) do
  begin
    Sleep(100);
    Tries := Tries + 1;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  QuitRunningApp();
  Result := '';
end;

function InitializeUninstall(): Boolean;
begin
  QuitRunningApp();
  Result := True;
end;
