; =============================================================================
; Erney's Translate Tool — Inno Setup script
; -----------------------------------------------------------------------------
; Builds a per-user installer that drops the app into
; %LocalAppData%\Programs\ErneyTranslateTool. No UAC prompt, install folder is
; writable so settings, cache, history and downloaded tessdata files all live
; next to the exe.
;
; Build steps:
;   1.  cd <repo>\ErneyTranslateTool
;   2.  dotnet publish -c Release -r win-x64 --self-contained true ^
;          -p:PublishSingleFile=false -p:IncludeNativeLibrariesForSelfExtract=true
;   3.  iscc Installer\setup.iss
;
; Output: Installer\Output\ErneyTranslateTool-Setup-1.0.0.exe
; =============================================================================

#define MyAppName "Erney's Translate Tool"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "Erney"
#define MyAppURL "https://github.com/erneywhite/erney-translate-tool"
#define MyAppExeName "ErneyTranslateTool.exe"

[Setup]
AppId={{A3B5C7D9-1234-5678-9ABC-DEF012345678}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={localappdata}\Programs\ErneyTranslateTool
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=Output
OutputBaseFilename=ErneyTranslateTool-Setup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
MinVersion=10.0.17763
SetupLogging=yes
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Создать ярлык на рабочем столе"; GroupDescription: "Дополнительные ярлыки:"; Flags: unchecked

[Files]
; Everything from the publish folder, including bundled tessdata and native DLLs.
Source: "..\bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\*"; \
    DestDir: "{app}"; \
    Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{group}\Удалить {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Запустить {#MyAppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Logs and downloaded tessdata are inside {app} — clean them up too. Settings
; and history (settings.json, cache.db, history.db) stay since some users
; reinstall expecting their data preserved. Comment out the next line if you
; want to wipe everything.
Type: filesandordirs; Name: "{app}\logs"
Type: filesandordirs; Name: "{app}\tessdata"

; Минимальная версия Windows контролируется через MinVersion= в [Setup] —
; Inno Setup сам остановит установку и покажет нативное сообщение.
