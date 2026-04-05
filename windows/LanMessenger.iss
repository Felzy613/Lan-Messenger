[Setup]
AppId={{D1F2044B-7D82-46A0-8D35-6A85D46F442C}
AppName=LAN Messenger
AppVersion=1.3.2
AppPublisher=Dave
DefaultDirName={localappdata}\Programs\LAN Messenger
DefaultGroupName=LAN Messenger
DisableProgramGroupPage=yes
OutputDir=.\dist-installer
OutputBaseFilename=LanMessengerSetup
Compression=lzma
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest

[Files]
Source: ".\dist\LanMessenger\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\LAN Messenger"; Filename: "{app}\LanMessenger.exe"
Name: "{userstartup}\LAN Messenger"; Filename: "{app}\LanMessenger.exe"; Tasks: startupshortcut

[Tasks]
Name: "startupshortcut"; Description: "Run LAN Messenger when I sign in"; Flags: checkedonce

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "LanMessenger"; ValueData: """{app}\LanMessenger.exe"""; Flags: uninsdeletevalue; Tasks: startupshortcut

[Run]
Filename: "{app}\LanMessenger.exe"; Description: "Launch LAN Messenger now"; Flags: nowait postinstall skipifsilent
