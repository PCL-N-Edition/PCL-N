#define AppId "{{7E5A69DC-48D1-4713-9684-D561C18A1D1F}"

[Setup]
AppId={#AppId}
AppName=PCL N
AppVersion={#ProductVersion}
AppPublisher=PCL N contributors
AppPublisherURL=https://pcln.top/
AppSupportURL=https://github.com/PCL-N-Edition/PCL-N/issues
DefaultDirName={localappdata}\Programs\PCL N
DefaultGroupName=PCL N
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed={#ArchitecturesAllowed}
ArchitecturesInstallIn64BitMode={#ArchitecturesInstallIn64BitMode}
OutputDir={#OutputDirectory}
OutputBaseFilename={#OutputBaseName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\PCL-N-Edition.exe
CloseApplications=yes
RestartApplications=no

[Files]
; Multi-file scatter layout (launcher + host + dep zips). User entry stays PCL-N-Edition.exe.
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#InstallKindMarker}"; DestDir: "{app}"; DestName: "pcln-install-kind"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\PCL N"; Filename: "{app}\PCL-N-Edition.exe"; WorkingDir: "{app}"

[Run]
Filename: "{app}\PCL-N-Edition.exe"; Description: "Launch PCL N"; Flags: nowait postinstall skipifsilent
