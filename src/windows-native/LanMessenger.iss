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
; VC++ 2015-2022 x64 redistributable. Required by NSec.Cryptography's bundled
; libsodium.dll (depends on vcruntime140.dll / vcruntime140_1.dll / msvcp140.dll).
; Fetched into this folder by CI before ISCC runs; do not check the binary in.
Source: "vc_redist.x64.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall; Check: not IsVCRedistInstalled

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "LanMessenger"; ValueData: "{app}\{#MyAppExeName}"; Flags: uninsdeletevalue; Tasks: startup
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "LanMessenger"; Flags: deletevalue uninsdeletevalue; Tasks: not startup

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; AppUserModelID: "LanMessenger.DesktopApp"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon; AppUserModelID: "LanMessenger.DesktopApp"

[Run]
; Install VC++ 2015-2022 x64 redistributable first if it isn't already present.
; libsodium.dll (P/Invoked by NSec.Cryptography) refuses to load without it,
; which on clean Windows 10 boxes manifests as TypeInitializationException in
; SessionCrypto on the first message — leaving the app unable to encrypt or
; decrypt anything. Exit code 1638 = "newer version already installed", which
; the chained installer reports as failure; treat both as success.
Filename: "{tmp}\vc_redist.x64.exe"; Parameters: "/install /quiet /norestart"; StatusMsg: "Installing Visual C++ runtime..."; Flags: waituntilterminated; Check: not IsVCRedistInstalled
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

[Code]
// True if a VC++ 2015-2022 x64 runtime ≥ 14.30 (the build that introduced
// vcruntime140_1.dll) is already installed, so we don't re-run the redistributable
// installer on every upgrade. Inno Setup is 32-bit, so we explicitly read the
// 64-bit registry view where the x64 runtime registers itself.
function IsVCRedistInstalled(): Boolean;
var
  Installed, Bld: Cardinal;
begin
  Result := False;
  if not RegQueryDWordValue(HKEY_LOCAL_MACHINE,
       'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\X64', 'Installed', Installed) then
    Exit;
  if Installed <> 1 then
    Exit;
  // 'Bld' is the build number of the installed runtime (e.g. 30704 for 14.30).
  // libsodium.dll needs vcruntime140_1.dll which first shipped in 14.20 (build 27820);
  // 14.30 is a safe floor that's been the redistributable's GA target for years.
  if not RegQueryDWordValue(HKEY_LOCAL_MACHINE,
       'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\X64', 'Bld', Bld) then
    Exit;
  Result := Bld >= 30704;
end;
