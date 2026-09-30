#ifndef ReleaseVersion
  #error ReleaseVersion is required
#endif
#ifndef PayloadDir
  #error PayloadDir is required
#endif
#ifndef RuntimeInstaller
  #error RuntimeInstaller is required
#endif
#ifndef OutputDir
  #error OutputDir is required
#endif

[Setup]
AppId={{8AE1D6CC-982F-4A91-8B44-B2675EBF8C47}
AppName=Starshot Fork
AppVersion={#ReleaseVersion}
AppPublisher=Yukikaze1945
AppPublisherURL=https://github.com/Yukikaze1945/Starshot-releases
AppUpdatesURL=https://github.com/Yukikaze1945/Starshot-releases/releases
DefaultDirName={localappdata}\Programs\Starshot Fork
DefaultGroupName=Starshot Fork
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#OutputDir}
OutputBaseFilename=Starshot-{#ReleaseVersion}-setup-x64
SetupIconFile=..\src\logo.ico
UninstallDisplayIcon={app}\Starshot.exe
LicenseFile=..\LICENSE
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
DisableProgramGroupPage=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#RuntimeInstaller}"; DestDir: "{tmp}"; Flags: dontcopy

[Icons]
Name: "{group}\Starshot Fork"; Filename: "{app}\Starshot.exe"
Name: "{autodesktop}\Starshot Fork"; Filename: "{app}\Starshot.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Starshot.exe"; Description: "Launch Starshot Fork"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Version folders contain binaries only. User data in the installation root is retained.
Type: filesandordirs; Name: "{app}\app-*"

[Code]
const
  WebViewKey = 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';

function HasWebView2: Boolean;
var Version: String;
begin
  Result := (RegQueryStringValue(HKLM32, WebViewKey, 'pv', Version) or
             RegQueryStringValue(HKCU, WebViewKey, 'pv', Version)) and
            (Version <> '') and (Version <> '0.0.0.0');
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var ExitCode: Integer;
begin
  Result := '';
  if HasWebView2 then exit;
  ExtractTemporaryFile('{#ExtractFileName(RuntimeInstaller)}');
  if not Exec(ExpandConstant('{tmp}\{#ExtractFileName(RuntimeInstaller)}'),
              '/silent /install', '', SW_HIDE, ewWaitUntilTerminated, ExitCode) then
    Result := 'Could not start the bundled WebView2 Runtime installer.'
  else if not HasWebView2 then
    Result := 'WebView2 Runtime installation failed (code ' + IntToStr(ExitCode) + '). Please install WebView2 Runtime and retry.';
end;
