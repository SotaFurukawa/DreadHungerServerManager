# Dread Hunger Server Manager

Dread Hunger Dedicated Server（`DreadHungerServer.exe`）を、batファイルやPowerShellを使わずにGUIから起動・停止・監視するWindowsアプリです。見た目はShikiWariと同じWinUI 3（Fluentデザイン）です。

現在のバージョンは **v0.4.0-preview.2** です（正本は `Directory.Build.props`）。

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
- AWS EC2 の状態表示と起動・停止（「AWS / Cloud」ページ）。パブリック IP と接続先（`IP:7777`）の表示とコピー。起動・停止の完了まで待って結果を表示
- EC2 用の常駐サービス（Windows サービス）。Windows にログインしなくても、EC2 の起動時にゲームサーバーを自動で起動し、GUI からの起動・再起動・停止の要求を処理する。GUI には EC2 上のゲームサーバーの状態（試合の状態・人数・稼働時間）を表示
- 「AWS / Cloud」ページでマップ・ゲーム設定を選んで「EC2 起動」すると、EC2 の起動後にその設定でゲームサーバーが立ち上がるまで待って結果を表示
- 自動停止: 誰も遊んでいない状態（ゲームサーバーが止まったまま、または参加者0人）が30分続いたら、または起動から6時間たったら、ゲームサーバーを止めてから EC2 を停止（時間は変更可）
- Discord Bot（AWS Lambda）: `/start`（設定パネルまたは引数でマップ・人数・倍率を指定）・`/stop`・`/restart`・`/status`。使う人は EC2 の場所を意識せず、表示されるのは接続先のアドレスだけ。Discord サーバーのメンバー全員（または許可したユーザー・ロールだけ）が操作でき、誰が何をしたかをログ用チャンネルに記録。自動停止の予告も Discord に届く
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

- すべてのユーザー用として `C:\Program Files\Dread Hunger Server Manager` に入ります。管理者権限の確認（「このアプリがデバイスに変更を加えることを許可しますか？」）が出たら「はい」を押します。以前の版を「現在のユーザー用」（`%LOCALAPPDATA%\Programs`）で入れていた場合は、自動で取り除いてから入れます（設定とプリセットは残ります）。
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

## AWS の設定（EC2 の起動・停止）

「AWS / Cloud」ページから、Dread Hunger サーバーを置いた EC2 を起動・停止し、最新の接続先（パブリック IP:7777）を確認できます。EC2 は停止・起動のたびにパブリック IP が変わる（Elastic IP を除く）ため、遊ぶ前にここで確認してください。

> **アクセスキーはアプリに入力・保存しません。** AWS SDK の標準の方法（共有認証情報ファイルのプロファイル、環境変数、EC2 の IAM ロール）から読み込みます。settings.json に保存されるのはリージョン・インスタンスID・プロファイル名だけです。

### 1. 専用の IAM ユーザーを作る（ルートユーザーのキーは使わない）

ルートユーザーのアクセスキーは、漏れるとアカウントのすべてを操作されてしまいます（高額なインスタンスを大量に起動されるなど、料金の被害に上限がありません）。このアプリ用に、**対象の EC2 の起動・停止だけを許可した IAM ユーザー**を作ってください。

1. AWS マネジメントコンソールで **IAM → ポリシー → ポリシーの作成** を開き、「JSON」に次を貼り付けます。`<アカウントID>`（12桁）・リージョン・インスタンスIDは自分のものに置き換えます。ポリシー名は例えば `DreadHungerServerControl` にします。

   ```json
   {
     "Version": "2012-10-17",
     "Statement": [
       {
         "Sid": "DescribeInstances",
         "Effect": "Allow",
         "Action": "ec2:DescribeInstances",
         "Resource": "*"
       },
       {
         "Sid": "ControlDreadHungerServer",
         "Effect": "Allow",
         "Action": ["ec2:StartInstances", "ec2:StopInstances", "ec2:RebootInstances"],
         "Resource": "arn:aws:ec2:ap-northeast-3:<アカウントID>:instance/i-xxxxxxxxxxxxxxxxx"
       },
       {
         "Sid": "ServerControlParameters",
         "Effect": "Allow",
         "Action": ["ssm:GetParameter", "ssm:PutParameter"],
         "Resource": "arn:aws:ssm:ap-northeast-3:<アカウントID>:parameter/dhsm/default/*"
       }
     ]
   }
   ```

   `ec2:DescribeInstances`（状態の取得）は AWS の仕様で対象のインスタンスを限定できないため `"*"` にしています。読み取りだけの権限です。起動・停止・再起動は、上のインスタンスだけに限定しています。`ssm:*Parameter` は、EC2 上の常駐サービスとの受け渡し（`/dhsm/default/` の下だけ）に使います。

   > 既に EC2 の権限だけでポリシーを作った場合は、**IAM → ポリシー → 該当のポリシー → 編集** で `ServerControlParameters` の部分を追加してください。

