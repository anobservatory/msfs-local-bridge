param(
  [string]$Version = "0.1.0",
  [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$projectRoot = $PSScriptRoot
$projectFile = Join-Path $projectRoot "MsfsLocalBridge.csproj"
$publishDir = Join-Path $projectRoot "dist\publish\win-x64"
$packageRoot = Join-Path $projectRoot "dist\package\msfs-local-bridge-v$Version"
$zipPath = Join-Path $projectRoot "dist\msfs-local-bridge-v$Version.zip"

$managedDll = Join-Path $projectRoot "lib\Microsoft.FlightSimulator.SimConnect.dll"
$nativeDll = Join-Path $projectRoot "lib\SimConnect.dll"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
  throw "dotnet SDK is not installed or not available in PATH."
}

if (-not (Test-Path $managedDll)) {
  throw "Missing required file: $managedDll"
}

if (-not (Test-Path $nativeDll)) {
  throw "Missing required file: $nativeDll"
}

Write-Host "Publishing msfs-local-bridge v$Version..." -ForegroundColor Cyan

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
  --self-contained false `
  -o $publishDir

New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null

# Package only runtime artifacts and runbook files (exclude bin/obj source artifacts).
Copy-Item (Join-Path $publishDir "*") $packageRoot -Recurse -Force
Copy-Item (Join-Path $projectRoot "README.md") (Join-Path $packageRoot "README.md") -Force
Copy-Item (Join-Path $projectRoot "run-bridge.ps1") (Join-Path $packageRoot "run-bridge.ps1") -Force
Copy-Item (Join-Path $projectRoot "preflight-v0.ps1") (Join-Path $packageRoot "preflight-v0.ps1") -Force

Compress-Archive -Path "$packageRoot\*" -DestinationPath $zipPath -Force

Write-Host ""
Write-Host "Build complete." -ForegroundColor Green
Write-Host "ZIP: $zipPath"
Write-Host ""
Write-Host "Quick verify:" -ForegroundColor Yellow
Write-Host "  1) Extract zip"
Write-Host "  2) Run .\preflight-v0.ps1"
Write-Host "  3) Run .\run-bridge.ps1"
Write-Host "  4) Confirm netstat -ano | findstr "":39000"""
