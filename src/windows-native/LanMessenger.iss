#define MyAppName "LAN Messenger"
#define MyAppPublisher "Dave"
#define MyAppExeName "LanMessenger.exe"
#define MyAppSourceDir "LanMessenger\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\publish"

[Setup]
AppId={{F3A7B2C1-D4E5-4F60-9ABC-DEF012345678}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\LanMessenger
DefaultGroupName={#MyAppName}
OutputDir=Output
OutputBaseFilename=LanMessenger-Setup-{#MyAppVersion}
SetupIconFile={#MyAppSourceDir}\Assets\icon.ico
UninstallDisplayIcon={app}\Assets\icon.ico
Compression=lzma
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "startup"; Description: "Start LAN Messenger automatically when Windows starts"; GroupDescription: "Startup:"; Flags: checkedonce

[Files]
Source: "{#MyAppSourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "LanMessenger"; ValueData: "{app}\{#MyAppExeName}"; Flags: uninsdeletevalue; Tasks: startup
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "LanMessenger"; Flags: deletevalue uninsdeletevalue; Tasks: not startup

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; Firewall rules — allow inbound UDP 54231 (discovery) and TCP 54232 (messaging).
; The installer runs elevated, so netsh succeeds. Scoped to private/domain profiles only.
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""LAN Messenger Discovery"" dir=in action=allow protocol=UDP localport=54231 profile=private,domain program=""{app}\{#MyAppExeName}"""; Flags: runhidden; StatusMsg: "Configuring firewall...";
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""LAN Messenger Messaging"" dir=in action=allow protocol=TCP localport=54232 profile=private,domain program=""{app}\{#MyAppExeName}"""; Flags: runhidden; StatusMsg: "Configuring firewall...";
; Two launch entries:
;   - Interactive installs: standard "Launch app" checkbox on the final page.
;   - Silent installs (in-app updater): always relaunch so the user lands back in the app.
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
Filename: "{app}\{#MyAppExeName}"; Flags: nowait runasoriginaluser; Check: WizardSilent

[UninstallRun]
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""LAN Messenger Discovery"""; Flags: runhidden;
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""LAN Messenger Messaging"""; Flags: runhidden;
