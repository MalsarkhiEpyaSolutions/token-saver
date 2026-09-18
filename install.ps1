# install.ps1 - one-line installer for TokenSaver.
#
#   irm https://raw.githubusercontent.com/MalsarkhiEpyaSolutions/token-saver/master/install.ps1 | iex
#
# Downloading with Invoke-WebRequest writes no Mark-of-the-Web, so the exe never trips
# SmartScreen - unlike a browser download of the release zip.
$ErrorActionPreference = 'Stop'

$api = 'https://api.github.com/repos/MalsarkhiEpyaSolutions/token-saver/releases/latest'
$asset = (Invoke-RestMethod $api -UseBasicParsing).assets |
    Where-Object { $_.name -like 'token-saver-v*.zip' } | Select-Object -First 1
if (-not $asset) { throw "no token-saver-v*.zip asset on the latest release" }

$zip  = Join-Path $env:TEMP 'token-saver.zip'
$dest = Join-Path $env:LOCALAPPDATA 'token-saver-download'

Write-Host "downloading $($asset.name) ..." -ForegroundColor Cyan
Invoke-WebRequest $asset.browser_download_url -OutFile $zip -UseBasicParsing
Remove-Item -Recurse -Force $dest -ErrorAction SilentlyContinue
Expand-Archive $zip $dest -Force
Remove-Item $zip -Force

$setup = Join-Path $dest 'token-saver-setup.exe'
if (Test-Path $setup) {
    # The GUI picks the install folder and shows progress; it runs the CLI underneath.
    Write-Host "launching setup ..." -ForegroundColor Cyan
    Start-Process -FilePath $setup -Wait
} else {
    # Older release, or a headless machine: fall back to the console installer.
    & (Join-Path $dest 'token-saver.exe') install
}
