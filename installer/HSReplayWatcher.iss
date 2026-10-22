; HS Replay Watcher setup. Built by installer/build.ps1, which stages the files into installer/stage first:
;   stage\bepinex\  BepInEx 5.4.23.5 + doorstop 4.5.0 x64, laid out as in the Hearthstone folder
;   stage\plugin\   the plugin DLL
;   stage\watcher\  python\ (embeddable CPython) and server\ (hsserver + tools/replay_server.py)
; BepInEx is shared with other mods (HS Friend Search's setup does the same): its files go in only where
; missing and are reference-counted (sharedfile), so uninstalling removes them only when nothing else
; counted on them. A BepInEx that was there before, with no count, keeps one count for whoever put it there.
; The test build (no admin) can't write the counts under HKLM and copies the files plainly.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifdef TestBuild
  #define BepInExFlags "onlyifdoesntexist"
#else
  #define BepInExFlags "onlyifdoesntexist sharedfile uninsnosharedfileprompt"
#endif

[Setup]
AppId={{BDB61574-2A1A-4B33-B59C-A96FEE8B03BE}
AppName=HS Replay Watcher
AppVersion={#AppVersion}
AppVerName=HS Replay Watcher {#AppVersion}
AppPublisher=RTantrumR
DefaultDirName={code:HsDir}
AppendDefaultDirName=no
DirExistsWarning=no
UsePreviousAppDir=yes
DisableProgramGroupPage=yes
DefaultGroupName=HS Replay Watcher
#ifdef TestBuild
PrivilegesRequired=lowest
#else
PrivilegesRequired=admin
#endif
UninstallFilesDir={app}\BepInEx\HSReplayWatcher\uninstall
UninstallDisplayName=HS Replay Watcher
OutputDir=out
OutputBaseFilename=HSReplayWatcher-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=no

[Messages]
SelectDirDesc=Where is Hearthstone installed?
SelectDirLabel3=The replay watcher goes into your Hearthstone folder (the one with Hearthstone.exe).
SelectDirBrowseLabel=Setup found this folder. If it is wrong, click Browse and pick the Hearthstone folder.
FinishedLabel=HS Replay Watcher is installed. Start Hearthstone from Battle.net as usual: every Battlegrounds game is recorded, and Battlegrounds > Replays plays them back.

[Files]
Source: "stage\bepinex\*"; DestDir: "{app}"; Flags: {#BepInExFlags} recursesubdirs createallsubdirs
Source: "stage\plugin\com.testhsserver.puzzlebridge.dll"; DestDir: "{app}\BepInEx\plugins"; Flags: ignoreversion
Source: "stage\watcher\*"; DestDir: "{app}\BepInEx\HSReplayWatcher"; Excludes: "__pycache__"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "licenses\*"; DestDir: "{app}\BepInEx\HSReplayWatcher\licenses"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}\BepInEx\HSReplayWatcher\licenses"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "..\NOTICE"; DestDir: "{app}\BepInEx\HSReplayWatcher\licenses"; DestName: "NOTICE.txt"; Flags: ignoreversion

[Dirs]
Name: "{localappdata}\HS Replay Watcher\Recordings"; Flags: uninsneveruninstall
Name: "{localappdata}\HS Replay Watcher\Logs"; Flags: uninsneveruninstall

[Icons]
Name: "{group}\Recordings folder"; Filename: "{localappdata}\HS Replay Watcher\Recordings"
Name: "{group}\Log replays folder"; Filename: "{localappdata}\HS Replay Watcher\Logs"
Name: "{group}\Uninstall HS Replay Watcher"; Filename: "{uninstallexe}"

[UninstallDelete]
Type: filesandordirs; Name: "{app}\BepInEx\HSReplayWatcher\server"
Type: filesandordirs; Name: "{app}\BepInEx\HSReplayWatcher\python"

[Code]
function HsDir(Param: String): String;
begin
  if not RegQueryStringValue(HKLM32, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Hearthstone',
                             'InstallLocation', Result) or (Result = '') then
    Result := ExpandConstant('{commonpf32}\Hearthstone');
end;

function HearthstoneRunning: Boolean;
var
  Locator, Wmi, Found: Variant;
begin
  Result := False;
  try
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Wmi := Locator.ConnectServer('.', 'root\CIMV2');
    Found := Wmi.ExecQuery('SELECT ProcessId FROM Win32_Process WHERE Name = ''Hearthstone.exe''');
    Result := Found.Count > 0;
  except
  end;
end;

function WaitForHearthstoneClosed: Boolean;
begin
  Result := True;
  while HearthstoneRunning do
    if MsgBox('Hearthstone is running. Close it, then click Retry.', mbError, MB_RETRYCANCEL) = IDCANCEL then
    begin
      Result := False;
      Exit;
    end;
end;

function InitializeSetup: Boolean;
begin
#ifdef TestBuild
  Result := True;
#else
  Result := WaitForHearthstoneClosed;
#endif
end;

function InitializeUninstall: Boolean;
begin
#ifdef TestBuild
  Result := True;
#else
  Result := WaitForHearthstoneClosed;
#endif
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = wpSelectDir) and not FileExists(AddBackslash(WizardDirValue) + 'Hearthstone.exe') then
  begin
    MsgBox('There is no Hearthstone.exe in this folder. Pick the Hearthstone folder.', mbError, MB_OK);
    Result := False;
  end;
end;
