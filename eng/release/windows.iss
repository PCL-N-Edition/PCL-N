#ifndef Payload
  #error Payload is required
#endif
[Setup]
AppId={{50AE2197-7C7B-43DA-BE98-1CDEBE86B273}
AppName=Nexa
AppVersion={#ProductVersion}
VersionInfoVersion={#NumericVersion}.0
DefaultDirName={localappdata}\Programs\Nexa
DefaultGroupName=Nexa
PrivilegesRequired=lowest
ArchitecturesAllowed={#InstallArch}
ArchitecturesInstallIn64BitMode={#InstallArch}
OutputDir={#OutputDir}
OutputBaseFilename={#OutputName}
SetupIconFile={#IconPath}
UninstallDisplayIcon={app}\Nexa.Desktop.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked
[Files]
Source: "{#Payload}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
[Icons]
Name: "{group}\Nexa"; Filename: "{app}\Nexa.Desktop.exe"; WorkingDir: "{app}"
Name: "{userdesktop}\Nexa"; Filename: "{app}\Nexa.Desktop.exe"; WorkingDir: "{app}"; Tasks: desktopicon