2. **IAM → ユーザー → ユーザーの作成** で、名前を例えば `dread-hunger-manager` にします（コンソールへのアクセスは不要）。「ポリシーを直接アタッチする」で 1. のポリシーを選びます。
3. 作ったユーザーの **セキュリティ認証情報 → アクセスキーを作成** を開き、用途に「AWS の外部で実行されるアプリケーション」を選んで作成します。表示されたアクセスキーIDとシークレットアクセスキーを、次の手順で使います（シークレットはこの画面でしか表示されません）。

### 2. この PC に認証情報を保存する

`%USERPROFILE%\.aws\credentials`（例: `C:\Users\<ユーザー名>\.aws\credentials`）というファイルを作り、次のように書きます。`[dread-hunger]` がプロファイル名です。

> **ファイル名は拡張子なしの `credentials` です。** `credentials.txt` や `credentials.ini` だと読み込まれません。メモ帳で保存するときは、ファイル名を `"credentials"` のように `"` で囲むと拡張子が付きません。エクスプローラーで「ファイル名拡張子」を表示にすると確認できます。

```
[dread-hunger]
aws_access_key_id = AKIA................
aws_secret_access_key = ........................................
```

AWS CLI が入っていれば `aws configure --profile dread-hunger` でも作れます。このファイルはユーザーフォルダーにあり、リポジトリやアプリのフォルダーには入りません。キーをチャットやスクリーンショットで共有しないでください。

### 3. アプリで設定する

「AWS / Cloud」ページの「接続設定」に入力します。

| 項目 | 例 |
| --- | --- |
| リージョン | `ap-northeast-3`（大阪） |
| インスタンスID | `i-xxxxxxxxxxxxxxxxx`（EC2 コンソールのインスタンス一覧に表示） |
| AWS プロファイル | `dread-hunger`（2. で付けた名前。空欄なら環境変数・`default` プロファイル・EC2 の IAM ロールの順に使う） |
| ゲームのポート | `7777`（接続先の表示に使う） |

入力すると状態を取得し、以降は30秒ごとに更新します。「EC2 起動」を押すと稼働中になるまで待ち、パブリック IP と接続先を表示します。Windows とゲームサーバーが起動するまでには、さらに数分かかります（ゲームサーバーの自動起動は、下の「EC2 の常駐サービス」を入れると行われます）。

「EC2 停止」は確認のうえ停止し、停止完了まで待ちます。**このアプリを対象の EC2 上で動かしている場合は**、先にこのアプリで管理しているゲームサーバーを安全に停止してから、EC2（この PC 自体）を停止します。

### セキュリティグループ

EC2 のセキュリティグループで、ゲームのポート（UDP 7777）の受信を許可してください（送信元 `0.0.0.0/0`）。リモートデスクトップ（TCP 3389）は送信元を自分の IP に限定することをおすすめします。このアプリの AWS 連携のために開けるポートはありません。

## EC2 の常駐サービス（ログインしなくてもゲームサーバーを自動起動）

EC2 に常駐サービス（Windows サービス `DreadHungerServerManagerHost`）を入れると、次のようになります。

- EC2 が起動すると、Windows にログインしなくても、前回の設定でゲームサーバーを自動で起動します
- 自宅の PC の「AWS / Cloud」ページから、EC2 上のゲームサーバーの状態（起動中・試合中・人数・稼働時間）を確認し、起動・再起動・停止ができます
- Windows のシャットダウン時（EC2 の停止時）は、ゲームサーバーを安全に停止してから終わります
- 試合が終わってゲームサーバーが自分で終了した場合は、停止のままにします（次に遊ぶときは「ゲームサーバー起動」）

常駐サービスと GUI・Discord Bot は、AWS Systems Manager の Parameter Store（標準パラメーター、無料）の `/dhsm/default/request`（要求）・`/dhsm/default/status`（状態）・`/dhsm/default/config`（自動停止の設定）でやり取りします。常駐サービスから数秒ごとに取りに行くため、**EC2 に受信用のポートを開ける必要はありません**。秘密情報は置きません。

