param([switch]$Test)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_NOLOGO = '1'
$dotnet = Join-Path $PSScriptRoot 'tools/dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = 'dotnet' }
Push-Location $PSScriptRoot
try {
 $releaseDir = 'dist/v1.2.0-win-x64'
 & $dotnet publish CodexUsageWidget.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o $releaseDir
 if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
 Copy-Item README.md (Join-Path $releaseDir 'README.md') -Force
 if ($Test) {
  $testOutput = Join-Path $PSScriptRoot 'tests/release-1.2'
  $process = Start-Process -FilePath (Join-Path $releaseDir 'CodexUsageWidget.exe') -ArgumentList '--self-test', $testOutput -WindowStyle Hidden -PassThru -Wait
  Get-Content (Join-Path $testOutput 'results.txt')
  if ($process.ExitCode -ne 0) { throw 'Self tests failed' }
 }
 Compress-Archive -Path "$releaseDir/*" -DestinationPath dist/CodexUsageWidget-1.2.0-win-x64.zip -Force
 Get-FileHash (Join-Path $releaseDir 'CodexUsageWidget.exe'),dist/CodexUsageWidget-1.2.0-win-x64.zip -Algorithm SHA256 | Format-List
} finally { Pop-Location }
