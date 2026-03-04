param(
  [string]$Version = "0.1.0",
  [string]$Configuration = "Release",
  [bool]$SelfContained = $true
)

$ErrorActionPreference = "Stop"

$projectRoot = $PSScriptRoot
$projectFile = Join-Path $projectRoot "apps\BridgeAssistant.WinUI\BridgeAssistant.WinUI.csproj"
$flavor = if ($SelfContained) { "self-contained" } else { "framework-dependent" }
$packageName = "msfs-local-bridge-assistant-winui-v$Version-$flavor"
$publishDir = Join-Path $projectRoot "dist\publish\winui\win-x64"
$packageRoot = Join-Path $projectRoot "dist\package\$packageName"
$zipPath = Join-Path $projectRoot "dist\$packageName.zip"
$hashFile = Join-Path $projectRoot "dist\SHA256SUMS-winui-v$Version.txt"

$managedDll = Join-Path $projectRoot "lib\Microsoft.FlightSimulator.SimConnect.dll"
$nativeDll = Join-Path $projectRoot "lib\SimConnect.dll"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
  throw "dotnet SDK is not installed or not available in PATH."
}

if (-not (Test-Path $projectFile)) {
  throw "WinUI project file not found: $projectFile"
}

if (-not (Test-Path $managedDll)) {
  throw "Missing required file: $managedDll"
}

if (-not (Test-Path $nativeDll)) {
  throw "Missing required file: $nativeDll"
}

Write-Host "Publishing WinUI Bridge Assistant v$Version..." -ForegroundColor Cyan
Write-Host "  runtime: win-x64"
Write-Host "  self-contained: $SelfContained"
Write-Host "  output zip: $zipPath"

if (Test-Path $publishDir) {
  Remove-Item $publishDir -Recurse -Force
}

if (Test-Path $packageRoot) {
  Remove-Item $packageRoot -Recurse -Force
}

if (Test-Path $zipPath) {
  Remove-Item $zipPath -Force
}

dotnet publish $projectFile `
  -c $Configuration `
  -r win-x64 `
  --self-contained $SelfContained `
  -p:WindowsAppSDKSelfContained=$SelfContained `
  -p:WindowsPackageType=None `
  -p:EnableMsixTooling=false `
  -p:PublishSingleFile=false `
  -o $publishDir

New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null
Copy-Item (Join-Path $publishDir "*") $packageRoot -Recurse -Force

Copy-Item (Join-Path $projectRoot "apps\BridgeAssistant.WinUI\README.md") (Join-Path $packageRoot "README-WINUI.md") -Force
Copy-Item (Join-Path $projectRoot "FIRST_TIME_CHECKLIST.md") (Join-Path $packageRoot "FIRST_TIME_CHECKLIST.md") -Force

Copy-Item (Join-Path $projectRoot "start.ps1") (Join-Path $packageRoot "start.ps1") -Force
Copy-Item (Join-Path $projectRoot "start-msfs-sync.ps1") (Join-Path $packageRoot "start-msfs-sync.ps1") -Force
Copy-Item (Join-Path $projectRoot "run-bridge.ps1") (Join-Path $packageRoot "run-bridge.ps1") -Force
Copy-Item (Join-Path $projectRoot "setup-wss-cert-v0.ps1") (Join-Path $packageRoot "setup-wss-cert-v0.ps1") -Force
Copy-Item (Join-Path $projectRoot "preflight-v0.ps1") (Join-Path $packageRoot "preflight-v0.ps1") -Force
Copy-Item (Join-Path $projectRoot "repair-elevated-v0.ps1") (Join-Path $packageRoot "repair-elevated-v0.ps1") -Force
Copy-Item (Join-Path $projectRoot "diagnostics-v0.ps1") (Join-Path $packageRoot "diagnostics-v0.ps1") -Force

Copy-Item $managedDll (Join-Path $packageRoot "Microsoft.FlightSimulator.SimConnect.dll") -Force
Copy-Item $nativeDll (Join-Path $packageRoot "SimConnect.dll") -Force

$packageLibDir = Join-Path $packageRoot "lib"
New-Item -ItemType Directory -Path $packageLibDir -Force | Out-Null
Copy-Item $managedDll (Join-Path $packageLibDir "Microsoft.FlightSimulator.SimConnect.dll") -Force
Copy-Item $nativeDll (Join-Path $packageLibDir "SimConnect.dll") -Force

Compress-Archive -Path "$packageRoot\*" -DestinationPath $zipPath -Force

$zipHash = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path $hashFile -Value ("$zipHash  " + (Split-Path $zipPath -Leaf)) -Encoding ascii

Write-Host ""
Write-Host "Build complete." -ForegroundColor Green
Write-Host "ZIP: $zipPath"
Write-Host "SHA256: $hashFile"
Write-Host ""
Write-Host "Quick verify:" -ForegroundColor Yellow
Write-Host "  1) Extract zip"
Write-Host "  2) Run BridgeAssistant.WinUI.exe"
Write-Host "  3) In app, run diagnostics before Start"
Write-Host "  4) Run admin-required repair commands when prompted"
