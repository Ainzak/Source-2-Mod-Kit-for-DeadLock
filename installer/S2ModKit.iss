; S2ModKit setup (Inno Setup 6). Built by scripts/Publish-Release.ps1 -InnoCompiler <ISCC.exe>:
;   ISCC.exe /DAppVersion=0.7.0 /DSourceDir=<staged release folder> /DOutputDir=<release output> installer\S2ModKit.iss
; The user chooses the install folder. No administrator rights are needed unless the user picks a protected folder.
; The app keeps its settings and data in the user profile (%LocalAppData%\S2ModKit, movable in the app), so updates and
; uninstalling never touch them. In-app updates run this setup silently with /DIR=<current folder>.

#ifndef AppVersion
  #error Pass /DAppVersion=x.y.z
#endif
#ifndef SourceDir
  #error Pass /DSourceDir=<staged release folder>
#endif
#ifndef OutputDir
  #define OutputDir "."
#endif

[Setup]
AppId={{AA8D57B6-B25F-4D05-8533-4BD6730EC153}
AppName=S2ModKit
AppVersion={#AppVersion}
AppVerName=S2ModKit {#AppVersion}
AppPublisher=S2ModKit contributors
AppPublisherURL=https://github.com/Ainzak/Source-2-Mod-Kit-for-DeadLock
AppSupportURL=https://github.com/Ainzak/Source-2-Mod-Kit-for-DeadLock/issues
AppUpdatesURL=https://github.com/Ainzak/Source-2-Mod-Kit-for-DeadLock/releases
DefaultDirName={autopf}\S2ModKit
DisableDirPage=no
UsePreviousAppDir=yes
DefaultGroupName=S2ModKit
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog commandline
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
LicenseFile={#SourceDir}\LICENSE
OutputDir={#OutputDir}
OutputBaseFilename=S2ModKit-{#AppVersion}-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
UninstallDisplayName=S2ModKit {#AppVersion}
UninstallDisplayIcon={app}\S2ModKit.exe

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "portable"

[Icons]
Name: "{group}\S2ModKit"; Filename: "{app}\S2ModKit.exe"
Name: "{group}\S2ModKit user guide"; Filename: "{app}\docs\USER-GUIDE.md"
Name: "{group}\Uninstall S2ModKit"; Filename: "{uninstallexe}"
Name: "{autodesktop}\S2ModKit"; Filename: "{app}\S2ModKit.exe"; Tasks: desktopicon

[Run]
; interactive install: offer to start the app; silent install (an in-app update): start it again
Filename: "{app}\S2ModKit.exe"; Description: "Start S2ModKit"; Flags: nowait postinstall skipifsilent
Filename: "{app}\S2ModKit.exe"; Flags: nowait skipifnotsilent
