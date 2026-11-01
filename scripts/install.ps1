<#
.SYNOPSIS
  Secrypt をソースからビルドして、この PC にインストールする（Windows 10 2004 以降 / Windows 11、x64）。

.DESCRIPTION
  publish.ps1 で一時フォルダに配布フォルダを作り、その setup.ps1 でインストールする。
  1. .NET 10 SDK の確認。無ければ winget で導入（確認してから）
  2. Release ビルド（自己完結: 実行先に .NET ランタイムは不要）
  3. 動作要件の確認（OS・64 ビット・フォント）
  4. %LOCALAPPDATA%\Programs\Secrypt へ配置、スタートメニューにショートカット
  5. 診断ツール pwm で TPM を確認

  ほかの PC に配る場合は scripts\publish.cmd で配布フォルダを作り、配布先でその setup.cmd を実行する。
  管理者権限は不要（ユーザー単位のインストール）。
  パスワードのデータ（%LOCALAPPDATA%\NewPWManageTool の vault.pwmv・設定・ログ）には一切触れない。

.PARAMETER InstallDir
  インストール先。既定は %LOCALAPPDATA%\Programs\Secrypt。
.PARAMETER Uninstall
  アプリを削除する（ショートカット・PATH の登録も外す）。パスワードのデータは削除しない。
.PARAMETER DesktopShortcut
  デスクトップにもショートカットを作る。
.PARAMETER AddToPath
  診断ツール pwm をどこからでも使えるよう、ユーザーの PATH に追加する。
.PARAMETER NoShortcut
  スタートメニューのショートカットを作らない（試験用）。
.PARAMETER RunTests
  ビルド前に自動テスト（実機 TPM のテストを除く）を実行する。
.PARAMETER Yes
  確認の質問にすべて「はい」と答える（.NET SDK の導入など）。

.EXAMPLE
  scripts\install.cmd
  scripts\install.cmd -DesktopShortcut -AddToPath
  scripts\install.cmd -Uninstall
#>
[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\Secrypt'),
    [switch]$Uninstall,
    [switch]$DesktopShortcut,
    [switch]$AddToPath,
    [switch]$NoShortcut,
    [switch]$RunTests,
    [switch]$Yes
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
try { [Console]::OutputEncoding = [Text.Encoding]::UTF8 } catch { }

$setup = Join-Path $PSScriptRoot 'dist\setup.ps1'
$common = @{ InstallDir = $InstallDir }
if ($Yes) { $common.Yes = $true }

if ($Uninstall) {
    & $setup @common -Uninstall
    exit $LASTEXITCODE
}

if (Get-Process -Name 'Secrypt', 'NewPWManageTool' -ErrorAction SilentlyContinue) {
    Write-Host '  [NG] Secrypt（または旧名 NewPWManageTool）が起動中です。ロックして閉じてから、もう一度実行してください。' -ForegroundColor Red
    exit 1
}

$staging = Join-Path ([IO.Path]::GetTempPath()) ("pwm-install-" + [Guid]::NewGuid().ToString('N'))
try {
    $pub = @{ PackageDir = $staging; NoZip = $true }
    if ($RunTests) { $pub.RunTests = $true }
    if ($Yes) { $pub.Yes = $true }
    & (Join-Path $PSScriptRoot 'publish.ps1') @pub
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    $inst = $common.Clone()
    if ($DesktopShortcut) { $inst.DesktopShortcut = $true }
    if ($AddToPath) { $inst.AddToPath = $true }
    if ($NoShortcut) { $inst.NoShortcut = $true }
    & (Join-Path $staging 'setup.ps1') @inst
    $rc = $LASTEXITCODE
}
finally {
    if (Test-Path $staging) { Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue }
}
exit $rc
