#define AppName "WallTrek"
#define AppVersion "1.1.0-test"

[Setup]
AppId={{3B417F62-FCD6-4C8A-BB0E-9EB8F551C9C4}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=WallTrek community fork
AppPublisherURL=https://github.com/Bl0ck154/WallTrek
DefaultDirName={localappdata}\Programs\WallTrek
DefaultGroupName=WallTrek
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
OutputDir=..\dist\installer
OutputBaseFilename=WallTrek-Windows-x64-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\assets\walltrek.ico
UninstallDisplayIcon={app}\WallTrek.exe
CloseApplications=yes
UsePreviousAppDir=yes

[Files]
Source: "..\dist\WallTrek\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\WallTrek"; Filename: "{app}\WallTrek.exe"
Name: "{autodesktop}\WallTrek"; Filename: "{app}\WallTrek.exe"; Tasks: desktopicon

[Tasks]
Name: desktopicon; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"

[Run]
Filename: "{app}\WallTrek.exe"; Description: "Launch WallTrek"; Flags: nowait postinstall skipifsilent
