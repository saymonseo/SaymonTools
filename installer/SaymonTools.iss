; SaymonTools installer (Inno Setup 6). Built by CI: iscc /DAppVersion=X.Y.Z installer\SaymonTools.iss
; after build.ps1 has filled ..\publish. Installs for all users into Program Files.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#define AppName "SaymonTools"
#define AppExe "SaymonTools.exe"
#define RepoUrl "https://github.com/saymonseo/SaymonTools"

[Setup]
; Never change the AppId: updates find the installed copy by it.
AppId={{EE06DDCF-4FE1-449B-9800-832A0EB5C681}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Saymon
AppPublisherURL={#RepoUrl}
AppSupportURL={#RepoUrl}/issues
AppUpdatesURL={#RepoUrl}/releases
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\artifacts
OutputBaseFilename=SaymonTools-Setup-{#AppVersion}
SetupIconFile=..\src\SaymonTools.App\Assets\SaymonTools.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
LicenseFile=..\LICENSE
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
; The running app is closed before its files are replaced (also for silent updates with /CLOSEAPPLICATIONS);
; [Run] starts the new version itself.
CloseApplications=yes
RestartApplications=no
; HKCU entries below only clean up what the app registered for the user.
UsedUserAreasWarning=no

[Languages]
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Autostart and the Explorer context menu are created by the app (per user); remove them on uninstall.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "{#AppName}"; Flags: dontcreatekey uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\*\shell\{#AppName}"; Flags: dontcreatekey uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Directory\shell\{#AppName}"; Flags: dontcreatekey uninsdeletekey

[Run]
; Started as the signed-in user, not elevated: drag and drop from Explorer doesn't reach elevated windows.
Filename: "{app}\{#AppExe}"; Parameters: "--updated"; Flags: nowait runasoriginaluser; Check: IsUpdate
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent runasoriginaluser; Check: not IsUpdate

[Code]
{ The app runs the setup with /UPDATE=1 when it updates itself. }
function IsUpdate: Boolean;
begin
  Result := ExpandConstant('{param:UPDATE|0}') = '1';
end;
