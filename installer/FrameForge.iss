#ifndef AppVersion
  #error AppVersion must be supplied by Build-Installer.ps1.
#endif
#ifndef PayloadDir
  #error PayloadDir must be supplied by Build-Installer.ps1.
#endif
#ifndef RepoRoot
  #define RepoRoot SourcePath + ".."
#endif
#ifndef InstallerOutputDir
  #define InstallerOutputDir RepoRoot + "\dist"
#endif

[Setup]
AppId={{F9BFB42E-55A8-4333-BBAC-1C6CDE84FD8E}
AppName=FrameForge Recorder
AppVersion={#AppVersion}
AppPublisher=Victor Kirsanov
AppPublisherURL=https://github.com/viktorkirs/FrameForge
AppSupportURL=https://github.com/viktorkirs/FrameForge/issues
AppUpdatesURL=https://github.com/viktorkirs/FrameForge/releases
AppCopyright=Copyright (C) Victor Kirsanov
VersionInfoVersion={#AppVersion}.0
VersionInfoCompany=Victor Kirsanov
DefaultDirName={localappdata}\Programs\FrameForge
DefaultGroupName=FrameForge Recorder
DisableProgramGroupPage=no
AllowNoIcons=yes
DisableDirPage=no
DisableWelcomePage=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
WizardStyle=modern dark
WizardSizePercent=110
SetupIconFile={#RepoRoot}\src\FrameForge.UI\Assets\frameforge.ico
UninstallDisplayIcon={app}\FrameForge.exe
UninstallDisplayName=FrameForge Recorder
AppMutex=Local\FrameForge.Recorder.Running
SetupMutex=Local\FrameForge.Setup
CloseApplications=no
RestartApplications=no
OutputDir={#InstallerOutputDir}
OutputBaseFilename=FrameForge-Setup-{#AppVersion}-x64
Compression=lzma2/max
SolidCompression=yes
LZMANumBlockThreads=2
#ifdef SignBuild
SignTool=frameforge
SignedUninstaller=yes
SignedUninstallerDir={#RepoRoot}\artifacts\signed-uninstallers
SignToolRunMinimized=yes
#endif

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "german"; MessagesFile: "compiler:Languages\German.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Excludes: "*.pdb,portable.marker,data\*,artifacts\*"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\FrameForge Recorder"; Filename: "{app}\FrameForge.exe"; WorkingDir: "{app}"; AppUserModelID: "FrameForge.Recorder"
Name: "{group}\{cm:UninstallProgram,FrameForge Recorder}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\FrameForge Recorder"; Filename: "{app}\FrameForge.exe"; WorkingDir: "{app}"; Tasks: desktopicon; AppUserModelID: "FrameForge.Recorder"

[Run]
Filename: "{app}\FrameForge.exe"; Description: "{cm:LaunchProgram,FrameForge Recorder}"; Flags: nowait postinstall skipifsilent

; Settings are in AppData\Roaming\FrameForge, videos in the configured output folder.
; Deliberately do not use UninstallDelete: user-created files must survive removal.
