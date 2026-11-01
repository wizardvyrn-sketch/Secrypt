<#
.SYNOPSIS
  ほかの PC に配るための配布フォルダ（と ZIP）を作る。

.DESCRIPTION
  publish\Secrypt-<版>-win-x64\ に次を作る。
    setup.cmd / setup.ps1   インストール・アンインストール（配布先ではビルド・.NET 不要）
    app\                    アプリ本体（自己完結・win-x64）、app\cli\pwm.exe（診断ツール）
    manual.html             操作手順書（画面写真を埋め込んだ 1 ファイル）
    README.txt / LICENSE.txt / THIRD-PARTY-NOTICES.md / SHA256SUMS.txt
  さらに同じ名前の ZIP を作る（-NoZip で省略）。

  ビルドには .NET 10 SDK が必要。無ければ winget で導入する（確認してから）。

.PARAMETER OutDir
  出力先。既定はリポジトリ直下の publish。
.PARAMETER PackageDir
  配布フォルダの場所を直接指定する（install.ps1 が一時フォルダに作るときに使う）。
.PARAMETER NoZip
  ZIP を作らない。
.PARAMETER RunTests
  ビルド前に自動テスト（実機 TPM のテストを除く）を実行する。
.PARAMETER Yes
  確認の質問にすべて「はい」と答える（.NET SDK の導入など）。

.EXAMPLE
  scripts\publish.cmd
  scripts\publish.cmd -RunTests
#>
[CmdletBinding()]
param(
    [string]$OutDir,
    [string]$PackageDir,
    [switch]$NoZip,
    [switch]$RunTests,
    [switch]$Yes
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
try { [Console]::OutputEncoding = [Text.Encoding]::UTF8 } catch { }

$AppName     = 'Secrypt'
$RequiredSdk = 10
$RepoRoot    = Split-Path -Parent $PSScriptRoot
$PkgMarker   = '.secrypt-package'

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

if (-not (Test-Path (Join-Path $RepoRoot 'PwManager.slnx'))) {
    Fail "ソースが見つかりません。このスクリプトはリポジトリの scripts フォルダから実行してください（$RepoRoot）。"
}
[xml]$props = Get-Content -LiteralPath (Join-Path $RepoRoot 'Directory.Build.props') -Encoding UTF8
# パッケージ名・README には表示用の版（1.00A 形式）を使う
$Version = @($props.Project.PropertyGroup | ForEach-Object { $_.DisplayVersion } | Where-Object { $_ })[0]
if (-not $Version) { $Version = '0.00A' }
$PkgName = "$AppName-$Version-win-x64"
if (-not $OutDir) { $OutDir = Join-Path $RepoRoot 'publish' }
if (-not $PackageDir) { $PackageDir = Join-Path $OutDir $PkgName }
$PackageDir = [IO.Path]::GetFullPath($PackageDir).TrimEnd('\')

# ---------------------------------------------------------------- 1. .NET SDK
Write-Step "1/4 .NET $RequiredSdk SDK の確認（ビルドに使用）"
function Get-SdkVersions {
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnet) { return @() }
    return @(& dotnet --list-sdks 2>$null | ForEach-Object { ($_ -split ' ')[0] })
}
function Test-Sdk { return [bool](Get-SdkVersions | Where-Object { $_ -match "^$RequiredSdk\." }) }

if (-not (Test-Sdk)) {
    Write-Note ".NET $RequiredSdk SDK がありません。"
    if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
        Fail ".NET $RequiredSdk SDK を https://dotnet.microsoft.com/download からインストールしてから、もう一度実行してください。"
    }
    if (-not (Confirm-Action "winget で Microsoft.DotNet.SDK.$RequiredSdk をインストールしますか？")) { Fail '中止しました。' }
    & winget install --id "Microsoft.DotNet.SDK.$RequiredSdk" --exact --source winget --accept-package-agreements --accept-source-agreements
    # 新しく入った dotnet を、この PowerShell でも使えるようにする
    $env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')
    if (-not (Test-Sdk)) { Fail '.NET SDK を確認できませんでした。PowerShell を開き直して、もう一度実行してください。' }
}
Write-Ok (".NET SDK " + ((Get-SdkVersions | Where-Object { $_ -match "^$RequiredSdk\." }) -join ', '))

# ---------------------------------------------------------------- 2. ビルド
Write-Step "2/4 ビルド（Release・自己完結・win-x64）版 $Version"
if (Test-Path $PackageDir) {
    $items = @(Get-ChildItem -LiteralPath $PackageDir -Force)
    if ($items.Count -gt 0 -and -not (Test-Path (Join-Path $PackageDir $PkgMarker))) {
        Fail "出力先 '$PackageDir' に別のファイルがあります。空のフォルダを指定してください。"
    }
    Remove-Item -LiteralPath $PackageDir -Recurse -Force
}
New-Item -ItemType Directory -Path $PackageDir -Force | Out-Null
Set-Content -LiteralPath (Join-Path $PackageDir $PkgMarker) -Encoding ASCII -Value "version=$Version"

if ($RunTests) {
    Write-Info '自動テストを実行しています（実機 TPM のテストを除く）…'
    & dotnet test (Join-Path $RepoRoot 'PwManager.slnx') -c Release --filter 'Category!=Hardware' --nologo -v q
    if ($LASTEXITCODE -ne 0) { Fail 'テストに失敗しました。中止します。' }
    Write-Ok 'テスト合格'
}

