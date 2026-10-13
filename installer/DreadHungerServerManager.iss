; Dread Hunger Server Manager のインストーラー（Inno Setup 6）。
; 直接ではなく scripts\Build-Installer.ps1 からビルドする（バージョンと配布フォルダーを渡すため）。

#ifndef AppVersion
  #error "/DAppVersion=<バージョン> を指定してください（scripts\Build-Installer.ps1 から実行してください）。"
#endif
#ifndef AppFileVersion
  #error "/DAppFileVersion=<x.y.z.w> を指定してください。"
#endif
#ifndef SourceDir
  #error "/DSourceDir=<配布フォルダー> を指定してください。"
#endif
#ifndef OutputDir
  #error "/DOutputDir=<出力先> を指定してください。"
#endif

#define AppName "Dread Hunger Server Manager"
#define AppExeName "DreadHungerServerManager.exe"
#define RepoUrl "https://github.com/SotaFurukawa/DreadHungerServerManager"
#define HostServiceName "DreadHungerServerManagerHost"
#define HostExe "host\DreadHungerServerManager.Host.exe"
#define UninstallKey "Software\Microsoft\Windows\CurrentVersion\Uninstall\{33BE9B09-DAFF-4490-A7C2-10FE1A3CE396}_is1"

[Setup]
; AppId は更新・アンインストールの識別に使うため、変更しない。
AppId={{33BE9B09-DAFF-4490-A7C2-10FE1A3CE396}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=SotaFurukawa
AppPublisherURL={#RepoUrl}
AppSupportURL={#RepoUrl}/issues
AppUpdatesURL={#RepoUrl}/releases
VersionInfoVersion={#AppFileVersion}
; すべてのユーザー用として Program Files に入れる（EC2 用の常駐サービスの登録に管理者権限が必要なため）。
; 以前の「現在のユーザー用」（%LOCALAPPDATA%\Programs）のインストールは、入れる前に自動で取り除く。
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Windows 10 1809 / Windows Server 2019 以降
MinVersion=10.0.17763
OutputDir={#OutputDir}
OutputBaseFilename=DreadHungerServerManager-Setup
SetupIconFile=..\src\DreadHungerServerManager.App\Assets\AppIcon.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; 更新時にアプリが起動していれば閉じる（サーバー本体は別の場所にあるため止まらない）。
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
; EC2 用。EC2 上でインストールする場合と、既に登録済みの場合は自動でチェックを付ける。
Name: "hostservice"; Description: "EC2 用の常駐サービスを入れる（Windows にログインしなくても、起動時にゲームサーバーを自動で起動）"; GroupDescription: "常駐サービス:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#HostExe}"; Parameters: "install"; StatusMsg: "常駐サービスを登録しています..."; Flags: runhidden waituntilterminated; Tasks: hostservice
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\{#HostExe}"; Parameters: "uninstall"; Flags: runhidden waituntilterminated; RunOnceId: "UninstallHostService"

[Code]
function HostServiceExists(): Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec(ExpandConstant('{sys}\sc.exe'), 'query {#HostServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;

// EC2 の Windows に入っている EC2Launch / EC2Config のフォルダーがあれば EC2 とみなす。
function IsRunningOnEc2(): Boolean;
begin
  Result := DirExists(ExpandConstant('{commonpf64}\Amazon\EC2Launch'))
    or DirExists(ExpandConstant('{commonpf64}\Amazon\Ec2ConfigService'))
    or DirExists(ExpandConstant('{commonappdata}\Amazon\EC2Launch'))
    or DirExists(ExpandConstant('{commonappdata}\Amazon\EC2-Windows\Launch'));
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  // EC2 上、または常駐サービスが登録済み（更新）なら、常駐サービスにチェックを付けておく。
  if (CurPageID = wpSelectTasks) and (IsRunningOnEc2() or HostServiceExists()) then
    WizardSelectTasks('hostservice');
end;

// 以前の版を「現在のユーザー用」で入れていた場合は、二重に入らないよう取り除く（設定とプリセットは残る）。
procedure RemovePerUserInstall();
var
  UninstallString: String;
  ResultCode: Integer;
  Waited: Integer;
begin
  if not RegQueryStringValue(HKCU, '{#UninstallKey}', 'UninstallString', UninstallString) then
    Exit;

  Exec(RemoveQuotes(UninstallString), '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  // アンインストーラーは一時フォルダーの複製に処理を任せて先に終わるため、消えるまで待つ。
  Waited := 0;
  while RegKeyExists(HKCU, '{#UninstallKey}') and (Waited < 60000) do
  begin
    Sleep(500);
    Waited := Waited + 500;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Result := '';
  RemovePerUserInstall();

  // ファイルを置き換えられるよう、常駐サービスが動いていれば止める（ゲームサーバーも安全に停止される）。
  if HostServiceExists() then
    Exec('powershell.exe', '-NoProfile -ExecutionPolicy Bypass -Command "Stop-Service -Name {#HostServiceName} -ErrorAction SilentlyContinue"',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;
