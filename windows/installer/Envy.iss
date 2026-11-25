; Envy for Windows: a per-user installer that needs no admin rights.
; Build with Inno Setup 6 after publishing the app to ..\artifacts\publish:
;   iscc /DAppVersion=1.2.0 installer\Envy.iss
; The file name stays Envy-Setup.exe for every version, so
; https://github.com/joeystdio/envy/releases/latest/download/Envy-Setup.exe always serves the newest one.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
AppId={{6B0E4F2C-3D8A-4E5B-9C71-0E7A2D9B4F11}
AppName=Envy
AppVersion={#AppVersion}
AppVerName=Envy {#AppVersion}
AppPublisher=Joey Stevense
AppPublisherURL=https://github.com/joeystdio/envy
; Product name and version on Envy-Setup.exe itself, which code signing checks.
VersionInfoProductName=Envy
VersionInfoVersion={#AppVersion}
VersionInfoProductTextVersion={#AppVersion}
AppSupportURL=https://github.com/joeystdio/envy/issues
DefaultDirName={autopf}\Envy
DisableProgramGroupPage=yes
DisableDirPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\artifacts
OutputBaseFilename=Envy-Setup
SetupIconFile=..\src\Envy.App\Assets\Envy.ico
UninstallDisplayIcon={app}\Envy.exe
UninstallDisplayName=Envy
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Closes a running Envy before replacing it, and starts it again afterwards.
CloseApplications=yes
RestartApplications=yes

[Tasks]
Name: "startup"; Description: "Start Envy in the notification area when I sign in"; Flags: unchecked

[Files]
Source: "..\artifacts\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Envy"; Filename: "{app}\Envy.exe"; Comment: "Environment variables and secrets for your shells and coding agents"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Envy"; ValueData: """{app}\Envy.exe"" --background"; Tasks: startup; Flags: uninsdeletevalue

[Run]
Filename: "{app}\Envy.exe"; Description: "Open Envy"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Takes the Envy line out of your PowerShell profiles and ~/.bashrc, and Envy's part out of cmd's AutoRun.
Filename: "{app}\Envy.exe"; Parameters: "--disconnect-all"; Flags: runhidden waituntilterminated; RunOnceId: "DisconnectShells"

[UninstallDelete]
; The generated shell files hold secret values. Settings (%APPDATA%\Envy) and Credential Manager entries stay,
; so reinstalling brings everything back.
Type: filesandordirs; Name: "{localappdata}\Envy\shell"
