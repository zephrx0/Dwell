[Setup]
AppId=Dwell
AppName=Dwell
AppVersion={#AppVersion}
AppPublisher=zephrx0
AppPublisherURL=https://github.com/zephrx0/Dwell
DefaultDirName={localappdata}\Programs\Dwell
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
MinVersion=10.0.17763
SetupIconFile=..\src\Dwell\Assets\Dwell.ico
UninstallDisplayIcon={app}\Dwell.exe
OutputBaseFilename=Dwell-Setup
VersionInfoVersion={#AppVersion}
VersionInfoProductName=Dwell
VersionInfoDescription=Dwell Setup
WizardStyle=modern
Compression=zip
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Files]
Source: "{#AppSource}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{userprograms}\Dwell"; Filename: "{app}\Dwell.exe"; WorkingDir: "{app}"

[Run]
Filename: "{app}\Dwell.exe"; Description: "{cm:LaunchProgram,Dwell}"; Flags: nowait postinstall skipifsilent
Filename: "{app}\Dwell.exe"; Flags: nowait skipifnotsilent; Check: IsDwellUpdate

[CustomMessages]
english.DwellWaitError=Dwell is still running. Exit Dwell from the system tray and run Setup again.
russian.DwellWaitError=Приложение Dwell ещё работает. Закройте его через значок в области уведомлений и запустите установщик снова.

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';

function OpenProcessForWait(Access: Cardinal; Inherit: Boolean; ProcessId: Cardinal): THandle;
  external 'OpenProcess@kernel32.dll stdcall';
function WaitForProcessExit(ProcessHandle: THandle; Milliseconds: Cardinal): Cardinal;
  external 'WaitForSingleObject@kernel32.dll stdcall';
function CloseProcessHandle(ProcessHandle: THandle): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';

function UpdateProcessId: Integer;
begin
  Result := StrToIntDef(ExpandConstant('{param:DwellUpdatePID|0}'), 0);
end;

function IsDwellUpdate: Boolean;
begin
  Result := UpdateProcessId > 0;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ProcessHandle: THandle;
begin
  Result := '';
  if not IsDwellUpdate then exit;
  ProcessHandle := OpenProcessForWait($00100000, False, UpdateProcessId);
  if ProcessHandle = 0 then exit;
  try
    if WaitForProcessExit(ProcessHandle, 30000) <> 0 then
      Result := CustomMessage('DwellWaitError');
  finally
    CloseProcessHandle(ProcessHandle);
  end;
end;

function InitializeUninstall: Boolean;
begin
  Result := not CheckForMutexes('Local\Dwell.Uninstall');
  if not Result then
    SuppressibleMsgBox(CustomMessage('DwellWaitError'), mbError, MB_OK, IDOK);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Command: String;
begin
  if (CurUninstallStep = usPostUninstall) and RegQueryStringValue(HKCU, RunKey, 'Dwell', Command) then
    if CompareText(Command, ExpandConstant('"{app}\Dwell.exe" --background')) = 0 then
      RegDeleteValue(HKCU, RunKey, 'Dwell');
end;
