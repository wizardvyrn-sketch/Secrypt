<#
.SYNOPSIS
  Secrypt を配布フォルダから PC にインストールする（ビルド・.NET は不要）。

.DESCRIPTION
  配布フォルダ（publish.ps1 が作る Secrypt-<版>-win-x64）の直下に置いて使う。
  1. 動作要件の確認（OS・64 ビット・フォント）
  2. app フォルダを %LOCALAPPDATA%\Programs\Secrypt へコピー
     （旧名 NewPWManageTool のインストールとショートカットがあれば片付ける）
  3. スタートメニューのショートカット（任意でデスクトップ・PATH）
  4. 診断ツール pwm で TPM を確認

  管理者権限は不要。パスワードのデータ（%LOCALAPPDATA%\NewPWManageTool。改名後も旧名のまま）には一切触れない。

.PARAMETER InstallDir
  インストール先。既定は %LOCALAPPDATA%\Programs\Secrypt。
.PARAMETER Uninstall
  アプリを削除する（ショートカット・PATH の登録も外す）。パスワードのデータは削除しない。
.PARAMETER DesktopShortcut
  デスクトップにもショートカットを作る。
.PARAMETER AddToPath
  診断ツール pwm をユーザーの PATH に追加する。
.PARAMETER NoShortcut
  スタートメニューのショートカットを作らない（試験用）。
.PARAMETER Source
  コピー元の app フォルダ。既定はこのスクリプトと同じ場所の app。
.PARAMETER Yes
  確認の質問にすべて「はい」と答える。

.EXAMPLE
  setup.cmd
  setup.cmd -DesktopShortcut -AddToPath
  setup.cmd -Uninstall
