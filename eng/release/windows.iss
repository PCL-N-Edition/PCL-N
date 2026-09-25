#ifndef Payload
  #error Payload is required
#endif
[Setup]
AppId={{50AE2197-7C7B-43DA-BE98-1CDEBE86B273}
AppName=NexaCL
AppVersion={#ProductVersion}
VersionInfoVersion={#NumericVersion}.0
DefaultDirName={autopf}\NexaCL
DefaultGroupName=NexaCL
UsePreviousAppDir=no
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=
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
Name: "{commonprograms}\NexaCL"; Filename: "{app}\Nexa.Desktop.exe"; WorkingDir: "{app}"
Name: "{commondesktop}\NexaCL"; Filename: "{app}\Nexa.Desktop.exe"; WorkingDir: "{app}"; Tasks: desktopicon