### 1. EC2 用の IAM ロールを作る

常駐サービスは、アクセスキーではなく **EC2 に割り当てた IAM ロール**で AWS を使います。

1. **IAM → ロール → ロールを作成** で、「信頼されたエンティティタイプ」に **AWS のサービス**、ユースケースに **EC2** を選びます。許可ポリシーは付けずに進め、名前を例えば `DreadHungerServerHostRole` にして作成します。
2. 作ったロールの **許可を追加 → インラインポリシーを作成** で「JSON」に次を貼り付けます（`<アカウントID>` は自分のもの）。名前は例えば `DreadHungerServerHost` にします。

   ```json
   {
     "Version": "2012-10-17",
     "Statement": [
       {
         "Sid": "ServerControlParameters",
         "Effect": "Allow",
         "Action": ["ssm:GetParameter", "ssm:PutParameter"],
         "Resource": "arn:aws:ssm:ap-northeast-3:<アカウントID>:parameter/dhsm/default/*"
       },
       {
         "Sid": "DescribeInstances",
         "Effect": "Allow",
         "Action": "ec2:DescribeInstances",
         "Resource": "*"
       },
       {
         "Sid": "AutoStopThisInstance",
         "Effect": "Allow",
         "Action": "ec2:StopInstances",
         "Resource": "arn:aws:ec2:ap-northeast-3:<アカウントID>:instance/i-xxxxxxxxxxxxxxxxx"
       }
     ]
   }
   ```

   `ec2:DescribeInstances` は、EC2 上で GUI を開いたときに「AWS / Cloud」ページで状態を表示するためのものです（読み取りのみ）。`ec2:StopInstances` は自動停止で自分自身（このインスタンスだけ）を止めるためのものです。

   > 既にロールを作っている場合は、ロールの「許可」タブでインラインポリシー `DreadHungerServerHost` を開き、**編集**で `AutoStopThisInstance` の部分を追加してください。これがないと、自動停止の時刻になってもゲームサーバーだけが止まり、EC2 は動き続けます（常駐サービスのログに「EC2 を停止できませんでした」と出ます）。

3. **EC2 → インスタンス** で対象のインスタンスを選び、**アクション → セキュリティ → IAM ロールを変更** で 1. のロールを選んで更新します。

### 2. 常駐サービスを入れる

EC2 にリモートデスクトップで接続し、インストーラーを実行します。

1. 管理者権限の確認が出たら「はい」を押します。
2. 「追加タスクの選択」で **「EC2 用の常駐サービスを入れる」** にチェックが付いていることを確認します（EC2 上では自動で付きます）。
3. インストールが終わると、常駐サービスが登録されて開始し、ゲームサーバーを起動します（既に GUI でゲームサーバーを動かしている場合は、それを管理下に置きます）。

更新のときも同じインストーラーを実行します。常駐サービスが入っていれば自動でチェックが付き、いったん止めて（ゲームサーバーも安全に停止して）から入れ直します。

### 3. 起動に使うゲーム設定

自宅の PC の「AWS / Cloud」ページには、「サーバー」ページと同じプリセット・マップ・ゲーム設定の欄があります（内容も共通です）。設定を選んで **「EC2 起動」** を押すと、次の順に進み、最後に結果（マップと接続先）を表示します。

1. 選んだ設定を、常駐サービス向けの起動要求として Parameter Store に書く
2. EC2 を起動し、稼働中になるまで待つ（1～2分）
3. Windows と常駐サービスが起動し、常駐サービスが起動直後に要求を拾って、その設定でゲームサーバーを起動するまで待つ（2～5分）

起動に使った設定は EC2 側に「前回の設定」として保存されるため、AWS コンソールなどから EC2 を直接起動した場合も、その設定でゲームサーバーが立ち上がります。EC2 が稼働中のときは「EC2 上のゲームサーバー」の「起動」「再起動」で、同じ設定を使えます。

### 4. 自動停止

AWS の料金を抑えるため、常駐サービスは次のどちらかで、**ゲームサーバーを安全に止めてから EC2 を停止**します。

