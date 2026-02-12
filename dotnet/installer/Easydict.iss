; Easydict for Windows - Inno Setup Script
; Creates a standard EXE installer from the self-contained publish output.
; No code signing certificate required.
;
; Usage:
;   iscc /DAppVersion=0.3.2 /DPlatform=x64 /DPublishDir=..\publish\x64 Easydict.iss
;   iscc /DAppVersion=0.3.2 /DTag=0.3.2-rc.1 /DPlatform=x64 /DPublishDir=..\publish\x64 Easydict.iss
;
; Prerequisites:
;   - Inno Setup 6.x (https://jrsoftware.org/isinfo.php)
;   - A completed dotnet publish output in PublishDir

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

#ifndef Platform
  #define Platform "x64"
#endif

#ifndef PublishDir
  #define PublishDir "..\publish\" + Platform
#endif

; Tag is used in the output filename (e.g. "0.3.2" or "0.3.2-rc.1")
; Defaults to AppVersion when no prerelease suffix exists.
#ifndef Tag
  #define Tag AppVersion
#endif

#define AppName "Easydict"
#define AppFullName "Easydict for Windows"
#define AppPublisher "xiaocang"
#define AppExeName "Easydict.WinUI.exe"
#define AppUrl "https://github.com/tisfeng/Easydict"

[Setup]
AppId={{B7E2A5F3-9C41-4D82-A6F0-1E8B3C5D7F9A}
AppName={#AppFullName}
AppVersion={#AppVersion}
AppVerName={#AppFullName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppFullName}
AllowNoIcons=yes
; Output settings
OutputDir=..\installer-output
OutputBaseFilename=Easydict-v{#Tag}-{#Platform}-setup.unsigned
; Compression
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
; Installer appearance
WizardStyle=modern
SetupIconFile={#PublishDir}\AppIcon.ico
UninstallDisplayIcon={app}\AppIcon.ico
UninstallDisplayName={#AppFullName}
; Architecture
#if Platform == "x64"
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#elif Platform == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#endif
; Minimum Windows version: Windows 10 2004 (build 19041)
MinVersion=10.0.19041
; Privileges: install per-user by default (no admin needed)
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
; Version info embedded in the EXE
VersionInfoVersion={#AppVersion}.0
VersionInfoCompany={#AppPublisher}
VersionInfoDescription={#AppFullName} Setup
VersionInfoProductName={#AppFullName}
VersionInfoProductVersion={#AppVersion}
; Disable unnecessary wizard pages for a streamlined install
DisableProgramGroupPage=yes
; Close running instances before install/uninstall
CloseApplications=force
CloseApplicationsFilter=Easydict.WinUI.exe

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "startupentry"; Description: "Start Easydict when Windows starts"; GroupDescription: "Other:"

[Files]
; Install all files from the publish directory
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; Start Menu shortcut
Name: "{autoprograms}\{#AppFullName}"; Filename: "{app}\{#AppExeName}"
; Desktop shortcut (optional)
Name: "{autodesktop}\{#AppFullName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Registry]
; Startup entry (optional)
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Easydict"; ValueData: """{app}\{#AppExeName}"""; Flags: uninsdeletevalue; Tasks: startupentry
; easydict:// protocol handler (for browser extension fallback)
Root: HKCU; Subkey: "Software\Classes\easydict"; ValueType: string; ValueName: ""; ValueData: "URL:Easydict Protocol"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\easydict"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\easydict\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExeName}"" ""%1"""

[Run]
; Launch after install
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(AppFullName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
// Kill running instances before install to avoid locked files
procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep = ssInstall then
  begin
    Exec('taskkill', '/F /IM Easydict.WinUI.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;

// Clean up user data directory on uninstall
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    if DirExists(ExpandConstant('{localappdata}\Easydict')) then
      DelTree(ExpandConstant('{localappdata}\Easydict'), True, True, True);
  end;
end;
