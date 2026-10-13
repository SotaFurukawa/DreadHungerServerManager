# Dread Hunger Server Manager

Dread Hunger Dedicated Server（`DreadHungerServer.exe`）を、batファイルやPowerShellを使わずにGUIから起動・停止・監視するWindowsアプリです。見た目はShikiWariと同じWinUI 3（Fluentデザイン）です。

現在のバージョンは **v0.2.0 (beta)** です（正本は `Directory.Build.props`）。

## できること

- マップ（進入路付近 = Approach / 広漠の地 = Expanse / 山頂 = Departure）とゲーム設定を選んでサーバーを起動
  - 最大人数・吹雪までの日数・1日の長さ・動物ダメージ・寒さ・空腹速度・石炭消費・人狼人数
  - 数値は直接入力と上下ボタンの両方で変更でき、倍率はよく使う値（0.25〜2.0）から選べる
  - 範囲外の値は入力欄の下に赤字で表示し、その状態では起動しない
- 実行されるコマンドのリアルタイム表示とコピー（フルパス付き。batファイルにも使える）
- 起動・停止・再起動、状態（停止中／起動処理中／起動中／停止処理中）、PID、起動時刻、稼働時間、ポートの表示
- `IpNetDriver listening on port` と `Match State Changed from EnteringMap to WaitingToStart` の検出による起動完了の判定
- サーバーログの表示（自動スクロール・消去・コピー。エラーは赤、起動完了の行は緑で表示）
- 二重起動の防止。アプリ外で起動済みのサーバーも検出して、監視・停止できる
- 異常終了の検出（終了コードと稼働時間を表示）。試合の終了後にサーバーが自分で終了した場合は、異常終了ではなく案内として表示
- 接続中のプレイヤー人数と試合の状態（試合待ち／開始準備中／試合中／試合終了）の表示（サーバーログの参加・切断から集計）
- プリセット（標準: Standard / Short Game / Long Game、名前を付けて保存・読み込み・削除）
- 最後の設定（マップ・各値・サーバーの場所など）を自動保存し、次回起動時に復元

## 動作環境

- Windows 10（1809以降）/ Windows 11 / Windows Server 2019以降（デスクトップ エクスペリエンス）
- 配布版は .NET 10 と Windows App SDK を同梱しているため、サーバーに事前インストールするものはありません
- このアプリは **DreadHungerServer.exe と同じPC上で** 実行します（ローカルのプロセスを起動・監視するため）

> 動作確認は Windows 10 22H2（x64）で、実サーバーの代わりに同梱の疑似サーバー（後述）を使って行っています。Windows Server 2019 と実際の DreadHungerServer.exe での確認はまだです。

## インストール

