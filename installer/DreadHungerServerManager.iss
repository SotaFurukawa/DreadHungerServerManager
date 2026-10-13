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

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
