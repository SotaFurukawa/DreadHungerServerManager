<#
.SYNOPSIS
    インストーラー（DreadHungerServerManager-Setup.exe）を作成します。

.DESCRIPTION
    scripts\Publish.ps1 で配布フォルダーを作ってから、Inno Setup 6 でインストーラーにまとめます。
    出力先: dist\DreadHungerServerManager-Setup.exe
    Inno Setup は https://jrsoftware.org/isinfo.php から、または
    winget install JRSoftware.InnoSetup で入れてください。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\Build-Installer.ps1
#>
param(
    # Publish.ps1 を実行せず、既にある dist\DreadHungerServerManager-win-x64 を使う
    [switch]$SkipPublish,

    # ISCC.exe の場所（省略時は PATH と標準のインストール先から探す）
    [string]$Iscc
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
[xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
$group = $props.Project.PropertyGroup
$version = "$($group.VersionPrefix)-$($group.VersionSuffix)"
$fileVersion = "$($group.FileVersion)"
$source = Join-Path $root 'dist\DreadHungerServerManager-win-x64'
$outputDir = Join-Path $root 'dist'

if (-not $Iscc) {
    $candidates = @(
        (Get-Command ISCC.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    )
    $Iscc = $candidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
}
if (-not $Iscc -or -not (Test-Path $Iscc)) {
    throw 'Inno Setup 6 (ISCC.exe) が見つかりません。winget install JRSoftware.InnoSetup で入れるか、-Iscc で場所を指定してください。'
}

if (-not $SkipPublish) {
    & (Join-Path $PSScriptRoot 'Publish.ps1') -Architecture x64
}
if (-not (Test-Path (Join-Path $source 'DreadHungerServerManager.exe'))) {
    throw "配布フォルダーがありません: $source"
}

& $Iscc /Q "/DAppVersion=$version" "/DAppFileVersion=$fileVersion" "/DSourceDir=$source" "/DOutputDir=$outputDir" (Join-Path $root 'installer\DreadHungerServerManager.iss')
if ($LASTEXITCODE -ne 0) {
    throw "インストーラーの作成に失敗しました（終了コード $LASTEXITCODE）。"
}

$setup = Join-Path $outputDir 'DreadHungerServerManager-Setup.exe'
$sizeMb = [math]::Round((Get-Item $setup).Length / 1MB, 1)
Write-Host "バージョン    : $version"
Write-Host "インストーラー: $setup（$sizeMb MB）"