#>
[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\Secrypt'),
    [switch]$Uninstall,
    [switch]$DesktopShortcut,
    [switch]$AddToPath,
    [switch]$NoShortcut,
    [string]$Source,
    [switch]$Yes
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
try { [Console]::OutputEncoding = [Text.Encoding]::UTF8 } catch { }

$AppName      = 'Secrypt'
$Marker       = '.secrypt-install'
# 旧名。データの場所はこの名前のまま。旧名のインストールは目印があるものだけ片付ける
$LegacyName   = 'NewPWManageTool'
$LegacyMarker = '.newpwmanagetool-install'
$StartMenuLnk = Join-Path ([Environment]::GetFolderPath('Programs')) "$AppName.lnk"
$DesktopLnk   = Join-Path ([Environment]::GetFolderPath('Desktop')) "$AppName.lnk"
# Windows PowerShell 5.1 では param の既定値で $PSScriptRoot が空になることがあるため、ここで決める
if (-not $Source) { $Source = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'app' }
$InstallDir   = [IO.Path]::GetFullPath($InstallDir).TrimEnd('\')
$DataDir      = if ($env:PWM_DATA) { $env:PWM_DATA } else { Join-Path $env:LOCALAPPDATA $LegacyName }

function Write-Step([string]$text) { Write-Host ''; Write-Host "== $text" -ForegroundColor Cyan }
function Write-Ok([string]$text)   { Write-Host "  [OK] $text" -ForegroundColor Green }
function Write-Note([string]$text) { Write-Host "  [!]  $text" -ForegroundColor Yellow }
function Write-Info([string]$text) { Write-Host "       $text" }
function Fail([string]$text)       { Write-Host "  [NG] $text" -ForegroundColor Red; exit 1 }

function Confirm-Action([string]$question) {
    if ($Yes) { return $true }
    $answer = Read-Host "  $question [y/N]"
    return $answer -match '^(y|yes|はい)$'
}

# 誤って別のフォルダを消さないよう、インストール先は「空」か「このスクリプトが作ったもの（目印あり）」に限る
function Assert-SafeInstallDir([string]$dir) {
    $full = [IO.Path]::GetFullPath($dir).TrimEnd('\')
    $data = [IO.Path]::GetFullPath($DataDir).TrimEnd('\')
    if ($full -eq $data -or $data.StartsWith("$full\", [StringComparison]::OrdinalIgnoreCase) -or $full.StartsWith("$data\", [StringComparison]::OrdinalIgnoreCase)) {
        Fail "インストール先がパスワードのデータの場所（$DataDir）と重なっています。別の場所を指定してください。"
    }
    if (-not (Test-Path $dir)) { return }
    $items = @(Get-ChildItem -LiteralPath $dir -Force)
    if ($items.Count -gt 0 -and -not (Test-Path (Join-Path $dir $Marker))) {
        Fail "インストール先 '$dir' に別のファイルがあります。空のフォルダか、-InstallDir で別の場所を指定してください。"
    }
}

function Remove-FromUserPath([string]$dir) {
    $path = [Environment]::GetEnvironmentVariable('Path', 'User')
    if (-not $path) { return $false }
    $parts = $path.Split(';') | Where-Object { $_ -and ($_.TrimEnd('\') -ne $dir.TrimEnd('\')) }
    $new = ($parts -join ';')
    if ($new -ne $path) { [Environment]::SetEnvironmentVariable('Path', $new, 'User'); return $true }
    return $false
}

function New-Shortcut([string]$lnk, [string]$target, [string]$workDir) {
    $shell = New-Object -ComObject WScript.Shell
    $s = $shell.CreateShortcut($lnk)
    $s.TargetPath = $target
    $s.WorkingDirectory = $workDir
    $s.Description = 'ローカル・パスワードマネージャ'
    $s.Save()
}

function Assert-NotRunning {
    foreach ($name in @($AppName, $LegacyName)) {
        if (Get-Process -Name $name -ErrorAction SilentlyContinue) {
            Fail "$name が起動中です。ロックして閉じてから、もう一度実行してください。"
        }
    }
}

# 旧名（NewPWManageTool）でインストールしたプログラム・ショートカット・PATH を片付ける。データには触れない
function Remove-LegacyInstall {
    $legacyDir = Join-Path $env:LOCALAPPDATA "Programs\$LegacyName"
    if (Test-Path (Join-Path $legacyDir $LegacyMarker)) {
        if (Remove-FromUserPath (Join-Path $legacyDir 'cli')) { Write-Ok "PATH から旧名の pwm を外しました" }
        Remove-Item -LiteralPath $legacyDir -Recurse -Force
        Write-Ok "旧名のプログラムを削除: $legacyDir"
    }
    foreach ($folder in @('Programs', 'Desktop')) {
        $lnk = Join-Path ([Environment]::GetFolderPath($folder)) "$LegacyName.lnk"
        if (Test-Path $lnk) { Remove-Item -LiteralPath $lnk -Force; Write-Ok "旧名のショートカットを削除: $lnk" }
    }
}

# ---------------------------------------------------------------- アンインストール
if ($Uninstall) {
    Write-Step "$AppName をアンインストールします"
    Assert-NotRunning
    Assert-SafeInstallDir $InstallDir
    foreach ($lnk in @($StartMenuLnk, $DesktopLnk)) {
        if (Test-Path $lnk) { Remove-Item -LiteralPath $lnk -Force; Write-Ok "ショートカットを削除: $lnk" }
    }
    if (Remove-FromUserPath (Join-Path $InstallDir 'cli')) { Write-Ok 'PATH から pwm を外しました' }
    if (Test-Path $InstallDir) {
        Remove-Item -LiteralPath $InstallDir -Recurse -Force
        Write-Ok "プログラムを削除: $InstallDir"
    } else {
        Write-Note "インストール先が見つかりません: $InstallDir"
    }
    Write-Host ''
    Write-Note "パスワードのデータは残しています: $DataDir"
    Write-Info '完全に消す場合は、鍵ファイル・復旧コードが不要になったことを確かめてから、手動で削除してください。'
    # uninstall.cmd が一時フォルダに置いた自分のコピーを片付ける
    if ((Split-Path -Leaf $PSCommandPath) -eq 'pwm-uninstall.ps1') { Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue }
    exit 0
}

# ---------------------------------------------------------------- 1. 動作要件
Write-Step '1/4 動作要件の確認'
$os = [Environment]::OSVersion.Version
if ($os.Major -lt 10 -or $os.Build -lt 19041) { Fail "Windows 10 バージョン 2004（ビルド 19041）以降が必要です（この PC: $os）。" }
Write-Ok "Windows ビルド $($os.Build)"
if (-not [Environment]::Is64BitOperatingSystem) { Fail '64 ビット版の Windows が必要です。' }
Write-Ok '64 ビット'
$fonts = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts' -ErrorAction SilentlyContinue
$fontNames = if ($fonts) { $fonts.PSObject.Properties.Name -join "`n" } else { '' }
foreach ($f in @('BIZ UDPGothic', 'Bahnschrift')) {
    if ($fontNames -match [Regex]::Escape($f)) { Write-Ok "フォント $f" }
    else { Write-Note "フォント $f が見つかりません（代わりに Yu Gothic UI などで表示されます）" }
}
if (-not (Test-Path (Join-Path $Source "$AppName.exe"))) {
    Fail "コピー元が見つかりません: $Source（配布フォルダの中で、展開してから実行してください）"
}
Write-Ok "コピー元: $Source"

# ---------------------------------------------------------------- 2. 配置
Write-Step "2/4 配置: $InstallDir"
Assert-NotRunning
Assert-SafeInstallDir $InstallDir
Remove-LegacyInstall
if (Test-Path (Join-Path $InstallDir $Marker)) {
    $old = Get-Content -LiteralPath (Join-Path $InstallDir $Marker) -ErrorAction SilentlyContinue | Where-Object { $_ -like 'version=*' }
    Write-Info "上書きします（現在: $(if ($old) { $old -replace '^version=' } else { '版不明' })）"
}
if (Test-Path $InstallDir) { Remove-Item -LiteralPath $InstallDir -Recurse -Force }
New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
Copy-Item -Path (Join-Path $Source '*') -Destination $InstallDir -Recurse -Force
# ZIP をダウンロードして展開した場合の「インターネットから来たファイル」の印を外す
Get-ChildItem -LiteralPath $InstallDir -Recurse -File | Unblock-File -ErrorAction SilentlyContinue

# アンインストール用（インストール先から実行できるよう、自分自身と起動用 cmd を置く）
Copy-Item -LiteralPath $PSCommandPath -Destination (Join-Path $InstallDir 'setup.ps1') -Force
# インストール先のフォルダ自体を消すため、cmd は 1 行で読み切らせ、PowerShell は一時フォルダのコピーで動かす
Set-Content -LiteralPath (Join-Path $InstallDir 'uninstall.cmd') -Encoding ASCII -Value @(
    '@echo off',
    'rem Uninstall Secrypt (password data in %LOCALAPPDATA%\NewPWManageTool is kept).',
    'copy /y "%~dp0setup.ps1" "%TEMP%\pwm-uninstall.ps1" >nul',
    'cd /d "%TEMP%"',
    'powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%TEMP%\pwm-uninstall.ps1" -Uninstall -InstallDir "%~dp0." %* & pause & exit /b')

$version = (Get-Item (Join-Path $InstallDir "$AppName.exe")).VersionInfo.ProductVersion
Set-Content -LiteralPath (Join-Path $InstallDir $Marker) -Encoding UTF8 -Value @(
    "installed=$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')", "version=$version", "source=$Source")
Write-Ok "プログラムをコピーしました（版 $version）"

# ---------------------------------------------------------------- 3. ショートカット
Write-Step '3/4 ショートカット'
$exe = Join-Path $InstallDir "$AppName.exe"
if (-not $NoShortcut) { New-Shortcut $StartMenuLnk $exe $InstallDir; Write-Ok "スタートメニュー: $AppName" }
else { Write-Info 'スタートメニューのショートカットは作りません（-NoShortcut）' }
if ($DesktopShortcut) { New-Shortcut $DesktopLnk $exe $InstallDir; Write-Ok 'デスクトップにショートカット' }
if ($AddToPath) {
    $cliDir = Join-Path $InstallDir 'cli'
    Remove-FromUserPath $cliDir | Out-Null
    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    [Environment]::SetEnvironmentVariable('Path', ((@($userPath, $cliDir) | Where-Object { $_ }) -join ';'), 'User')
    Write-Ok 'PATH に pwm を追加しました（新しく開いたターミナルから使えます）'
}

# ---------------------------------------------------------------- 4. 確認
Write-Step '4/4 確認'
$pwm = Join-Path $InstallDir 'cli\pwm.exe'
$tpm = (& $pwm diag tpm 2>&1 | Out-String).Trim()
if ($tpm -match '利用できます') { Write-Ok $tpm } else { Write-Note $tpm; Write-Info 'TPM が使えない場合は、初期設定で「TPM を使わずに進む」を選んでください。' }

Write-Host ''
Write-Host "インストールが完了しました。" -ForegroundColor Green
if ($NoShortcut) { Write-Info "起動: $exe" } else { Write-Info "起動: スタートメニューの「$AppName」、または $exe" }
Write-Info "データの保存先: $DataDir（vault.pwmv・設定・ログ）"
Write-Info "診断: `"$pwm`" diag vault / diag tpm / logs"
Write-Info "削除: $(Join-Path $InstallDir 'uninstall.cmd')（データは残ります）"
exit 0
