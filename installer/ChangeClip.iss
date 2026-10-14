#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\portable"
#endif
[Setup]
AppId={{CB07D7E2-C0F8-48C9-938D-60295E311846}
AppName=ChangeClip
AppVersion={#AppVersion}
AppPublisher=Functtive
DefaultDirName={localappdata}\Programs\ChangeClip
DefaultGroupName=ChangeClip
PrivilegesRequired=lowest
MinVersion=10.0
OutputDir=..\artifacts
OutputBaseFilename=ChangeClip-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\ChangeClip.exe
CloseApplications=yes
RestartApplications=no
[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
[Icons]
Name: "{group}\ChangeClip"; Filename: "{app}\ChangeClip.exe"
Name: "{autodesktop}\ChangeClip"; Filename: "{app}\ChangeClip.exe"
[Run]
Filename: "{app}\ChangeClip.exe"; Description: "Pokreni ChangeClip"; Flags: nowait postinstall skipifsilent
[Code]
function InitializeSetup(): Boolean;
var Release: Cardinal;
begin
  Result := RegQueryDWordValue(HKLM, 'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full', 'Release', Release) and (Release >= 528040);
  if not Result then
    MsgBox('ChangeClip zahtijeva Microsoft .NET Framework 4.8 ili noviji. Instalirajte ga sa Microsoft stranice i ponovo pokrenite ovaj installer.', mbError, MB_OK);
end;
