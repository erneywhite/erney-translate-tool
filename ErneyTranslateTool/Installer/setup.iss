; =============================================================================
; Erney's Translate Tool - Installer Script
; Inno Setup Script File
; =============================================================================

#define MyAppName "Erney's Translate Tool"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "Erney"
#define MyAppURL "https://github.com/erney/translate-tool"
#define MyAppExeName "ErneyTranslateTool.exe"
#define MyCompanyName "Erney"
#define MyCopyright "© 2024 Erney. Все права защищены."

[Setup]
; Основные настройки
AppId={{A3B5C7D9-1234-5678-9ABC-DEF012345678}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyCompanyName}\{#MyAppName}
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
LicenseFile=
OutputDir=Output
OutputBaseFilename=ErneyTranslateTool-Setup-{#MyAppVersion}
SetupIconFile=
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64

; Требования к системе
MinVersion=10.0.17763
PrivilegesRequiredOverridesAllowed=dialog

; Настройки безопасности
SignedUninstaller=yes
UsePreviousAppDir=yes
UsePreviousGroup=yes
UsePreviousTasks=yes
UsePreviousSetupType=yes
UsePreviousPrivileges=yes

; Язык установщика
LanguageDetectionMethod=uilanguage
ShowLanguageDialog=auto

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "quicklaunchicon"; Description: "{cm:CreateQuickLaunchIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked; OnlyBelowVersion: 6.1; Check: not IsAdminInstallMode
Name: "startup"; Description: "Запускать вместе с Windows"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Основное приложение
Source: "..\bin\Release\net8.0-windows\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\bin\Release\net8.0-windows\{#MyAppName}.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\bin\Release\net8.0-windows\{#MyAppName}.runtimeconfig.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\bin\Release\net8.0-windows\{#MyAppName}.deps.json"; DestDir: "{app}"; Flags: ignoreversion

; DLL зависимости
Source: "..\bin\Release\net8.0-windows\*.dll"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs excludes: "{#MyAppName}.dll"

; Ресурсы и конфигурация
Source: "..\bin\Release\net8.0-windows\**\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs excludes: "*.dll,*.exe,*.json"

; Документы
Source: "..\README.md"; DestDir: "{app}"; Flags: isreadme
Source: "..\LICENSE"; DestDir: "{app}"; Flags: uninsneveruninstall

; Ярлыки
[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon
Name: "{userappdata}\Microsoft\Internet Explorer\Quick Launch\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: quicklaunchicon

; Запись в автозагрузку
[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#MyAppName}"; ValueData: """{app}\{#MyAppExeName}"""; Tasks: startup; Flags: uninsdeletevalue

[Run]
; Запуск приложения после установки
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent unchecked

; Проверка и установка .NET 8.0 Runtime
Filename: "{tmp}\dotnet-runtime-8.0.exe"; Parameters: "/install /passive /norestart"; Check: DotNetRuntimeNotInstalled; StatusMsg: "Установка .NET 8.0 Runtime..."; Flags: waituntilterminated

[UninstallRun]
; Очистка после удаления
Filename: "{app}\{#MyAppExeName}"; Parameters: "--uninstall"; Flags: runhidden

[Code]
var
  DotNetRuntimeCheckPage: TOutputMsgWizardPage;

// Проверка наличия .NET 8.0 Runtime
function DotNetRuntimeNotInstalled: Boolean;
var
  ResultCode: Integer;
begin
  // Проверка через реестр
  Result := not RegKeyExists(HKEY_LOCAL_MACHINE, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedhost\8.0');
  
  // Альтернативная проверка
  if Result then
    Result := not RegKeyExists(HKEY_LOCAL_MACHINE, 'SOFTWARE\WOW6432Node\dotnet\Setup\InstalledVersions\x64\sharedhost\8.0');
end;

// Проверка архитектуры системы
function Is64Bit: Boolean;
begin
  Result := Is64BitInstallMode;
end;

// Инициализация установки
function InitializeSetup(): Boolean;
var
  ResultCode: Integer;
begin
  Result := True;
  
  // Проверка архитектуры
  if not Is64Bit then
  begin
    MsgBox('Это приложение требует 64-разрядную версию Windows.', mbError, MB_OK);
    Result := False;
    Exit;
  end;
  
  // Проверка версии Windows
  if not IsWindowsVersionOrNewer(10, 0, 17763) then
  begin
    MsgBox('Это приложение требует Windows 10 версии 1809 или новее.', mbError, MB_OK);
    Result := False;
    Exit;
  end;
end;

// Загрузка .NET Runtime если необходимо
procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
  DownloadUrl: String;
begin
  if CurStep = ssPostInstall then
  begin
    if DotNetRuntimeNotInstalled then
    begin
      DownloadUrl := 'https://download.visualstudio.microsoft.com/download/pr/3e1a0961-e5e7-4c0a-8b6e-7e3e3e3e3e3e/dotnet-runtime-8.0.0-win-x64.exe';
      
      if DownloadFile(DownloadUrl, ExpandConstant('{tmp}\dotnet-runtime-8.0.exe')) then
      begin
        // Установка .NET Runtime
        if Exec(ExpandConstant('{tmp}\dotnet-runtime-8.0.exe'), '/install /passive /norestart', '', SW_SHOW, ewWaitUntilTerminated, ResultCode) then
        begin
          if ResultCode = 0 then
            Log('.NET 8.0 Runtime успешно установлен')
          else
            Log('.NET 8.0 Runtime установка завершилась с кодом: ' + IntToStr(ResultCode));
        end
        else
        begin
          MsgBox('Не удалось установить .NET 8.0 Runtime. Пожалуйста, установите его вручную с сайта Microsoft.', mbError, MB_OK);
        end;
      end
      else
      begin
        MsgBox('Не удалось загрузить .NET 8.0 Runtime. Пожалуйста, установите его вручную с сайта Microsoft.', mbError, MB_OK);
      end;
    end;
  end;
end;

// Отображение информации после установки
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    // Удаление настроек пользователя (опционально)
    // DelTree(ExpandConstant('{userappdata}\ErneyTranslateTool'), True, True, True);
  end;
end;

// Проверка запущенного приложения перед установкой
function IsAppRunning: Boolean;
begin
  Result := IsTaskRunning('{#MyAppExeName}');
end;

// Предупреждение о запущенном приложении
function InitializeUninstall(): Boolean;
begin
  Result := True;
  
  if IsAppRunning then
  begin
    if MsgBox('Приложение {#MyAppName} запущено. Закройте его перед удалением.' + #13#10 + #13#10 + 'Продолжить удаление?', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDNO then
      Result := False;
  end;
end;

// Текст лицензии (можно добавить отдельный файл)
[Messages]
WelcomeLabel1=Добро пожаловать в мастер установки {#MyAppName}
WelcomeLabel2=Эта программа установит {#MyAppName} версии {#MyAppVersion} на ваш компьютер.%n%nРекомендуется закрыть все работающие приложения перед продолжением.
ClickFinish=Нажмите Готово для завершения установки {#MyAppName}.

[CustomMessages]
NameAndVersion={#MyAppName} {#MyAppVersion}
AdditionalIcons=Дополнительные ярлыки:
CreateDesktopIcon=Создать ярлык на рабочем столе
CreateQuickLaunchIcon=Создать ярлык в панели быстрого запуска
LaunchProgram=Запустить {#MyAppName}
DotNetRuntimeRequired=.NET 8.0 Runtime требуется для работы приложения
DotNetRuntimeInstalling=Установка .NET 8.0 Runtime...
DotNetRuntimeDownload=Загрузка .NET 8.0 Runtime...

[Dirs]
; Создание директории для настроек
Name: "{userappdata}\ErneyTranslateTool"; Permissions: users-full

[INI]
; Запись информации об установке
Filename: "{app}\install_info.ini"; Section: "Install"; Key: "Version"; String: "{#MyAppVersion}"
Filename: "{app}\install_info.ini"; Section: "Install"; Key: "Date"; String: "{code:GetCurrentDateTime}"

[Code]
function GetCurrentDateTime(Param: String): String;
begin
  Result := GetDateTimeString('yyyy-mm-dd hh:nn:ss', '-', ':');
end;
