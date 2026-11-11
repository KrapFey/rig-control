; RigControl installer (Inno Setup 6). Built by tools\build-release.ps1, which passes AppVersion and SourceDir.
; Per-user install: no admin needed to install (RigControl itself asks for admin when it starts).
#ifndef AppVersion
  #error Run tools\build-release.ps1 (it passes /DAppVersion and /DSourceDir)
#endif

[Setup]
AppId={{6F1C2B7E-4A9D-4E3B-9C5A-2D7E8B1F0A43}
AppName=RigControl
AppVersion={#AppVersion}
AppVerName=RigControl {#AppVersion}
AppPublisher=KrapFey
AppPublisherURL=https://github.com/KrapFey/rig-control
AppSupportURL=https://github.com/KrapFey/rig-control/issues
AppUpdatesURL=https://github.com/KrapFey/rig-control/releases
DefaultDirName={localappdata}\Programs\RigControl
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir={#SourceDir}\..
OutputBaseFilename=rig-control-{#AppVersion}-setup
SetupIconFile=..\src\RigControl\Assets\RigControl.ico
UninstallDisplayIcon={app}\RigControl.exe
UninstallDisplayName=RigControl
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
; RigControl runs elevated, so Restart Manager can't close it: InitializeSetup below waits for it instead.
CloseApplications=no

[Tasks]
Name: desktopicon; Description: "Create a &desktop shortcut"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\RigControl"; Filename: "{app}\RigControl.exe"
Name: "{autodesktop}\RigControl"; Filename: "{app}\RigControl.exe"; Tasks: desktopicon

[Run]
; shellexec so Windows shows RigControl's admin prompt. Not skipped in /SILENT: an in-app update starts it again.
Filename: "{app}\RigControl.exe"; Description: "Start RigControl"; Flags: nowait postinstall shellexec

[Code]
function Running(): Boolean;
begin
  Result := CheckForMutexes('RigControlRunning');
end;

{ Waits a little (an in-app update: RigControl is handing fans back and exiting), then asks the user to exit it. }
function WaitUntilClosed(): Boolean;
var
  i: Integer;
begin
  for i := 1 to 40 do
  begin
    if not Running() then break;
    Sleep(500);
  end;
  while Running() do
  begin
    if MsgBox('RigControl is running. Exit it from its tray icon (right-click, Exit), then click Retry.',
              mbError, MB_RETRYCANCEL) = IDCANCEL then
    begin
      Result := False;
      exit;
    end;
  end;
  Result := True;
end;

function InitializeSetup(): Boolean;
begin
  Result := WaitUntilClosed();
end;

function InitializeUninstall(): Boolean;
begin
  Result := WaitUntilClosed();
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  rc: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    { "Start with Windows" is a scheduled task RigControl made with admin rights; removing it needs them too. }
    if Exec(ExpandConstant('{sys}\schtasks.exe'), '/query /tn RigControl', '', SW_HIDE, ewWaitUntilTerminated, rc) and (rc = 0) then
      ShellExec('', ExpandConstant('{app}\RigControl.exe'), '--uninstall', '', SW_HIDE, ewWaitUntilTerminated, rc);
  end;
  if CurUninstallStep = usPostUninstall then
  begin
    if DirExists(ExpandConstant('{userappdata}\RigControl')) and
       (SuppressibleMsgBox('Also remove your RigControl settings (fan curves, lighting, device settings)?',
                           mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES) then
      DelTree(ExpandConstant('{userappdata}\RigControl'), True, True, True);
  end;
end;
