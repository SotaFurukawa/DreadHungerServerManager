<#
.SYNOPSIS
    Discord Bot（AWS Lambda 用）の zip を作成します。

.DESCRIPTION
    出力先: dist\DreadHungerServerManager-Bot-lambda-<arch>.zip
    Lambda のランタイム「Amazon Linux 2023（provided.al2023）」・アーキテクチャ arm64 にそのままアップロードできます。
    .NET を同梱した自己完結版です（Lambda 側に .NET は不要）。

    Lambda は zip 内の bootstrap に実行権限が必要です。Windows で作った zip には Unix の権限が入らないため、
    各ファイルに Unix の権限（bootstrap は 755、それ以外は 644）を付け、作成元を Unix として記録します。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\Publish-Bot.ps1
#>
param(
    [ValidateSet('arm64', 'x64')]
    [string]$Architecture = 'arm64'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$rid = "linux-$Architecture"
$output = Join-Path $root "dist\bot-lambda-$rid"
$zip = Join-Path $root "dist\DreadHungerServerManager-Bot-lambda-$Architecture.zip"
$project = Join-Path $root 'src\DreadHungerServerManager.Bot.Lambda\DreadHungerServerManager.Bot.Lambda.csproj'

if (Test-Path $output) {
    Remove-Item $output -Recurse -Force
}

# ReadyToRun（事前コンパイル）で起動を速くする。Discord は3秒以内の応答を求めるため、Lambda の初回起動（コールドスタート）を短くしたい。
dotnet publish $project -c Release -r $rid --self-contained true -p:DebugType=none -p:PublishReadyToRun=true -o $output
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish に失敗しました（終了コード $LASTEXITCODE）。"
}

if (-not (Test-Path (Join-Path $output 'bootstrap'))) {
    throw "bootstrap が出力されていません: $output"
}

Add-Type -AssemblyName System.IO.Compression
if (Test-Path $zip) {
    Remove-Item $zip
}

$stream = [System.IO.File]::Open($zip, [System.IO.FileMode]::CreateNew)
$archive = New-Object System.IO.Compression.ZipArchive($stream, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in Get-ChildItem $output -File -Recurse) {
        $name = $file.FullName.Substring($output.Length + 1).Replace('\', '/')
        $entry = $archive.CreateEntry($name, [System.IO.Compression.CompressionLevel]::Optimal)
        # 上位16ビットが Unix のファイル種別と権限（0100755 / 0100644）。
        $mode = if ($name -eq 'bootstrap') { 0x81ED } else { 0x81A4 }
        $entry.ExternalAttributes = $mode -shl 16
        $entry.LastWriteTime = $file.LastWriteTime
        $source = $file.OpenRead()
        $target = $entry.Open()
        try {
            $source.CopyTo($target)
        }
        finally {
            $target.Dispose()
            $source.Dispose()
        }
    }
}
finally {
    $archive.Dispose()
    $stream.Dispose()
}

# 中央ディレクトリの「作成元」を Unix（3）にする。Windows のままだと Lambda が権限を読まず、bootstrap を実行できない。
$bytes = [System.IO.File]::ReadAllBytes($zip)
$eocd = -1
for ($i = $bytes.Length - 22; $i -ge 0; $i--) {
    if ($bytes[$i] -eq 0x50 -and $bytes[$i + 1] -eq 0x4B -and $bytes[$i + 2] -eq 0x05 -and $bytes[$i + 3] -eq 0x06) {
        $eocd = $i
        break
    }
}
if ($eocd -lt 0) {
    throw 'zip の終端が見つかりません。'
}

$count = [BitConverter]::ToUInt16($bytes, $eocd + 10)
$offset = [int][BitConverter]::ToUInt32($bytes, $eocd + 16)
for ($n = 0; $n -lt $count; $n++) {
    if ([BitConverter]::ToUInt32($bytes, $offset) -ne 0x02014B50) {
        throw "zip の中央ディレクトリを読めません（$n 件目）。"
    }

    $bytes[$offset + 5] = 3
    $nameLength = [BitConverter]::ToUInt16($bytes, $offset + 28)
    $extraLength = [BitConverter]::ToUInt16($bytes, $offset + 30)
    $commentLength = [BitConverter]::ToUInt16($bytes, $offset + 32)
    $offset += 46 + $nameLength + $extraLength + $commentLength
}
[System.IO.File]::WriteAllBytes($zip, $bytes)

$size = (Get-Item $zip).Length / 1MB
Write-Host ("作成しました: {0}（{1:N1} MB）" -f $zip, $size)
if ($size -ge 50) {
    Write-Warning 'Lambda のコンソールから直接アップロードできるのは 50 MB までです。S3 経由でアップロードしてください。'
}
