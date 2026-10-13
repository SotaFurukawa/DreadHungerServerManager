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
; 「すべてのユーザー」（Program Files、管理者権限が必要）か「自分のみ」（%LOCALAPPDATA%\Programs）を選べる。
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
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
; EC2 用。すべてのユーザー用（管理者）でインストールした場合だけ選べる。既に登録済みなら自動で選択する。
Name: "hostservice"; Description: "EC2 用の常駐サービスを入れる（Windows にログインしなくても、起動時にゲームサーバーを自動で起動）"; GroupDescription: "常駐サービス:"; Flags: unchecked; Check: IsAdminInstallMode

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

procedure CurPageChanged(CurPageID: Integer);
begin
  // 更新時、常駐サービスが登録済みなら入れ直す（チェックを付けておく）。
  if (CurPageID = wpSelectTasks) and IsAdminInstallMode and HostServiceExists() then
    WizardSelectTasks('hostservice');
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Result := '';
  // ファイルを置き換えられるよう、常駐サービスが動いていれば止める（ゲームサーバーも安全に停止される）。
  if IsAdminInstallMode and HostServiceExists() then
    Exec('powershell.exe', '-NoProfile -ExecutionPolicy Bypass -Command "Stop-Service -Name {#HostServiceName} -ErrorAction SilentlyContinue"',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;
