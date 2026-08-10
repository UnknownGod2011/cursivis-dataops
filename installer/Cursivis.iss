#ifndef MyAppVersion
  #define MyAppVersion "0.1.0-beta.1"
#endif

#define MyAppName "Cursivis Next"
#define MyAppPublisher "Cursivis"
#define MyAppURL "https://cursiviss.vercel.app"
#define MyAppExeName "Cursivis.exe"
#define StableExtensionId "ofjpnfmkpmdohcolkooobigigdcdnkgl"

[Setup]
AppId={{4C2D043F-F0B5-4A50-A88F-508637E3DC24}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL=https://github.com/UnknownGod2011/openai-cursivis/issues
AppUpdatesURL=https://github.com/UnknownGod2011/openai-cursivis/releases/latest
DefaultDirName={localappdata}\Programs\Cursivis Next
DefaultGroupName=Cursivis Next
DisableProgramGroupPage=yes
AllowNoIcons=yes
OutputDir=..\artifacts\release
OutputBaseFilename=Cursivis-Setup-x64
SetupIconFile=..\apps\windows\Cursivis.Windows.App\Assets\AppIcon.ico
UninstallDisplayIcon={app}\Assets\AppIcon.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
CloseApplications=yes
CloseApplicationsFilter=Cursivis.exe,Cursivis.Windows.NativeHost.exe
RestartApplications=no
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked
Name: "startup"; Description: "Start Cursivis Next automatically after Windows sign-in"; GroupDescription: "Startup:"; Flags: checkedonce

[Files]
Source: "..\artifacts\publish\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\artifacts\publish\native-host\*"; DestDir: "{app}\NativeHost"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\extensions\chromium\*"; DestDir: "{app}\BrowserExtension"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Cursivis Next"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{group}\Uninstall Cursivis Next"; Filename: "{uninstallexe}"
Name: "{autodesktop}\Cursivis Next"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Cursivis Next"; ValueData: """{app}\{#MyAppExeName}"" --background"; Flags: uninsdeletevalue; Tasks: startup
Root: HKCU; Subkey: "Software\Google\Chrome\NativeMessagingHosts\app.cursivis.next.bridge"; ValueType: string; ValueName: ""; ValueData: "{app}\NativeHost\app.cursivis.next.bridge.json"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Microsoft\Edge\NativeMessagingHosts\app.cursivis.next.bridge"; ValueType: string; ValueName: ""; ValueData: "{app}\NativeHost\app.cursivis.next.bridge.json"; Flags: uninsdeletekey

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch Cursivis Next and configure OpenAI"; WorkingDir: "{app}"; Flags: nowait postinstall

[UninstallDelete]
Type: files; Name: "{app}\NativeHost\app.cursivis.next.bridge.json"
Type: files; Name: "{app}\startup-error.txt"
Type: dirifempty; Name: "{app}\NativeHost"
Type: dirifempty; Name: "{app}"

[Code]
function JsonEscapePath(Value: String): String;
begin
  Result := Value;
  StringChangeEx(Result, '\', '\\', True);
end;

procedure WriteNativeHostManifest;
var
  ManifestPath: String;
  HostPath: String;
  Lines: TArrayOfString;
begin
  ManifestPath := ExpandConstant('{app}\NativeHost\app.cursivis.next.bridge.json');
  HostPath := JsonEscapePath(ExpandConstant('{app}\NativeHost\Cursivis.Windows.NativeHost.exe'));
  SetArrayLength(Lines, 7);
  Lines[0] := '{';
  Lines[1] := '  "name": "app.cursivis.next.bridge",';
  Lines[2] := '  "description": "Cursivis authenticated native browser bridge",';
  Lines[3] := '  "path": "' + HostPath + '",';
  Lines[4] := '  "type": "stdio",';
  Lines[5] := '  "allowed_origins": ["chrome-extension://{#StableExtensionId}/"]';
  Lines[6] := '}';
  if not SaveStringsToUTF8File(ManifestPath, Lines, False) then
    RaiseException('Cursivis could not create the native browser host manifest.');
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    WriteNativeHostManifest;
end;
