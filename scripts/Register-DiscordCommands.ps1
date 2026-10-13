<#
.SYNOPSIS
    Discord のサーバー（ギルド）に /start・/stop・/restart・/status を登録します。

.DESCRIPTION
    定義は deploy\discord-commands.json です。コマンドを追加・変更したときだけ実行します（Bot の運用中は不要）。
    Bot Token は環境変数 DISCORD_BOT_TOKEN から読みます。なければ入力を求めます（画面に表示しません）。
    Bot Token はファイルにも Lambda にも保存しません。登録にだけ使います。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\Register-DiscordCommands.ps1 -ApplicationId 1300000000000000000 -GuildId 1200000000000000000
#>
param(
    # Discord Developer Portal の「General Information」→「Application ID」
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d{17,20}$')]
    [string]$ApplicationId,

    # コマンドを使う Discord サーバーのID（Discord の開発者モードで、サーバー名を右クリック →「サーバーIDをコピー」）
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d{17,20}$')]
    [string]$GuildId
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$definition = Join-Path $root 'deploy\discord-commands.json'
$json = [System.IO.File]::ReadAllText($definition, [System.Text.Encoding]::UTF8)

$token = $env:DISCORD_BOT_TOKEN
if ([string]::IsNullOrWhiteSpace($token)) {
    $secure = Read-Host 'Bot Token を貼り付けてください（表示されません）' -AsSecureString
    $pointer = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try {
        $token = [System.Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
    }
    finally {
        [System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
    }
}

if ([string]::IsNullOrWhiteSpace($token)) {
    throw 'Bot Token がありません。'
}

[System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12
$uri = "https://discord.com/api/v10/applications/$ApplicationId/guilds/$GuildId/commands"
try {
    # PUT は一覧ごと置き換える（定義から消したコマンドは Discord からも消える）。
    $result = Invoke-RestMethod -Method Put -Uri $uri `
        -Headers @{ Authorization = "Bot $($token.Trim())"; 'User-Agent' = 'DreadHungerServerManager (https://github.com/SotaFurukawa/DreadHungerServerManager, 1.0)' } `
        -ContentType 'application/json; charset=utf-8' `
        -Body ([System.Text.Encoding]::UTF8.GetBytes($json))
}
catch {
    $status = $_.Exception.Response.StatusCode.value__
    switch ($status) {
        401 { throw 'Bot Token が正しくありません（401）。Developer Portal の「Bot」→「Reset Token」で発行し直したものを使ってください。' }
        403 { throw 'このサーバーにコマンドを登録する権限がありません（403）。Bot を applications.commands の権限付きでサーバーに招待したか確認してください。' }
        404 { throw 'Application ID かサーバーIDが正しくありません（404）。' }
        default { throw "コマンドの登録に失敗しました（$status）: $($_.Exception.Message)" }
    }
}
finally {
    $token = $null
}

Write-Host ("登録しました: {0}" -f (($result | ForEach-Object { '/' + $_.name }) -join ' '))