| 条件 | 既定 | 内容 |
| --- | --- | --- |
| 誰も遊んでいない | 30分 | ゲームサーバーが止まったまま（起動していない・試合が終わって終了した）、または参加者が0人の状態が続いたら停止。ゲームサーバーの起動・操作・参加があると数え直す。停止の5分前に警告 |
| 最大稼働時間 | 6時間 | EC2 の起動から一定時間で停止（試合中でも停止する。30分前・10分前・5分前に警告） |

自宅の PC の「AWS / Cloud」ページの「自動停止」で、それぞれのオン・オフと時間を変えられます。変更したら **「EC2 に反映」** を押してください。常駐サービスが1分以内に読み込み、「EC2 上で使用中」の欄に反映後の設定が表示されます。次の停止予定は「EC2 上のゲームサーバー」に「自動停止予定 HH:MM（あと N 分）」と表示されます。

自動停止には、EC2 の IAM ロールに `ec2:StopInstances`（1. の `AutoStopThisInstance`）が必要です。

### 5. 常駐サービスの設定

常駐サービスの設定は `C:\ProgramData\DreadHungerServerManager\settings.json`（GUI と同じ形式）です。最初の起動時に既定値で作られます。

| 項目 | 内容 |
| --- | --- |
| `serverExecutablePath` | `DreadHungerServer.exe` の場所（既定 `C:\DreadHunger\WindowsServer\DreadHungerServer.exe`） |
| `game` | 前回の設定。起動に成功するたびに、使った設定で上書きされる（自動起動にはこの設定を使う） |
| `additionalArguments` | 追加引数 |
| `cloud.autoStartGameServer` | `true` なら EC2 の起動時にゲームサーバーを自動で起動する |
| `cloud.serverId` | 受け渡しに使う名前（既定 `default`。複数サーバー用） |

編集した後は、PowerShell（管理者）で `Restart-Service DreadHungerServerManagerHost` を実行して読み込ませます。

### 6. 動作確認

1. EC2 を再起動します（または停止してから起動します）。**Windows にはログインしません。**
2. 数分後、自宅の PC の「AWS / Cloud」ページで「EC2 上のゲームサーバー」が **「起動中（参加待ち）」** になり、接続先が表示されることを確認します。
3. ゲームから接続して遊べることを確認します。

うまくいかない場合は、EC2 にリモートデスクトップで接続して次を確認してください。

- 常駐サービスのログ: `C:\ProgramData\DreadHungerServerManager\logs\host-YYYYMMDD.log`
- サービスの状態: PowerShell で `Get-Service DreadHungerServerManagerHost`
- ゲームサーバーのログ: `C:\DreadHunger\WindowsServer\DreadHunger\Saved\Logs\DreadHunger.log`

常駐サービスは LocalSystem で動き、ゲームサーバーはログイン画面の裏側（セッション 0）で動くため、画面には表示されません。EC2 上で GUI を開くと「サーバー」ページに「常駐サービスが管理しています」と表示され、ログと状態を確認できます（起動・停止は「AWS / Cloud」ページから行います）。常駐サービスを止めるには `Stop-Service DreadHungerServerManagerHost`、削除するにはアプリをアンインストールします。

## Discord Bot（/start・/stop・/restart・/status）

Discord から、EC2 ごとゲームサーバーを起動・停止できます。Bot は **AWS Lambda** で動くため、EC2 が止まっていても応答します（PC を起動しておく必要はありません）。

- **使う人は EC2 の場所（リージョン・インスタンスID）を気にしなくてよい**: 接続先は、管理者が Lambda の環境変数に1度だけ設定します。コマンドで指定するのはゲームの設定だけです。
- **Bot の返信にはインスタンスIDやリージョンを出しません**: 表示するのは、ゲームに入るためのアドレス（`IP:7777`）だけです。設定や権限の問題で失敗したときも、利用者には「管理者に連絡してください」とだけ表示し、詳しい原因は管理者だけが見られるログ（CloudWatch Logs）に出します。
- 料金: 個人で使う程度なら、Lambda の無料枠（毎月 100万リクエスト・40万 GB 秒）に収まる見込みです。

| コマンド | 内容 |
| --- | --- |
| `/start` | 引数なし: 自分だけに見える**設定パネル**を出します。前回の設定が選ばれた状態で、マップ・最大人数・人狼人数・吹雪までの日数・1日の長さ・各倍率をプルダウンで選び、「この設定で起動」を押します。<br>引数あり: 例 `/start map:山頂（Departure） coal:1.5` のように、前回の設定に指定した項目だけを変えてすぐ起動します。 |
| `/stop` | 確認のあと、ゲームサーバーを安全に止めてから EC2 を停止します。 |
| `/restart` | EC2 は起動したまま、ゲームサーバーだけを起動し直します（引数で設定を変えられます）。 |
| `/status` | EC2 とゲームサーバーの状態・マップ・人数・アドレス・稼働時間・自動停止の予定を表示します。 |

