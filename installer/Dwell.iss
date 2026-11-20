[Setup]
AppId=Dwell
AppName=Dwell
AppVersion={#AppVersion}
AppPublisher=zephrx0
AppPublisherURL=https://github.com/zephrx0/Dwell
DefaultDirName={localappdata}\Programs\Dwell
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
MinVersion=10.0.17763
SetupIconFile=..\src\Dwell\Assets\Dwell.ico
UninstallDisplayIcon={app}\Dwell.exe
OutputBaseFilename=Dwell-Setup
VersionInfoVersion={#AppVersion}
WizardStyle=modern
Compression=zip
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Files]
Source: "{#AppExe}"; DestDir: "{app}"

[Icons]
Name: "{userprograms}\Dwell"; Filename: "{app}\Dwell.exe"; WorkingDir: "{app}"

[Run]
Filename: "{app}\Dwell.exe"; Description: "{cm:LaunchProgram,Dwell}"; Flags: nowait postinstall skipifsilent

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Command: String;
begin
  if (CurUninstallStep = usPostUninstall) and RegQueryStringValue(HKCU, RunKey, 'Dwell', Command) then
    if CompareText(Command, ExpandConstant('"{app}\Dwell.exe" --background')) = 0 then
      RegDeleteValue(HKCU, RunKey, 'Dwell');
end;
