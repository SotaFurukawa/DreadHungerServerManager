<#
.SYNOPSIS
    配布用のフォルダーとzipを作成します（.NET と Windows App SDK を同梱した自己完結版）。

.DESCRIPTION
    出力先: dist\DreadHungerServerManager-win-<arch>\
            dist\DreadHungerServerManager-<version>-win-<arch>.zip
    出力フォルダーをそのままサーバー（Windows Server 2019 など）へコピーすれば動きます。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\Publish.ps1
    powershell -ExecutionPolicy Bypass -File scripts\Publish.ps1 -CreateDesktopShortcut
#>
param(
    [ValidateSet('x64', 'arm64')]
    [string]$Architecture = 'x64',

    # デスクトップに「Dread Hunger Server Manager」のショートカットを作る
    [switch]$CreateDesktopShortcut
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
[xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
$group = $props.Project.PropertyGroup
$version = "$($group.VersionPrefix)-$($group.VersionSuffix)"
$rid = "win-$Architecture"
$platform = if ($Architecture -eq 'x64') { 'x64' } else { 'ARM64' }
$output = Join-Path $root "dist\DreadHungerServerManager-$rid"
$zip = Join-Path $root "dist\DreadHungerServerManager-$version-$rid.zip"
$project = Join-Path $root 'src\DreadHungerServerManager.App\DreadHungerServerManager.App.csproj'

if (Test-Path $output) {
    Remove-Item $output -Recurse -Force
}

dotnet publish $project -c Release -r $rid -p:Platform=$platform --self-contained true -o $output
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish に失敗しました（終了コード $LASTEXITCODE）。"
}

Get-ChildItem $output -Filter *.pdb | Remove-Item
if (Test-Path $zip) {
    Remove-Item $zip
}
Compress-Archive -Path (Join-Path $output '*') -DestinationPath $zip

$exe = Join-Path $output 'DreadHungerServerManager.exe'
if ($CreateDesktopShortcut) {
    $shortcutPath = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Dread Hunger Server Manager.lnk'
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $exe
    $shortcut.WorkingDirectory = $output
    $shortcut.IconLocation = "$exe,0"
    $shortcut.Description = 'Dread Hunger Dedicated Server を起動・管理します'
    $shortcut.Save()
    Write-Host "ショートカット: $shortcutPath"
}

$sizeMb = [math]::Round(((Get-ChildItem $output -Recurse | Measure-Object Length -Sum).Sum / 1MB), 1)
Write-Host "バージョン: $version"
Write-Host "出力先    : $output（$sizeMb MB）"
Write-Host "zip       : $zip"