起動に成功すると、次のように表示されます（IP は EC2 を起動するたびに変わります）。

```
🟢 Dread Hunger Server Online
Map: 山頂（Departure）
Players: 0 / 8
Address: 203.0.113.10:7777
Uptime: 00:00:12
```

`/start`・`/stop`・`/restart` を使える人は、Lambda の環境変数で決めます。**指定した Discord サーバーのメンバー全員**（`DHSM_ALLOWED_GUILD_IDS`）にするか、**特定のユーザー・ロールだけ**（`DHSM_ALLOWED_USER_IDS`・`DHSM_ALLOWED_ROLE_IDS`）にできます。ほかの Discord サーバーや DM からは使えません。`/status` は既定で誰でも使えます。同時に2つの操作（`/start` を2人が同時に押した場合など）が来た場合は、後から来たほうを「Server operation already in progress」で断ります。

誰が・いつ・何をしたか（結果と、起動した設定）は、Discord のログ用チャンネル（6.）と CloudWatch Logs（`"audit":true` の行）に記録されます。

### 1. Discord のアプリケーションを作る

1. [Discord Developer Portal](https://discord.com/developers/applications) で **New Application** を押し、名前（例: `Dread Hunger Server`）を付けて作成します。
2. **General Information** の **Application ID** と **Public Key** を控えます（どちらも秘密情報ではありません）。
3. **Bot** → **Reset Token** で Bot Token を表示してコピーします。**Bot Token は秘密情報です。** コマンドの登録（4.）に1度使うだけなので、メモ帳などに保存せず、Lambda にも設定しません。
4. **OAuth2** → **OAuth2 URL Generator** で `applications.commands` にチェックを入れ、下に出た URL をブラウザーで開いて、Bot を使う Discord サーバーに追加します。
5. Discord の **ユーザー設定** → **詳細設定** → **開発者モード** をオンにします。サーバー名を右クリック →「サーバーIDをコピー」でサーバーIDを控えます。特定の人だけに許可する場合は、その人を右クリック →「ユーザーIDをコピー」でユーザーIDを控えます。ロールで許可する場合は、**サーバー設定** → **ロール** でロールを右クリック →「ロールIDをコピー」します。

### 2. Lambda 用の IAM ロールを作る

1. **IAM → ロール → ロールを作成** で、「信頼されたエンティティタイプ」に **AWS のサービス**、ユースケースに **Lambda** を選びます。
2. 許可ポリシーで **`AWSLambdaBasicExecutionRole`**（ログの書き込み）を選び、名前を例えば `DreadHungerBotRole` にして作成します。
3. 作成したロールの **許可を追加 → インラインポリシーを作成 → JSON** に次を貼り付けます。`<アカウントID>`（12桁）・インスタンスID・関数名（3. で付ける名前）を自分のものに置き換えてください。

   ```json
   {
     "Version": "2012-10-17",
     "Statement": [
       {
         "Sid": "ReadInstanceState",
         "Effect": "Allow",
         "Action": "ec2:DescribeInstances",
         "Resource": "*"
       },
       {
         "Sid": "StartAndStopTheGameServerInstance",
         "Effect": "Allow",
         "Action": ["ec2:StartInstances", "ec2:StopInstances"],
         "Resource": "arn:aws:ec2:ap-northeast-3:<アカウントID>:instance/i-xxxxxxxxxxxxxxxxx"
       },
       {
         "Sid": "TalkToTheHostService",
         "Effect": "Allow",
         "Action": ["ssm:GetParameter", "ssm:PutParameter"],
         "Resource": "arn:aws:ssm:ap-northeast-3:<アカウントID>:parameter/dhsm/default/*"
       },
       {
         "Sid": "RunLongOperationsInTheBackground",
         "Effect": "Allow",
         "Action": "lambda:InvokeFunction",
         "Resource": "arn:aws:lambda:ap-northeast-3:<アカウントID>:function:dread-hunger-bot"
       }
     ]
   }
   ```

   EC2 の起動・停止は上のインスタンスだけ、Parameter Store は `/dhsm/default/` の下だけに限定しています。`lambda:InvokeFunction` は、時間のかかる処理（EC2 の起動待ちなど）を Bot 自身に裏で実行させるためのものです（Discord は3秒以内の応答を求めるため）。

### 3. Lambda 関数を作る

1. [Releases](https://github.com/SotaFurukawa/DreadHungerServerManager/releases) から `DreadHungerServerManager-Bot-lambda-arm64.zip` をダウンロードします（自分でビルドする場合は `scripts\Publish-Bot.ps1`）。**展開せずに** zip のまま使います。
2. **Lambda**（EC2 と同じリージョン。大阪なら右上で「アジアパシフィック (大阪)」）→ **関数の作成** → **一から作成** を選び、次のように設定します。
   - 関数名: `dread-hunger-bot`（2. のポリシーと同じ名前）
   - ランタイム: **Amazon Linux 2023**（「Amazon Linux 2023 で独自のブートストラップを提供する」）
   - アーキテクチャ: **arm64**
   - 実行ロール: **既存のロールを使用する** → `DreadHungerBotRole`
   - 詳細設定: **関数 URL を有効化** にチェック、認証タイプは **NONE**（Discord の署名を Bot が検証し、署名のない要求はすべて拒否します）
3. **コード** → **アップロード元** → **.zip ファイル** で、ダウンロードした zip をアップロードします。
4. **設定** → **一般設定** → **編集** で、メモリを **1024 MB**、タイムアウトを **15 分 0 秒** にします。
5. **設定** → **環境変数** → **編集** で、次を追加します。

   | 名前 | 値 |
   | --- | --- |
   | `DISCORD_APPLICATION_ID` | 1. の Application ID |
   | `DISCORD_PUBLIC_KEY` | 1. の Public Key（64文字） |
   | `DHSM_INSTANCE_ID` | ゲームサーバーの EC2 のインスタンスID（`i-` で始まる） |
   | `DHSM_ALLOWED_GUILD_IDS` | Discord サーバーのID。**そのサーバーのメンバー全員**が `/start` などを使える |
   | `DHSM_ALLOWED_USER_IDS` | （任意）特定の人だけに許可する場合の Discord のユーザーID（複数はカンマ区切り） |
   | `DHSM_ALLOWED_ROLE_IDS` | （任意）許可するロールID。そのロールを持つ人は全員使える |
   | `DHSM_AUDIT_WEBHOOK_URL` | （任意）誰が何をしたかを書き込むログ用チャンネルの Webhook URL（6.） |
   | `DHSM_NOTIFY_WEBHOOK_URL` | （任意）自動停止の予告などを送るチャンネルの Webhook URL（6.） |
   | `DHSM_REGION` | （任意）EC2 のリージョン。省略すると Lambda と同じリージョン |
   | `DHSM_STATUS_PUBLIC` | （任意）`false` にすると `/status` も許可した人だけ |
   | `DHSM_GAME_PORT` | （任意）表示するポート。既定 `7777` |
   | `DHSM_WATCHDOG_STALE_STOP_MINUTES` | （任意）EC2 が動いているのに常駐サービスが応答しない状態がこの分数続いたら EC2 を止める。既定 `30`、`0` で止めない |

   サーバーのメンバー全員に使わせる場合は `DHSM_ALLOWED_GUILD_IDS` だけ、特定の人だけにする場合は `DHSM_ALLOWED_USER_IDS`（か `DHSM_ALLOWED_ROLE_IDS`）だけを設定します。3つとも空だと、誰も `/start` などを使えません（誰でも EC2 を起動できる状態にはしません）。**Bot Token はここに設定しません。**
6. **設定** → **非同期呼び出し** → **編集** で、**再試行 = 0** にします（失敗時に EC2 の起動などが二重に行われないようにするため）。
7. **トリガーを追加** → **EventBridge (CloudWatch Events)** → **新規ルールの作成** で、ルール名を `dread-hunger-bot-watchdog`、ルールタイプを **スケジュール式**、式を `rate(5 minutes)` にして追加します。5分ごとに、自動停止の予告・停止の通知と、常駐サービスが応答しない EC2 の停止を行います（Bot の初回応答が遅くなるのを防ぐ役目もあります）。
8. 関数の概要に表示される **関数 URL**（`https://….lambda-url.ap-northeast-3.on.aws/`）をコピーします。

### 4. Discord に接続先とコマンドを登録する

1. Developer Portal の **General Information** → **Interactions Endpoint URL** に 3. の関数 URL を貼り付けて **Save Changes** を押します。Discord が署名付きの確認を送り、成功すると保存されます。
2. このリポジトリのフォルダーで次を実行し、コマンドを登録します。Bot Token を聞かれたら 1. でコピーしたものを貼り付けます（画面には表示されず、どこにも保存されません）。

   ```powershell
   powershell -ExecutionPolicy Bypass -File scripts\Register-DiscordCommands.ps1 -ApplicationId <Application ID> -GuildId <サーバーID>
   ```

   「登録しました: /start /restart /stop /status」と出れば完了です。コマンドの定義（`deploy\discord-commands.json`）を変えたときだけ、もう一度実行します。

### 5. 動作確認

1. Discord で `/status` を実行し、「EC2: Stopped（停止中）」と表示されることを確認します。
2. `/start` → 設定パネルで「この設定で起動」を押します。チャンネルに進み具合（EC2 Starting… → Windows とゲームサーバーの起動待ち）が表示され、数分後に「Dread Hunger Server Online」とアドレスが表示されます。
3. `/stop` → 「停止する」で、ゲームサーバーと EC2 が停止することを確認します。

### 6. 通知・ログ用の Webhook（任意）

チャンネルの **設定（歯車）** → **連携サービス** → **ウェブフック** → **新しいウェブフック** で作成し、**ウェブフックURLをコピー** して Lambda の環境変数に設定します。ウェブフック URL を知っている人は誰でもそのチャンネルに投稿できるため、Lambda の環境変数以外には書かないでください。

| 環境変数 | 投稿する内容 |
| --- | --- |
| `DHSM_NOTIFY_WEBHOOK_URL` | 自動停止の予告（誰も遊んでいない: 5分前、最大稼働時間: 30分前・10分前・5分前）と、自動停止したこと。遊ぶ人が見るチャンネル向け |
| `DHSM_AUDIT_WEBHOOK_URL` | 操作の記録。例: `📝 @Alice（Alice）が /start を実行 → ✅ 成功` と起動した設定。管理者だけが見られるチャンネル（例: `#bot-log`）を作って使うのがおすすめです |

ログ用チャンネルでは、使った人をメンション形式で表示します（通知は飛びません）。表示名を変えても誰か分かります。2つに同じ URL を設定すれば、1つのチャンネルにまとめて投稿します。

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
│   ├─ Services\Cloud  ICloudInstanceService（EC2などの操作の抽象）, FakeCloudInstanceService,
│   │                  IServerControlStore（要求・状態の受け渡し）, ServerControlClient, ServerHostCoordinator
│   └─ Interop         Win32 API（プロセス一覧・コンソール制御・ファイルID）
├─ src\DreadHungerServerManager.Aws      AWS SDK を使う実装（net10.0）。AWS 固有の型はここから外に出さない
│                      AwsEc2InstanceService（ICloudInstanceService の実装）, SsmServerControlStore,
│                      AwsClientFactory, Ec2InstanceMetadata
├─ src\DreadHungerServerManager.Host     EC2 用の常駐サービス（Windows サービス、net10.0-windows）
│                      GameServerHostService, HostEnvironment, ServiceInstaller（install / uninstall）
├─ src\DreadHungerServerManager.Bot      Discord Bot の手順（net10.0。Discord にも AWS SDK にも依存しない）
│                      BotWorkflows（起動・停止・再起動・状態・見張り）, LaunchPanel（設定パネル）,
│                      BotAuthorizer（許可リスト）, BotMessages, BotRedaction（EC2 の場所を伏せる）
├─ src\DreadHungerServerManager.Bot.Lambda  AWS Lambda の入口（linux-arm64、カスタムランタイム）
│                      Function, InteractionHandler, Worker, Discord（署名の検証・応答・Webhook）
├─ src\DreadHungerServerManager.App      WinUI 3 のアプリ
│   ├─ ViewModels      ServerViewModel, CloudViewModel ほか（MVVM。WinUIの型に依存しない）
│   ├─ Views           MainWindow, ServerPage, CloudPage, SettingsPage, AboutPage
│   └─ Services        ダイアログ・通知・ファイル選択（IUserInteraction の実装）、単一インスタンス
├─ tests\DreadHungerServerManager.Core.Tests   xUnit（単体テスト＋疑似サーバーを使った結合テスト）
├─ tests\DreadHungerServerManager.Aws.Tests    xUnit（EC2 の応答・エラーの変換。実 AWS には接続しない）
├─ tests\DreadHungerServerManager.Bot.Tests    xUnit（疑似 EC2 と疑似の常駐サービスで Bot の手順、署名・権限・パネル）
├─ deploy\discord-commands.json         Discord に登録するコマンドの定義
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
- EC2の起動/停止・パブリックIP取得は `ICloudInstanceService` として Core に抽象を用意しました（AWS SDK を使う実装は `DreadHungerServerManager.Aws` の `AwsEc2InstanceService`）。`FakeCloudInstanceService` は実AWSを使わずに「停止中→起動処理中→稼働中」「稼働中→停止処理中→停止中」の遷移と、起動のたびにIPが変わる動きを再現します。AWS 連携の設定は `settings.json` の `cloud` 節に保存します（秘密情報は保存しません）
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

AWS を使わずに「AWS / Cloud」ページを確認するには、起動引数 `--fake-cloud` を付けて起動します。実際の AWS の代わりに疑似 EC2（`FakeCloudInstanceService`。起動15秒・停止10秒、起動のたびに IP が変わる）を操作します。

## トラブルシューティング

- **「起動完了のログを検出できていません」と出る**: 設定ページの「使用するファイル」が実際のログファイルを指しているか確認してください。
- **停止できない／強制終了できない**: サーバーが管理者として起動されている場合、このアプリも管理者として実行する必要があります。
- **「AWS の認証情報が見つかりません」**: `%USERPROFILE%\.aws\credentials` のプロファイル名と、「接続設定」のプロファイル名が一致しているか確認してください。
- **「AWS の認証に失敗しました」**: アクセスキーが無効（削除・無効化済み）か、PC の時刻が大きくずれています。
- **「権限がありません」**: IAM ポリシーのリージョン・アカウントID・インスタンスIDが正しいか確認してください。EBS ボリュームをカスタマー管理の KMS キーで暗号化している場合は、起動に `kms:CreateGrant` も必要です。
- **「インスタンスが見つかりません」**: リージョンとインスタンスIDの組み合わせを確認してください（大阪は `ap-northeast-3`、東京は `ap-northeast-1`）。
- **「EC2 上のゲームサーバー」が「常駐サービス未確認」「応答なし」のまま**: EC2 に常駐サービスが入っているか、EC2 に IAM ロールが割り当てられているかを確認してください。常駐サービスのログ（`C:\ProgramData\DreadHungerServerManager\logs`）に原因が出ます。
- **「Parameter Store の … を使う権限がありません」**: 自宅の PC の IAM ユーザー、または EC2 の IAM ロールのポリシーに `ssm:GetParameter`・`ssm:PutParameter`（`/dhsm/default/*`）があるか確認してください。
- **Discord で「Interactions Endpoint URL を検証できませんでした」と出る**: Lambda の環境変数 `DISCORD_PUBLIC_KEY` が Developer Portal の Public Key と一致しているか確認してください。Lambda の **モニタリング** → **CloudWatch Logs を表示** に「Bot の設定に誤りがあります」と原因が出ます。
- **Discord で「アプリケーションが応答しませんでした」と出る**: Lambda が長く使われていなかった直後は、最初の応答が Discord の制限（3秒）に間に合わないことがあります。もう一度実行してください。続く場合は、「Discord Bot」の手順 3. の 7.（5分ごとのトリガー）とメモリ（1024 MB）を確認してください。
- **Discord で「このコマンドを使う権限がありません」**: Lambda の `DHSM_ALLOWED_GUILD_IDS` に Discord サーバーのIDが入っているか（特定の人だけにしている場合は `DHSM_ALLOWED_USER_IDS`・`DHSM_ALLOWED_ROLE_IDS` にその人が入っているか）確認してください。
- **Discord で「Bot にサーバーを操作する権限がない」「Bot の接続先サーバーの設定に問題がある」**: Lambda の IAM ロールのポリシー、`DHSM_INSTANCE_ID`・`DHSM_REGION` を確認してください。詳しい原因は CloudWatch Logs の `cloud operation failed` の行に出ます。
- **Discord にコマンドが出てこない**: `scripts\Register-DiscordCommands.ps1` のサーバーIDを確認し、Discord を再読み込み（Ctrl+R）してください。
- **外部から接続できない**: Windows ファイアウォールと（EC2の場合は）セキュリティグループで、ゲームポート（既定 UDP 7777。`-port` で変えた場合はその番号）の受信を許可してください。
