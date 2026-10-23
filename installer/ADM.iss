[Setup]
AppId={{A1F74D1A-6F2B-4E54-9A4C-5B0A0D3C8E11}
AppName=ADM All Download Manager
AppVersion={#AppVersion}
AppVerName=ADM All Download Manager {#AppVersion}
AppPublisher=ADM
DefaultDirName={autopf}\ADM
DefaultGroupName=ADM
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=..\artifacts\installer
OutputBaseFilename=ADM-Setup-{#AppVersion}
SetupIconFile=..\src\ADM.App\app.ico
UninstallDisplayIcon={app}\app.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
MinVersion=10.0

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional icons:"
Name: "startup"; Description: "Start ADM when Windows starts"; GroupDescription: "Startup:"; Flags: unchecked

[Files]
Source: "..\artifacts\publish\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\src\ADM.App\app.ico"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\artifacts\publish\native\*"; DestDir: "{app}\native"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\extension\*"; DestDir: "{app}\extension"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\extension\README.md"; DestDir: "{app}\extension"; Flags: ignoreversion

[Icons]
Name: "{group}\ADM All Download Manager"; Filename: "{app}\ADM.exe"; IconFilename: "{app}\app.ico"; IconIndex: 0
Name: "{group}\Browser extension setup"; Filename: "{app}\extension\README.md"
Name: "{autodesktop}\ADM All Download Manager"; Filename: "{app}\ADM.exe"; IconFilename: "{app}\app.ico"; IconIndex: 0; Tasks: desktopicon

[Registry]
; Native messaging hosts, per user, for the Chromium family.
Root: HKCU; Subkey: "Software\Google\Chrome\NativeMessagingHosts\com.adm.downloadmanager"; ValueType: string; ValueName: ""; ValueData: "{app}\native\com.adm.downloadmanager.json"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Microsoft\Edge\NativeMessagingHosts\com.adm.downloadmanager"; ValueType: string; ValueName: ""; ValueData: "{app}\native\com.adm.downloadmanager.json"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\BraveSoftware\Brave-Browser\NativeMessagingHosts\com.adm.downloadmanager"; ValueType: string; ValueName: ""; ValueData: "{app}\native\com.adm.downloadmanager.json"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Chromium\NativeMessagingHosts\com.adm.downloadmanager"; ValueType: string; ValueName: ""; ValueData: "{app}\native\com.adm.downloadmanager.json"; Flags: uninsdeletekey
; Firefox.
Root: HKCU; Subkey: "Software\Mozilla\NativeMessagingHosts\com.adm.downloadmanager"; ValueType: string; ValueName: ""; ValueData: "{app}\native\com.adm.downloadmanager.firefox.json"; Flags: uninsdeletekey
; Optional autostart.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "ADM"; ValueData: """{app}\ADM.exe"""; Tasks: startup; Flags: uninsdeletevalue

[Run]
Filename: "{app}\ADM.exe"; Description: "Launch ADM All Download Manager"; Flags: nowait postinstall skipifsilent
Filename: "{app}\extension\README.md"; Description: "Open browser extension instructions"; Flags: shellexec nowait postinstall skipifsilent unchecked

[UninstallDelete]
Type: files; Name: "{app}\native\com.adm.downloadmanager.json"
Type: files; Name: "{app}\native\com.adm.downloadmanager.firefox.json"

[Code]
procedure StopADMProcesses;
var
  ResultCode: Integer;
begin
  // ADM hides in the notification area when its window is closed, so it may
  // still be running when the uninstall wizard starts. Stop the full process
  // tree (including yt-dlp) before deleting installed files.
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /T /IM ADM.exe', '', SW_HIDE,
    ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /T /IM ADM.NativeHost.exe', '', SW_HIDE,
    ewWaitUntilTerminated, ResultCode);
end;

procedure RemoveADMRegistryEntries;
begin
  RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Google\Chrome\NativeMessagingHosts\com.adm.downloadmanager');
  RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Microsoft\Edge\NativeMessagingHosts\com.adm.downloadmanager');
  RegDeleteKeyIncludingSubkeys(HKCU, 'Software\BraveSoftware\Brave-Browser\NativeMessagingHosts\com.adm.downloadmanager');
  RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Chromium\NativeMessagingHosts\com.adm.downloadmanager');
  RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Mozilla\NativeMessagingHosts\com.adm.downloadmanager');
  RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'ADM');
end;

procedure RemoveADMUserData;
var
  TempRoot: string;
begin
  // Remove settings, download history, license/trial data, logs, cached
  // native-host manifests, app-owned temporary extraction, and untracked files
  // left in the dedicated ADM install directory (for example appkey.txt).
  DelTree(ExpandConstant('{userappdata}\ADM'), True, True, True);
  DelTree(ExpandConstant('{localappdata}\ADM'), True, True, True);
  DelTree(ExpandConstant('{app}'), True, True, True);
  TempRoot := GetEnv('TEMP');
  if TempRoot <> '' then
    DelTree(AddBackslash(TempRoot) + '.net\ADM', True, True, True);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    StopADMProcesses;
    RemoveADMRegistryEntries;
  end
  else if CurUninstallStep = usPostUninstall then
    RemoveADMUserData;
end;

// Escapes a Windows path for use in a JSON string: one backslash becomes
// two. Written with plain Pascal so it works in every Inno Setup version.
function EscapeJson(const Value: string): string;
var
  Index: Integer;
  Ch: Char;
begin
  Result := '';
  for Index := 1 to Length(Value) do
  begin
    Ch := Value[Index];
    if Ch = '\' then
      Result := Result + '\\'
    else
      Result := Result + Ch;
  end;
end;

procedure WriteNativeManifest(const FileName, AllowedKey, AllowedValue: string);
var
  HostPath: string;
  Lines: TArrayOfString;
begin
  HostPath := EscapeJson(ExpandConstant('{app}\native\ADM.NativeHost.exe'));

  SetArrayLength(Lines, 7);
  Lines[0] := '{';
  Lines[1] := '  "name": "com.adm.downloadmanager",';
  Lines[2] := '  "description": "ADM All Download Manager native messaging host",';
  Lines[3] := '  "path": "' + HostPath + '",';
  Lines[4] := '  "type": "stdio",';
  Lines[5] := '  "' + AllowedKey + '": ["' + AllowedValue + '"]';
  Lines[6] := '}';

  if not SaveStringsToUTF8File(FileName, Lines, False) then
    MsgBox('Could not write the native messaging manifest:' + #13#10 + FileName, mbError, MB_OK);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    WriteNativeManifest(ExpandConstant('{app}\native\com.adm.downloadmanager.json'),
      'allowed_origins', 'chrome-extension://nanpfakhkjjfpmibmdapaeldgodbmebh/');

    WriteNativeManifest(ExpandConstant('{app}\native\com.adm.downloadmanager.firefox.json'),
      'allowed_extensions', 'adm@all-download-manager.local');
  end;
end;
