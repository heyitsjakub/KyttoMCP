; Inno Setup installer for the self-contained Windows publish.
; Build the publish first, then compile this file with ISCC.exe.
;
; The version is not written here. `release.ps1` reads it from Kytto.App.csproj
; and passes it as /DAppVersionSlug=…, so the number on the installer is the
; number the binaries were built with rather than a second copy that can drift.
; The fallback below only exists so compiling this file by hand still works, and
; it is deliberately obvious when it is the one that was used.

#ifndef AppVersionSlug
  #define AppVersionSlug "0.0.0-local"
#endif

#ifndef AppRuntimeSlug
  #define AppRuntimeSlug "win-x64"
#endif

; "1.0.3-beta" reads as "1.0.3 beta" in the wizard. The file version resource
; accepts up to four numbers, so an already four-part release must not gain a
; fifth trailing zero.
#define AppVersion StringChange(AppVersionSlug, "-", " ")
#if Pos("-", AppVersionSlug) > 0
  #define AppVersionNumeric Copy(AppVersionSlug, 1, Pos("-", AppVersionSlug) - 1)
#else
  #define AppVersionNumeric AppVersionSlug
#endif

#define AppName "Kytto"
#define AppPublisher "Jakub Studio"

; Release automation normally uses the version-independent dist directory. An
; explicit publish directory is also supported so a new artifact can be built
; beside an older release without overwriting its staging files.
#ifndef AppPublishDir
  #define AppPublishDir "..\dist\Kytto-" + AppRuntimeSlug
#endif
#define PublishDir AppPublishDir

#if AppRuntimeSlug == "win-arm64"
  #define AppArchitecture "arm64"
#else
  #define AppArchitecture "x64compatible"
#endif

[Setup]
AppId={{A6B54AA4-2B66-4A8E-A2E8-7A4D2D7CB2DF}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed={#AppArchitecture}
ArchitecturesInstallIn64BitMode={#AppArchitecture}
OutputDir=..\dist
OutputBaseFilename=Kytto-Setup-{#AppRuntimeSlug}-{#AppVersionSlug}
VersionInfoVersion={#AppVersionNumeric}
VersionInfoProductVersion={#AppVersionNumeric}
SetupIconFile=..\src\Kytto.App\Kytto.ico
UninstallDisplayIcon={app}\Kytto.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Kytto"; Filename: "{app}\Kytto.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\Kytto"; Filename: "{app}\Kytto.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\Kytto.exe"; Description: "Launch Kytto"; Flags: nowait postinstall skipifsilent

[Code]
// A server in Gateway mode is started through kytto-mcp-proxy.exe in this folder, so
// removing Kytto leaves it unable to start and its client shows only "failed". The
// way out is in the app (MCP Doctor, Restore Direct mode for all), so say so before
// anything is removed. Suppressible: a silent uninstall goes ahead.
function InitializeUninstall(): Boolean;
var
  Routes: AnsiString;
begin
  Result := True;
  if LoadStringFromFile(ExpandConstant('{userappdata}\Kytto\gateway-routes.json'), Routes) and
     (Pos('"id"', Routes) > 0) then
    Result := SuppressibleMsgBox(
      'Some MCP servers still start through Kytto''s Gateway helper. After Kytto is removed, ' +
      'their clients will not be able to start them.' + #13#10#13#10 +
      'To keep them working, choose No, open Kytto, and in MCP Doctor choose ' +
      '"Restore Direct mode for all". Then uninstall again.' + #13#10#13#10 +
      'Uninstall anyway?',
      mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDYES) = IDYES;
end;
