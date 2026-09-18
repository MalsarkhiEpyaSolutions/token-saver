# publish.ps1 — builds the distributable zip: dist/token-saver-v<version>.zip
# The zip carries TWO exes: the console CLI (the real installer, and what the hooks and the
# Scheduled Task run) and the setup GUI, which drives the CLI. The GUI locates the CLI beside
# itself, so both must stay in the same folder.
param([string]$Version = "1.0.0")
$ErrorActionPreference = 'Stop'
dotnet test TokenStack.sln
if ($LASTEXITCODE -ne 0) { throw "tests failed - not publishing" }

$common = @(
  '-c', 'Release', '-r', 'win-x64', '--self-contained',
  '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
  # NOT EnableCompressionInSingleFile: measured, it shrinks the exes on disk but leaves the
  # zip the same size (a compressed bundle will not re-compress), and it makes every startup
  # decompress first — which the CLI pays on EVERY Claude session, because the session status
  # hook runs it. Not trimmed either: WinForms resolves controls reflectively.
  "-p:Version=$Version"
)

# Separate output folders: two single-file publishes into one folder fight over shared
# intermediate artifacts. Stage the two exes together afterwards instead.
dotnet publish src/TokenStack.Cli @common -o publish-cli
if ($LASTEXITCODE -ne 0) { throw "cli publish failed" }
dotnet publish src/TokenStack.Gui @common -o publish-gui
if ($LASTEXITCODE -ne 0) { throw "gui publish failed" }

Remove-Item -Recurse -Force publish-out -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force publish-out | Out-Null
Copy-Item publish-cli/token-saver.exe       publish-out/
Copy-Item publish-gui/token-saver-setup.exe publish-out/
Copy-Item README.md                         publish-out/

New-Item -ItemType Directory -Force dist | Out-Null
Compress-Archive -Force -DestinationPath "dist/token-saver-v$Version.zip" `
  -Path "publish-out/token-saver.exe", "publish-out/token-saver-setup.exe", "publish-out/README.md"
Remove-Item -Recurse -Force publish-out, publish-cli, publish-gui
Write-Host "dist/token-saver-v$Version.zip ready" -ForegroundColor Green
