#define AppName "NowFound"
#define DistDir "..\dist\NowFound"
#define MainDb  "maib"
#define MyAppVersion Trim(FileRead(FileOpen(SourcePath + "..\VERSION")))

[Setup]
AppId={{7F1E5C0A-4B7D-4E39-9C52-3A6D8B2F91E4}
AppName={#AppName}
AppVersion={#MyAppVersion}
AppPublisher={#AppName}
DefaultDirName={sd}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename={#AppName}-Setup-{#MyAppVersion}
Compression=lzma2/fast
SolidCompression=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
DisableWelcomePage=no
WizardStyle=modern zircon
WizardImageFile=images\WizardImage.bmp
WizardSmallImageFile=images\WizardSmallImage.bmp
UninstallDisplayName={#AppName}
; Icon shown in "Apps & features": the favicon of the Web app (installed with the web files).
UninstallDisplayIcon={app}\web\wwwroot\favicon.ico
SetupLogging=yes
; The running service / indexer are stopped by this script (see PrepareToInstall).
CloseApplications=no
; The directory page is skipped when updating (the previous install folder is reused).
DisableDirPage=auto

[Languages]
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
japanese.DotNetMissing=ASP.NET Core ランタイム 10 が見つかりません。NowFound の起動に必要です。%n%nhttps://dotnet.microsoft.com/download/dotnet/10.0 からインストールしてください。%n%nこのままインストールを続行しますか？
japanese.ServiceFailed=Web サービスの登録に失敗しました（終了コード: %1）。%n手動で次のスクリプトを管理者権限の PowerShell で実行してください。%n%2
japanese.TaskFailed=Indexer のタスクスケジューラ登録に失敗しました（終了コード: %1）。%n手動で次のスクリプトを管理者権限の PowerShell で実行してください。%n%2
japanese.DeleteData=データフォルダ（ユーザー・設定・検索データベース）を削除しますか？%n%n%1%n%n「いいえ」を選ぶとデータは残り、再インストール時にそのまま使えます。
japanese.OpenBrowser=ブラウザーで NowFound を開く
japanese.UrlShortcut=NowFound を開く
japanese.UpdateNotice=すでにインストールされています。プログラムファイルだけを更新します。サービス・タスクスケジューラの登録、データ、証明書、設定ファイルは変更しません。
japanese.ServiceStartFailed=更新後の Web サービスの開始に失敗しました（終了コード: %1）。サービス「NowFound」を手動で開始してください。
english.DotNetMissing=The ASP.NET Core Runtime 10 was not found. It is required to run NowFound.%n%nInstall it from https://dotnet.microsoft.com/download/dotnet/10.0.%n%nContinue with the installation anyway?
english.ServiceFailed=Registering the web service failed (exit code: %1).%nRun the following script manually from an elevated PowerShell.%n%2
english.TaskFailed=Registering the indexer scheduled task failed (exit code: %1).%nRun the following script manually from an elevated PowerShell.%n%2
english.DeleteData=Delete the data folder (users, settings and search databases)?%n%n%1%n%nChoose No to keep the data; it is reused when you reinstall.
english.OpenBrowser=Open NowFound in the browser
english.UrlShortcut=Open NowFound
english.UpdateNotice=NowFound is already installed. Only the program files will be updated; the service and scheduled task registration, data, certificate and settings are left unchanged.
english.ServiceStartFailed=Starting the web service after the update failed (exit code: %1). Start the "NowFound" service manually.

[Dirs]
Name: "{app}\data"

[Files]
; Windows package. Linux-only files and native runtimes are left out.
Source: "{#DistDir}\*"; DestDir: "{app}"; \
  Excludes: "\install-linux.sh,\run-indexer.sh,\*.service,\*.timer,\web\appsettings.Production.json,\certs\*,\web\runtimes\linux-x64\*,\indexer\runtimes\linux-x64\*"; \
  Flags: recursesubdirs createallsubdirs ignoreversion
; HTTPS settings are never overwritten, so a replaced certificate survives upgrades.
; The certificate itself is generated on the first registration (register-web-service.ps1), not shipped.
Source: "{#DistDir}\web\appsettings.Production.json"; DestDir: "{app}\web"; Flags: onlyifdoesntexist

[INI]
Filename: "{app}\{#AppName}.url"; Section: "InternetShortcut"; Key: "URL"; String: "https://localhost:5002/"

[Icons]
Name: "{group}\{cm:UrlShortcut}"; Filename: "{app}\{#AppName}.url"
Name: "{group}\INSTALL.md"; Filename: "{app}\INSTALL.md"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"

[UninstallDelete]
Type: files; Name: "{app}\{#AppName}.url"

[Run]
Filename: "https://localhost:5002/"; Description: "{cm:OpenBrowser}"; Flags: postinstall shellexec skipifsilent unchecked

[Code]
const
  ServiceName = '{#AppName}';
  TaskName = '{#AppName}Indexer';

function PowerShellPath: String;
begin
  Result := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
end;

function RunPowerShell(const Arguments: String): Integer;
begin
  if not Exec(PowerShellPath, '-NoProfile -ExecutionPolicy Bypass ' + Arguments, ExpandConstant('{sys}'),
    SW_HIDE, ewWaitUntilTerminated, Result) then
  begin
    Result := -1;
  end;
  Log(Format('powershell %s -> %d', [Arguments, Result]));
end;

function RunScript(const ScriptName, ExtraArguments: String): Integer;
begin
  Result := RunPowerShell('-File "' + ExpandConstant('{app}') + '\' + ScriptName + '" ' + ExtraArguments);
end;

#include "DotNetCheck.pas"

var
  Updating: Boolean;

// True when this program was installed by this installer before (Inno's uninstall entry for the same AppId).
function IsAlreadyInstalled: Boolean;
var
  Key: String;
begin
  Key := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\' + '{#SetupSetting("AppId")}' + '_is1';
  StringChangeEx(Key, '{{', '{', True);
  Result := RegKeyExists(HKLM, Key) or RegKeyExists(HKCU, Key);
end;

function ServiceExists: Boolean;
begin
  Result := RegKeyExists(HKLM, 'SYSTEM\CurrentControlSet\Services\' + ServiceName);
end;

function TaskExists: Boolean;
var
  Code: Integer;
begin
  Result := Exec(ExpandConstant('{sys}\schtasks.exe'), '/Query /TN "' + TaskName + '"', '', SW_HIDE, ewWaitUntilTerminated, Code)
    and (Code = 0);
end;

function InitializeSetup: Boolean;
begin
  Result := True;
  Updating := IsAlreadyInstalled;
  Log('Updating = ' + IntToStr(Ord(Updating)));
  if (not Updating) and (not IsAspNetCore10Installed) then
  begin
    Result := SuppressibleMsgBox(ExpandConstant('{cm:DotNetMissing}'), mbError, MB_YESNO, IDYES) = IDYES;
  end;
end;

procedure InitializeWizard;
begin
  if Updating then
  begin
    WizardForm.WelcomeLabel2.Caption := WizardForm.WelcomeLabel2.Caption + #13#10#13#10 + ExpandConstant('{cm:UpdateNotice}');
  end;
end;

// Update: stop the running service and indexer so that their files can be replaced.
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  if Updating or ServiceExists then
  begin
    RunPowerShell('-Command "Stop-ScheduledTask -TaskName ''' + TaskName + ''' -ErrorAction SilentlyContinue; ' +
      'Stop-Service -Name ''' + ServiceName + ''' -Force -ErrorAction SilentlyContinue"');
  end;
  Result := '';
end;

procedure RegisterWebService;
var
  Code: Integer;
  SeedArgument: String;
begin
  // The initial administrator is created only on the very first start (no database yet).
  SeedArgument := '';
  if not FileExists(ExpandConstant('{app}\data\{#MainDb}.db')) then
  begin
    SeedArgument := '-Seed';
  end;

  Code := RunScript('register-web-service.ps1', SeedArgument);
  if Code <> 0 then
  begin
    SuppressibleMsgBox(FmtMessage(ExpandConstant('{cm:ServiceFailed}'), [IntToStr(Code), ExpandConstant('{app}\register-web-service.ps1')]),
      mbError, MB_OK, IDOK);
  end;
end;

procedure RegisterIndexerTask;
var
  Code: Integer;
begin
  Code := RunScript('register-indexer-task.ps1', '');
  if Code <> 0 then
  begin
    SuppressibleMsgBox(FmtMessage(ExpandConstant('{cm:TaskFailed}'), [IntToStr(Code), ExpandConstant('{app}\register-indexer-task.ps1')]),
      mbError, MB_OK, IDOK);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Code: Integer;
begin
  if CurStep <> ssPostInstall then
  begin
    Exit;
  end;

  if not Updating then
  begin
    // First install: register the service and the scheduled task.
    RegisterWebService;
    RegisterIndexerTask;
    Exit;
  end;

  // Update: the service and the task are left as they are (their settings are kept).
  // Only what is missing is registered again; an existing service is just started again.
  if ServiceExists then
  begin
    Code := RunPowerShell('-Command "Start-Service -Name ''' + ServiceName + '''"');
    if Code <> 0 then
    begin
      SuppressibleMsgBox(FmtMessage(ExpandConstant('{cm:ServiceStartFailed}'), [IntToStr(Code)]), mbError, MB_OK, IDOK);
    end;
  end
  else
  begin
    RegisterWebService;
  end;

  if not TaskExists then
  begin
    RegisterIndexerTask;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    // Remove the service (and its firewall rule) and the scheduled task before the files are deleted.
    RunScript('register-web-service.ps1', '-Unregister');
    RunScript('register-indexer-task.ps1', '-Unregister');
  end
  else if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{app}\data');
    if DirExists(DataDir) then
    begin
      if UninstallSilent = False then
      begin
        if MsgBox(FmtMessage(ExpandConstant('{cm:DeleteData}'), [DataDir]), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        begin
          DelTree(DataDir, True, True, True);
        end;
      end;
    end;
  end;
end;
