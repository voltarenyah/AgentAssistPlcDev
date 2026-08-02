#ifndef MyAppVersion
  #define MyAppVersion "0.1.0-dev.1"
#endif
#ifndef ReleaseDir
  #define ReleaseDir "..\artifacts\release\win-x64"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\installer"
#endif

#define AppName "Automation Workbench"

[Setup]
AppId={{B1D4D6BF-0C2A-4A17-BD08-9D8EF4D2A0C8}
AppName={#AppName}
AppVersion={#MyAppVersion}
AppPublisher=Automation Workbench
AppPublisherURL=https://example.invalid/automation-workbench
DefaultDirName={autopf}\Automation Workbench
DefaultGroupName=Automation Workbench
UninstallDisplayName=Automation Workbench
OutputDir={#OutputDir}
OutputBaseFilename=AutomationWorkbench-{#MyAppVersion}-win-x64-setup
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=no
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupLogging=yes
DisableProgramGroupPage=yes
Uninstallable=yes
VersionInfoDescription=Automation Workbench installer
VersionInfoProductName=Automation Workbench
VersionInfoCompany=Automation Workbench

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked
Name: "launch"; Description: "Launch Automation Workbench after setup"; GroupDescription: "After installation:"; Flags: unchecked

[Files]
Source: "{#ReleaseDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Automation Workbench"; Filename: "{app}\AutomationWorkbench.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\Automation Workbench"; Filename: "{app}\AutomationWorkbench.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\AutomationWorkbench.exe"; Description: "Launch Automation Workbench"; Flags: nowait postinstall skipifsilent; Tasks: launch

[Code]
const
  TiaWhitelistRoot = 'SOFTWARE\Siemens\Automation\Openness\17.0';

function RunHelper(const CommandLine: String; var ExitCode: Integer): Boolean;
var
  HelperPath: String;
begin
  HelperPath := ExpandConstant('{app}\tools\AutomationWorkbench.OpennessWhitelist.exe');
  if not FileExists(HelperPath) then
  begin
    ExitCode := 11;
    Result := False;
    Exit;
  end;
  Result := Exec(HelperPath, CommandLine, ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, ExitCode);
end;

procedure ShowEngineeringWarning(const Detail: String);
begin
  MsgBox(
    'Automation Workbench was installed, but engineering integration is unavailable.' + #13#10#13#10 +
    'TIA Portal V17 is required. Install TIA V17 and rerun whitelist registration through Repair.' + #13#10#13#10 + Detail,
    mbError, MB_OK);
end;

procedure RegisterWhitelist;
var
  EngineeringPath: String;
  ExitCode: Integer;
begin
  EngineeringPath := ExpandConstant('{app}\mcp\engineering\Mcp.Engineering.exe');
  if not FileExists(EngineeringPath) then
  begin
    ShowEngineeringWarning('The installed engineering executable was not found.');
    Exit;
  end;

  if not RegKeyExists(HKLM, TiaWhitelistRoot) then
  begin
    ShowEngineeringWarning('The Siemens TIA Portal V17 registry key was not found; the whitelist step was skipped.');
    Exit;
  end;

  if (not RunHelper('register --exe "' + EngineeringPath + '"', ExitCode)) or (ExitCode <> 0) then
  begin
    ShowEngineeringWarning('Whitelist registration returned exit code ' + IntToStr(ExitCode) + '.');
    Exit;
  end;

  if (not RunHelper('verify --exe "' + EngineeringPath + '"', ExitCode)) or (ExitCode <> 0) then
    ShowEngineeringWarning('Whitelist verification returned exit code ' + IntToStr(ExitCode) + '.');
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    RegisterWhitelist;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  EngineeringPath: String;
  ExitCode: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    EngineeringPath := ExpandConstant('{app}\mcp\engineering\Mcp.Engineering.exe');
    if FileExists(ExpandConstant('{app}\tools\AutomationWorkbench.OpennessWhitelist.exe')) then
      RunHelper('remove --exe "' + EngineeringPath + '"', ExitCode);
  end;
end;