$appDir = Join-Path $PackageDir 'app'
Write-Info 'アプリをビルドしています…（初回は数分かかります）'
& dotnet publish (Join-Path $RepoRoot 'src\PwManager.App\PwManager.App.csproj') -c Release -r win-x64 --self-contained true `
    -p:DebugType=none -p:SatelliteResourceLanguages=ja -o $appDir --nologo -v q -clp:ErrorsOnly
if ($LASTEXITCODE -ne 0) { Fail 'アプリのビルドに失敗しました。' }
Write-Ok 'アプリ'

Write-Info '診断ツール pwm をビルドしています…'
# アプリと同じ .NET を二重に持たないよう、pwm は圧縮した 1 ファイルにする
& dotnet publish (Join-Path $RepoRoot 'src\PwManager.Cli\PwManager.Cli.csproj') -c Release -f net10.0-windows -r win-x64 --self-contained true `
    -p:DebugType=none -p:SatelliteResourceLanguages=ja -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -o (Join-Path $appDir 'cli') --nologo -v q -clp:ErrorsOnly
if ($LASTEXITCODE -ne 0) { Fail '診断ツールのビルドに失敗しました。' }
Write-Ok '診断ツール pwm'

# ---------------------------------------------------------------- 3. 同梱物
Write-Step '3/4 同梱物'
$dist = Join-Path $PSScriptRoot 'dist'
Copy-Item -LiteralPath (Join-Path $dist 'setup.cmd') -Destination $PackageDir
Copy-Item -LiteralPath (Join-Path $dist 'setup.ps1') -Destination $PackageDir
$readme = [IO.File]::ReadAllText((Join-Path $dist 'README.txt'), [Text.Encoding]::UTF8).Replace('{VERSION}', $Version)
[IO.File]::WriteAllText((Join-Path $PackageDir 'README.txt'), $readme, (New-Object Text.UTF8Encoding $true))
Copy-Item -LiteralPath (Join-Path $RepoRoot 'LICENSE') -Destination (Join-Path $PackageDir 'LICENSE.txt')
# 同梱しているライブラリ・フォントのライセンス表記（MIT などは著作権表示の同梱が条件）
Copy-Item -LiteralPath (Join-Path $RepoRoot 'THIRD-PARTY-NOTICES.md') -Destination (Join-Path $PackageDir 'THIRD-PARTY-NOTICES.md')
Write-Ok 'setup.cmd / setup.ps1 / README.txt / LICENSE.txt / THIRD-PARTY-NOTICES.md'

# 操作手順書: images\*.png を data URI で埋め込んだ 1 ファイルにする
$manualDir = Join-Path $RepoRoot 'docs\manual'
$html = [IO.File]::ReadAllText((Join-Path $manualDir 'index.html'), [Text.Encoding]::UTF8)
$html = [Regex]::Replace($html, 'src="(images/[^"]+\.png)"', {
    param($m)
    $bytes = [IO.File]::ReadAllBytes((Join-Path $manualDir ($m.Groups[1].Value -replace '/', '\')))
    'src="data:image/png;base64,' + [Convert]::ToBase64String($bytes) + '"'
})
if ($html -match 'src="images/') { Fail '操作手順書の画像を埋め込めませんでした。' }
[IO.File]::WriteAllText((Join-Path $PackageDir 'manual.html'), $html, (New-Object Text.UTF8Encoding $false))
Write-Ok 'manual.html（画像埋め込み）'

$commit = try { (& git -C $RepoRoot rev-parse --short HEAD 2>$null) } catch { '' }
$exeVersion = (Get-Item (Join-Path $appDir "$AppName.exe")).VersionInfo.ProductVersion
Set-Content -LiteralPath (Join-Path $PackageDir $PkgMarker) -Encoding ASCII -Value @(
    "version=$Version", "product=$exeVersion", "commit=$commit", "built=$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')")

# 改ざん確認用のハッシュ（SHA256SUMS.txt 自身と目印は除く）
$sums = Get-ChildItem -LiteralPath $PackageDir -Recurse -File |
    Where-Object { $_.Name -ne 'SHA256SUMS.txt' -and $_.Name -ne $PkgMarker } |
    Sort-Object FullName |
    ForEach-Object {
        $rel = $_.FullName.Substring($PackageDir.Length + 1)
        '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $rel
    }
Set-Content -LiteralPath (Join-Path $PackageDir 'SHA256SUMS.txt') -Encoding ASCII -Value $sums
Write-Ok "SHA256SUMS.txt（$(@($sums).Count) ファイル）"

# ---------------------------------------------------------------- 4. ZIP
Write-Step '4/4 ZIP'
$size = [Math]::Round(((Get-ChildItem -LiteralPath $PackageDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 1)
if ($NoZip) {
    Write-Info '作りません（-NoZip）'
} else {
    $zip = "$PackageDir.zip"
    if (Test-Path $zip) { Remove-Item -LiteralPath $zip -Force }
    Compress-Archive -Path $PackageDir -DestinationPath $zip -CompressionLevel Optimal
    Write-Ok ("{0}（{1} MB）" -f $zip, [Math]::Round((Get-Item $zip).Length / 1MB, 1))
}

Write-Host ''
Write-Host '配布フォルダを作成しました。' -ForegroundColor Green
Write-Info "$PackageDir（$size MB）"
Write-Info '配布先の PC では、フォルダ（ZIP は展開して）の setup.cmd を実行します。'
exit 0
