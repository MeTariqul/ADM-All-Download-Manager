[Setup]
AppId={{A1F74D1A-6F2B-4E54-9A4C-5B0A0D3C8E11}
AppName=ADM All Download Manager
AppVersion=1.2.0
AppVerName=ADM All Download Manager 1.2.0
AppPublisher=ADM
DefaultDirName={autopf}\ADM
DefaultGroupName=ADM
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=..\artifacts\installer
OutputBaseFilename=ADM-Setup-1.2.0
SetupIconFile=..\src\ADM.App\app.ico
UninstallDisplayIcon={app}\ADM.exe
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
Source: "..\artifacts\publish\native\*"; DestDir: "{app}\native"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\extension\*"; DestDir: "{app}\extension"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\extension\README.md"; DestDir: "{app}\extension"; Flags: ignoreversion

[Icons]
Name: "{group}\ADM All Download Manager"; Filename: "{app}\ADM.exe"
Name: "{group}\Browser extension setup"; Filename: "{app}\extension\README.md"
Name: "{autodesktop}\ADM All Download Manager"; Filename: "{app}\ADM.exe"; Tasks: desktopicon

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
