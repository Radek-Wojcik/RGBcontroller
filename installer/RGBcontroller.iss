; RGBcontroller installer (Inno Setup 6). Build with installer\Build-Installer.ps1, which publishes the app and passes
; MyAppVersion and PublishDir. SPEC 3 "Installer": pinned OpenRGB inside {app}\openrgb, the Service, the Agent autostart
; (logon, per user, not elevated), shortcuts, a PawnIO check, consent before any other OpenRGB/AmbiBox autostart is
; switched off (never deleted), and a clean uninstall that puts everything back.

#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif
#ifndef PublishDir
  #error PublishDir must point to the output of tools\Publish-App.ps1
#endif

#define ServiceName "RGBcontroller"

[Setup]
AppId={{6F0C7A52-3B8E-4D2A-9A57-5C1B0E6E4F21}
AppName=RGBcontroller
AppVersion={#MyAppVersion}
AppPublisher=Radek Wojcik
DefaultDirName={autopf}\RGBcontroller
DefaultGroupName=RGBcontroller
DisableProgramGroupPage=yes
PrivilegesRequired=admin
; The Agent autostart goes into HKCU of the user who runs the setup (the one who will use the PC); the warning about
; per-user areas in an elevated setup is known and accepted.
UsedUserAreasWarning=no
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=Output
OutputBaseFilename=RGBcontroller-Setup-{#MyAppVersion}
SetupIconFile={#PublishDir}\rgbcontroller.ico
UninstallDisplayIcon={app}\rgbcontroller.ico
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
; The Service and the Agent are stopped by our own code (PrepareToInstall), so the restart manager is not needed.
CloseApplications=no
RestartApplications=no
LicenseFile=..\LICENSE

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion
Source: "..\tools\InstallPreflight.ps1"; DestDir: "{app}\tools"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\THIRD_PARTY_NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
; Extracted early for the checks in InitializeSetup.
Source: "..\tools\InstallPreflight.ps1"; DestDir: "{tmp}"; Flags: dontcopy

[Dirs]
Name: "{commonappdata}\RGBcontroller"

[Icons]
Name: "{autoprograms}\RGBcontroller"; Filename: "{app}\RGBcontroller.UI.exe"; WorkingDir: "{app}"; IconFilename: "{app}\rgbcontroller.ico"
Name: "{autodesktop}\RGBcontroller"; Filename: "{app}\RGBcontroller.UI.exe"; WorkingDir: "{app}"; IconFilename: "{app}\rgbcontroller.ico"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Registry]
; The tray Agent (hotkeys, screen capture) starts at logon for this user, not elevated.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "RGBcontroller.Agent"; \
    ValueData: """{app}\RGBcontroller.Agent.exe"""; Flags: uninsdeletevalue

[Run]
; explorer.exe starts the Agent in the user's normal (unelevated) context, like the logon autostart will.
Filename: "{win}\explorer.exe"; Parameters: """{app}\RGBcontroller.Agent.exe"""; Flags: nowait; Description: "Start the tray Agent"

[Code]
var
  DisableConflicts: Boolean;

function PowerShellExe: String;
begin
  Result := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
end;

function RunPreflight(const Script, Args: String; var ExitCode: Integer): Boolean;
begin
  Result := Exec(PowerShellExe, '-NoProfile -ExecutionPolicy Bypass -File "' + Script + '" ' + Args, '', SW_HIDE, ewWaitUntilTerminated, ExitCode);
end;

function InitializeSetup: Boolean;
var
  Script, Consent: String;
  Code, I: Integer;
  Lines: TArrayOfString;
  Listing: String;
  Answer: Integer;
begin
  Result := True;
  ExtractTemporaryFile('InstallPreflight.ps1');
  Script := ExpandConstant('{tmp}\InstallPreflight.ps1');
  Consent := ExpandConstant('{tmp}\consent.txt');
  if not RunPreflight(Script, '-Check -OutFile "' + Consent + '"', Code) then
  begin
    MsgBox('The pre-install checks could not run (Windows PowerShell is missing or blocked).', mbError, MB_OK);
    Result := False;
    Exit;
  end;

  if Code = 2 then
  begin
    MsgBox('The PawnIO driver is not installed. RGBcontroller needs it to reach the RAM, motherboard and GPU lighting.' + #13#10 + #13#10 +
      'Install PawnIO (https://pawnio.eu), then run this setup again.', mbError, MB_OK);
    Result := False;
    Exit;
  end;

  if Code = 3 then
  begin
    MsgBox('The Microsoft Edge WebView2 Runtime is not installed. The RGBcontroller window needs it.' + #13#10 + #13#10 +
      'Install the Evergreen runtime from https://developer.microsoft.com/microsoft-edge/webview2/ and run this setup again.', mbError, MB_OK);
    Result := False;
    Exit;
  end;

  DisableConflicts := False;
  if LoadStringsFromFile(Consent, Lines) and (GetArrayLength(Lines) > 0) then
  begin
    Listing := '';
    for I := 0 to GetArrayLength(Lines) - 1 do
      if Trim(Lines[I]) <> '' then
        Listing := Listing + '  - ' + Trim(Lines[I]) + #13#10;
    if Listing <> '' then
    begin
      if WizardSilent then
        DisableConflicts := ExpandConstant('{param:DisableConflicts|0}') = '1'
      else
      begin
        Answer := MsgBox('These programs start with Windows and would fight RGBcontroller for the same lights:' + #13#10 + #13#10 +
          Listing + #13#10 +
          'Turn their autostart off? Nothing is deleted: the entries are only disabled, and uninstalling RGBcontroller turns them back on.' + #13#10 + #13#10 +
          'Choose No to leave them as they are (close those programs yourself before using RGBcontroller).',
          mbConfirmation, MB_YESNO);
        DisableConflicts := Answer = IDYES;
      end;
    end;
  end;
end;

procedure StopRunning;
var
  Code: Integer;
begin
  Exec(ExpandConstant('{sys}\sc.exe'), 'stop {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, Code);
  // The Service ends its own OpenRGB; give it a moment, then make sure nothing of ours is left holding files.
  Sleep(3000);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM RGBcontroller.UI.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM RGBcontroller.Agent.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM RGBcontroller.Service.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopRunning;
  Result := '';
end;

function ServiceExists: Boolean;
var
  Code: Integer;
begin
  Result := Exec(ExpandConstant('{sys}\sc.exe'), 'query {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0);
end;

procedure RegisterAndStartService;
var
  Code: Integer;
  Sc, Exe: String;
begin
  Sc := ExpandConstant('{sys}\sc.exe');
  Exe := ExpandConstant('{app}\RGBcontroller.Service.exe');
  if not ServiceExists then
    Exec(Sc, 'create {#ServiceName} binPath= "' + Exe + '" start= auto obj= LocalSystem DisplayName= RGBcontroller', '', SW_HIDE, ewWaitUntilTerminated, Code)
  else
    Exec(Sc, 'config {#ServiceName} binPath= "' + Exe + '" start= auto', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Exec(Sc, 'description {#ServiceName} "RGB lighting and Adalight strip controller (supervises a private OpenRGB instance)."', '', SW_HIDE, ewWaitUntilTerminated, Code);
  // Restart after 1 s, 5 s, 30 s; the failure counter resets after a day (SPEC 4).
  Exec(Sc, 'failure {#ServiceName} reset= 86400 actions= restart/1000/restart/5000/restart/30000', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Exec(Sc, 'failureflag {#ServiceName} 1', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Exec(Sc, 'start {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

procedure SecureDataFolder;
var
  Code: Integer;
  Folder: String;
begin
  // SYSTEM and Administrators write the config; users may read config and logs (SPEC 3).
  Folder := ExpandConstant('{commonappdata}\RGBcontroller');
  Exec(ExpandConstant('{sys}\icacls.exe'), '"' + Folder + '" /inheritance:r /grant:r *S-1-5-18:(OI)(CI)F *S-1-5-32-544:(OI)(CI)F *S-1-5-32-545:(OI)(CI)RX /Q', '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Code: Integer;
begin
  if CurStep = ssPostInstall then
  begin
    SecureDataFolder;
    if DisableConflicts then
      RunPreflight(ExpandConstant('{app}\tools\InstallPreflight.ps1'), '-Disable', Code);
    RegisterAndStartService;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Code: Integer;
  Data: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    StopRunning;
    Exec(ExpandConstant('{sys}\sc.exe'), 'delete {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, Code);
    // Whatever the installer switched off comes back on.
    RunPreflight(ExpandConstant('{app}\tools\InstallPreflight.ps1'), '-Restore', Code);
  end;

  if CurUninstallStep = usPostUninstall then
  begin
    Data := ExpandConstant('{commonappdata}\RGBcontroller');
    if DirExists(Data) and (not UninstallSilent) then
      if MsgBox('Also delete the settings and logs in ' + Data + '?' + #13#10 + 'Choose No to keep them for a later reinstall.', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(Data, True, True, True);
  end;
end;