[Releases](https://github.com/SotaFurukawa/DreadHungerServerManager/releases/latest) から `DreadHungerServerManager-Setup.exe` をダウンロードして実行します。

EC2 などのサーバーでは、ブラウザーを使わずに PowerShell で取得できます（Windows Server 2019 の Internet Explorer はセキュリティ強化の設定でダウンロードしにくいため）。

```powershell
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$setup = "$env:USERPROFILE\Downloads\DreadHungerServerManager-Setup.exe"
Invoke-WebRequest https://github.com/SotaFurukawa/DreadHungerServerManager/releases/latest/download/DreadHungerServerManager-Setup.exe -OutFile $setup -UseBasicParsing
Start-Process $setup
```

- 最初に「すべてのユーザー用」（`C:\Program Files`、管理者権限が必要）か「現在のユーザーのみ」（`%LOCALAPPDATA%\Programs`）かを選びます。どちらでも動作は同じです。
- インストーラーには署名がないため、「Windows によって PC が保護されました」と表示された場合は「詳細情報」→「実行」を押してください。
- 更新するときは、新しいインストーラーを同じように実行します。設定とプリセットはそのまま引き継がれます。
- アンインストールは「設定」→「アプリ」（Windows Server 2019 では「アプリと機能」）から行います。設定ファイル（後述）は残ります。

インストーラーを使わない場合は、Releases の zip を展開して `DreadHungerServerManager.exe` を直接実行することもできます。

## 使い方

1. `DreadHungerServerManager.exe`（スタートメニューの「Dread Hunger Server Manager」）を起動します。
2. 「サーバー実行ファイル」に `DreadHungerServer.exe` の場所を指定します（後述）。
3. マップとゲーム設定を選びます。プリセットを読み込むこともできます。
4. 「コマンドプレビュー」で実行内容を確認し、「サーバー起動」を押します。
5. 状態が「起動処理中」→「起動中」に変わり、「サーバー起動完了」と通知されたら接続できます。
6. 止めるときは「サーバー停止」を押します。まずサーバーへ Ctrl+C を送り、通常の終了処理を行わせます。設定した時間（既定20秒）を過ぎても終了しない場合は、強制終了するかを確認します。

アプリを閉じるときにサーバーが起動中だと、「停止して終了」「サーバーを残して終了」を選べます（設定ページで既定の動作を変えられます）。残して終了した場合も、次にアプリを開くと起動中のサーバーを自動で検出し、管理を再開します。

## DreadHungerServer.exe の設定方法

既定の場所は `C:\DreadHunger\WindowsServer\DreadHungerServer.exe` です。違う場所にある場合は、サーバーページの「サーバー実行ファイル」で次のどちらかを行います。

- 「参照…」を押して `DreadHungerServer.exe` を選ぶ
- パスを直接入力（貼り付け）する

入力欄の下に「見つかりました。」と出れば設定完了です。指定した場所は自動で保存され、次回起動時も使われます。サーバーは `DreadHungerServer.exe` のあるフォルダーを作業フォルダーとして起動します。

ポート番号を変える場合などは、設定ページの「追加引数」（例: `-port=7778`）を使います。`-log` の後ろに付きます。

## 設定ファイルの保存場所

`%LOCALAPPDATA%\DreadHungerServerManager\`（例: `C:\Users\<ユーザー名>\AppData\Local\DreadHungerServerManager\`）

| ファイル | 内容 |
| --- | --- |
| `settings.json` | サーバーの場所、最後のマップ・ゲーム設定、追加引数、停止・終了時の動作、ログ表示の設定 |
| `presets.json` | 自分で保存したプリセット（標準プリセットはアプリに内蔵） |
| `logs\app-YYYYMMDD.log` | このアプリ自体の動作記録（不具合調査用） |

設定ページの「保存先フォルダーを開く」からも開けます。JSONは手で編集できます。壊れて読み込めない場合は `settings.json.corrupt-日時` に退避したうえで既定値で起動します。

起動引数 `--data-dir <フォルダー>` を付けると、別の場所に保存できます（動作確認や、USBメモリ等での持ち運び用）。

## ログの取得について

`DreadHungerServer.exe` はブートストラップで、実体の `DreadHunger\Binaries\Win64\DreadHungerServer-Win64-Shipping.exe` を子プロセスとして起動します。実体はGUIアプリで、`-log` 指定時は自分専用のコンソール画面にログを出すため、多くの場合は標準出力からはログを取得できません。

そのため、サーバーが書き出すログファイルを読み取るのが基本です。

- 既定のログファイル: `<DreadHungerServer.exeのフォルダー>\DreadHunger\Saved\Logs\DreadHunger.log`
- サーバーは起動のたびに古いログを `DreadHunger-backup-日時.log` へ改名するため、アプリは新しいファイルに切り替わったことを検出して先頭から読み直します
- 設定ページで、取得方法（自動／ログファイルのみ／標準出力のみ）と、ログファイルの場所を変更できます

## ビルド方法

必要なもの: .NET SDK 10.0.401（`global.json` で指定）。Visual Studio は必須ではありません。

```powershell
dotnet build DreadHungerServerManager.sln -c Release
dotnet test tests\DreadHungerServerManager.Core.Tests -c Release --no-build
```

開発中にそのまま起動する場合:

```powershell
src\DreadHungerServerManager.App\bin\x64\Release\net10.0-windows10.0.26100.0\win-x64\DreadHungerServerManager.exe
```

### 配布用の作成

```powershell
powershell -ExecutionPolicy Bypass -File scripts\Publish.ps1
# デスクトップにショートカットも作る場合
powershell -ExecutionPolicy Bypass -File scripts\Publish.ps1 -CreateDesktopShortcut
```

`dist\DreadHungerServerManager-win-x64\`（フォルダー）と `dist\DreadHungerServerManager-<version>-win-x64.zip` ができます。フォルダーごと（またはzipを展開して）サーバーへコピーし、`DreadHungerServerManager.exe` を実行してください。MSIXではないため、Developer Modeや証明書のインストールは不要です。

### インストーラーの作成

[Inno Setup 6](https://jrsoftware.org/isinfo.php) が必要です（`winget install JRSoftware.InnoSetup`）。

```powershell
powershell -ExecutionPolicy Bypass -File scripts\Build-Installer.ps1
```

配布フォルダーを作り直したうえで、`dist\DreadHungerServerManager-Setup.exe` を作ります。定義は `installer\DreadHungerServerManager.iss` です。

### リリースの公開

1. `Directory.Build.props` のバージョン（`VersionPrefix` / `VersionSuffix` / `AssemblyVersion` / `FileVersion`）を上げてコミットします。
2. 同じバージョンのタグを付けて push します。

   ```powershell
   git tag v0.2.0-beta
   git push origin main v0.2.0-beta
   ```

3. GitHub Actions（`.github/workflows/release.yml`）がテスト・インストーラー作成を行い、Release にインストーラーと zip を公開します。インストーラーのファイル名は常に `DreadHungerServerManager-Setup.exe` なので、上の「インストール」のURLはいつも最新版を指します。

## 設計

### プロジェクト構成

```
DreadHungerServerManager.sln
├─ src\DreadHungerServerManager.Core     UIに依存しないロジック（net10.0 と net10.0-windows）
│   ├─ Models          GameSettings, GameMap, ServerPreset, ServerStatus, ServerLogLine, CloudInstance
│   ├─ Configuration   AppSettings（settings.jsonの形）, CloudSettings, AppPaths, ServerPaths
│   ├─ Services        ServerProcessManager, GameServerController, OperationGate, ServerCommandBuilder,
│   │                  GameSettingsValidator, ServerLogAnalyzer, PlayerTracker, LogFileTailer,
│   │                  JsonAppSettingsStore, PresetService
│   ├─ Services\Cloud  ICloudInstanceService（EC2などの操作の抽象）, FakeCloudInstanceService
│   └─ Interop         Win32 API（プロセス一覧・コンソール制御・ファイルID）
├─ src\DreadHungerServerManager.App      WinUI 3 のアプリ
│   ├─ ViewModels      ServerViewModel ほか（MVVM。WinUIの型に依存しない）
│   ├─ Views           MainWindow, ServerPage, SettingsPage, AboutPage
│   └─ Services        ダイアログ・通知・ファイル選択（IUserInteraction の実装）、単一インスタンス
├─ tests\DreadHungerServerManager.Core.Tests   xUnit（単体テスト＋疑似サーバーを使った結合テスト）
└─ tools\FakeDreadHungerServer           開発用の疑似 DreadHungerServer.exe
```

### 使用技術

- C# / .NET 10（LTS）。仕様書の第一候補は .NET 8 でしたが、.NET 8 のサポートが2026年11月に終了するため .NET 10 にしています
- WinUI 3（Windows App SDK 2.x）。ShikiWari と同じ見た目（Mica、TitleBar、NavigationView、カード）にするため WPF ではなく WinUI 3 を採用
- 未パッケージ（`WindowsPackageType=None`）＋自己完結（.NET と Windows App SDK を同梱）。必要なコンポーネント（WinUI / Foundation / DWrite）だけを参照し、AI/ML用のライブラリは含めない
- 外部ライブラリ: なし（MVVMの基盤も自前の小さな実装）
- Core は `net10.0` と `net10.0-windows` の両方でビルドします。`net10.0` 版は Windows 専用の部分（プロセス管理・ログ追尾・Win32 呼び出し）を含まず、AWS Lambda（Linux）上の Discord Bot からも使えます

### サーバー起動方式

`ServerCommandBuilder` が設定から `"<MAP>_Persistent?maxplayers=8?daysbeforeblizzard=3?...?thralls=2 -log [追加引数]"` を組み立て、`ProcessStartInfo` で `DreadHungerServer.exe` を起動します（作業フォルダーはexeのあるフォルダー、ウィンドウなし、標準出力・標準エラーはリダイレクト）。倍率は `1.0 → 1`、`0.75 → 0.75` のように表記します。

### 設定ファイル方式

`System.Text.Json` で `settings.json` / `presets.json` に保存します。書き込みは一時ファイルへ書き切ってから置き換えるため、途中で落ちても元のファイルは壊れません。画面の変更は0.5秒ごとにまとめて自動保存し、終了時にも保存します。

### プロセス管理方式（`ServerProcessManager`）

- 起動前に ToolHelp でプロセス一覧を調べ、`DreadHungerServer` / `DreadHungerServer-*` が動いていれば二重起動としてエラーにする
- 起動後はブートストラップの子孫（`DreadHungerServer-Win64-Shipping.exe`）も追跡し、すべて終了した時点で「停止」とする。停止を要求していない終了は異常終了として終了コードとともに通知する
- 停止は「サーバーのコンソールへ Ctrl+C」→ 待機 → 必要なら確認のうえプロセスツリーごと強制終了。Ctrl+C を送る前に、そのコンソールにサーバー以外のプロセス（batを実行したcmd.exeなど）が居ないことを確認する
- 起動完了の判定・ログ表示は、ログファイルの追尾（`LogFileTailer`）を主に、標準出力を補助に使う
- アプリ起動時に起動済みのサーバーを検出した場合は、そのプロセスとログファイルを管理下に置く
- 試合が終わると、サーバーは `FPlatformMisc::RequestExit(0)` を出して自分で終了する。この行を検出して終了コードが0なら、異常終了ではなく「サーバーが自分で終了した」として扱う（Ctrl+C による `ConsoleCtrl RequestExit` は含まない）
- プレイヤー人数は、ログの `Login request: ?Name=… userId: …`・`Join succeeded: …`（参加）と、`UNetConnection::Close: … Driver: GameNetDriver … PC: …`（切断）から数える（`PlayerTracker`）。参加まで至らなかった接続（`PC: NULL`）は数えない
- 起動・停止の手順（安全な終了→待機→強制終了）は `GameServerController` にまとめ、GUI（確認ダイアログあり）と、今後追加する EC2 上の常駐サービス・Discord Bot（確認なし）で共通に使う。同時に別の操作が来た場合は `OperationGate` が「Server operation already in progress」で断る

### 今後の拡張に向けて

- サーバー1台分の制御は `IServerProcessManager` と `ServerViewModel` の組にまとまっているため、複数サーバー管理はこの組をサーバーごとに持たせ、NavigationView に項目を増やす形で追加できます
- EC2の起動/停止・パブリックIP取得は `ICloudInstanceService` として Core に抽象を用意しました（AWS SDK を使う実装は別プロジェクトで追加します）。`FakeCloudInstanceService` は実AWSを使わずに「停止中→起動処理中→稼働中」「稼働中→停止処理中→停止中」の遷移と、起動のたびにIPが変わる動きを再現します。AWS 連携の設定は `settings.json` の `cloud` 節に保存します（秘密情報は保存しません）
- 自動起動は、`AppSettings` に項目を追加し、`App.OnLaunched` の起動済みサーバー検出の後で `StartCommand` を実行する形で追加できます
- Mod対応は、起動引数やファイル配置の処理を `ServerCommandBuilder` と新しいサービスに追加する想定です

## 開発用の疑似サーバー

`tools\FakeDreadHungerServer` をビルドすると `DreadHungerServer.exe` という名前の疑似サーバーができます。実サーバーと同じく、ブートストラップ→本体の2プロセス構成で、UE形式のログをログファイルへ書き、Ctrl+Cで終了します。アプリの「サーバー実行ファイル」にこれを指定すると、実サーバーなしで動作を確認できます。設定ページの「追加引数」で動作を変えられます。

| 追加引数 | 動作 |
| --- | --- |
| `-fakecrash=秒` | 指定秒後に異常終了（終了コード3） |
| `-fakedelay=ミリ秒` | 起動完了までの時間（既定1500） |
| `-fakeignorectrlc` | Ctrl+Cを無視（強制終了の確認用） |
| `-fakehidden` | 本体のコンソール画面を表示しない |
| `-fakesingle` | ブートストラップを使わず1プロセスで動く |
| `-fakeplayer=名前` | 起動完了の直後に、そのプレイヤーが参加したログを出す |
| `-fakematchend=秒` | 起動完了から指定秒後に試合が終わり、実物と同じく自分で終了する（終了コード0） |

## トラブルシューティング

- **「起動完了のログを検出できていません」と出る**: 設定ページの「使用するファイル」が実際のログファイルを指しているか確認してください。
- **停止できない／強制終了できない**: サーバーが管理者として起動されている場合、このアプリも管理者として実行する必要があります。
- **外部から接続できない**: Windows ファイアウォールと（EC2の場合は）セキュリティグループで、ゲームポート（既定 UDP 7777。`-port` で変えた場合はその番号）の受信を許可してください。
