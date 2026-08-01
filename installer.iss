; =====================================================================
; Inno Setup Installer Script for m-mslc-overlay
; Target Architecture: Windows x64 (win-x64)
; Compiler: Inno Setup 6.0+
; =====================================================================

#define MyAppName "m-mslc-overlay"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "Gawasna"
#define MyAppURL "https://github.com/Gawasna/m-mslc-overlay"
#define MyAppExeName "m-mslc-overlay.exe"
#define PublishDir "bin\Release\net9.0\win-x64\publish"

[Setup]
; Unique App ID for Upgrade / Uninstall Tracking
AppId={{E6F3A5B1-92D0-4B12-885E-5D31F585C921}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}

; Default Install Directory: Program Files\m-mslc-overlay
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes

; Output Configuration
OutputDir=dist
OutputBaseFilename=m-mslc-overlay-setup-v{#MyAppVersion}
Compression=lzma2/ultra
SolidCompression=yes
WizardStyle=modern

; Execution Privileges
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=commandline dialog

; Architecture Filtering (64-bit Windows)
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; Icon & Display Settings
UninstallDisplayIcon={app}\{#MyAppExeName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; 1. Main Published Application Files (Excluding dynamic runtime files & extractor binaries)
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "extractor\*,extractor,logs\*,*.log,plugins.lock.json,*.pdb,testhost.*,Microsoft.TestPlatform.*,Microsoft.VisualStudio.TestPlatform.*,xunit.*,CodeCoverage\*,InstrumentationEngine\*,Microsoft.CodeCoverage.*,plugins\*,Mono.Cecil.*,Microsoft.VisualStudio.CodeCoverage.*"

; 2. Root Plugin Manifest File
Source: "plugins.manifest.json"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}\runtimes"
Type: filesandordirs; Name: "{app}\LatoFont"
