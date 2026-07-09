; WinFlow installer — self-contained app (no .NET install needed).
; Two flavors, selected by the BundleModels define:
;   Full (/DBundleModels): both on-device models included, offline immediately.
;   Lite (default):        ~90 MB; models download in-app on first use.
;
; Build with installer\build.ps1 (stages the publish output and models first).
;
; Uninstall cleanup:
;   - Always removes %APPDATA%\WinFlow (settings.json, recordings).
;   - Prompts to remove WinFlow/OpenAI from Credential Manager (optional).
;   - uninstall-cleanup.ps1 deletes the credential when the user accepts.

#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif

#define MyAppName "WinFlow"
#define MyAppExeName "WinFlow.App.exe"
#define ParakeetId "parakeet-tdt-0.6b-v2-int8"
#define QwenId "qwen2.5-0.5b-instruct-q4-k-m"

[Setup]
AppId={{8E1B4C0A-2F63-4D0B-9C1E-5A7D2B9F4E11}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=WinFlow
DefaultDirName={localappdata}\Programs\{#MyAppName}
; Always let the user pick where the app + models (~1.2 GB) go.
DisableDirPage=no
DisableProgramGroupPage=yes
; Per-user install: no admin prompt, and models live in the user's
; %LOCALAPPDATA%\WinFlow\models where the app looks by default.
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=Output
#ifdef BundleModels
OutputBaseFilename=WinFlow-Setup-Full-{#MyAppVersion}
#else
OutputBaseFilename=WinFlow-Setup-{#MyAppVersion}
#endif
; Model weights are already quantized and barely compress; fast keeps the
; compile and install times reasonable.
Compression=lzma2/fast
SolidCompression=no
WizardStyle=modern
UninstallDisplayIcon={app}\{#MyAppExeName}
CloseApplications=yes

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "publish\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion
Source: "uninstall-cleanup.ps1"; DestDir: "{app}"; Flags: ignoreversion
; Models live inside the chosen install dir; the app auto-discovers a
; "models" folder next to its exe (LocalModelManager.BundledModelsRoot).
#ifdef BundleModels
Source: "..\models\{#ParakeetId}\*"; DestDir: "{app}\models\{#ParakeetId}"; Flags: ignoreversion
Source: "..\models\{#QwenId}\*"; DestDir: "{app}\models\{#QwenId}"; Flags: ignoreversion
#endif

[Dirs]
; Present even in the Lite flavor: in-app model downloads then default to
; the user's chosen install dir instead of %LOCALAPPDATA% on C:.
Name: "{app}\models"

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Sweep anything the app added under the install dir (e.g. re-downloaded
; model files).
Type: filesandordirs; Name: "{app}\models"
; Settings and debug recordings live in %APPDATA%\WinFlow (not the install dir).
Type: filesandordirs; Name: "{userappdata}\WinFlow"

[Code]
var
  RemoveCredential: Boolean;

function InitializeUninstall(): Boolean;
begin
  Result := True;
  if WizardSilent then
    RemoveCredential := False
  else
    RemoveCredential := MsgBox(
      'WinFlow stores an OpenAI API key in Windows Credential Manager (WinFlow/OpenAI).' + #13#10#13#10 +
      'Remove the saved API key now?' + #13#10 +
      '(Settings and recordings in %APPDATA%\WinFlow are always removed.)',
      mbConfirmation, MB_YESNO) = IDYES;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
begin
  if (CurUninstallStep = usUninstall) and RemoveCredential then
  begin
    Exec('powershell.exe',
      '-ExecutionPolicy Bypass -NoProfile -WindowStyle Hidden -File "' +
      ExpandConstant('{app}\uninstall-cleanup.ps1') + '" -RemoveCredential',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
  if (CurUninstallStep = usPostUninstall) and (not RemoveCredential) and (not WizardSilent) then
  begin
    MsgBox(
      'To finish cleanup, remove WinFlow/OpenAI from' + #13#10 +
      'Control Panel → Credential Manager → Windows Credentials.',
      mbInformation, MB_OK);
  end;
end;
