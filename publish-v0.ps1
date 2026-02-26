param(
  [string]$Version = "0.2.6",
  [string]$Configuration = "Release",
  [ValidateSet("self-contained", "lite")]
  [string]$Package = "self-contained"
)

$ErrorActionPreference = "Stop"

$projectRoot = $PSScriptRoot
$projectFile = Join-Path $projectRoot "MsfsLocalBridge.csproj"
$isSelfContained = $Package -eq "self-contained"
$packageName = "msfs-local-bridge-v$Version-$Package"
$publishDir = Join-Path $projectRoot "dist\publish\win-x64"
$packageRoot = Join-Path $projectRoot "dist\package\$packageName"
$zipPath = Join-Path $projectRoot "dist\$packageName.zip"

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
Write-Host "  runtime: win-x64"
Write-Host "  package: $Package"
Write-Host "  self-contained: $isSelfContained"

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
  --self-contained $isSelfContained `
  -o $publishDir

New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null

# Package only runtime artifacts and runbook files (exclude bin/obj source artifacts).
Copy-Item (Join-Path $publishDir "*") $packageRoot -Recurse -Force
Copy-Item (Join-Path $projectRoot "README.md") (Join-Path $packageRoot "README.md") -Force
Copy-Item (Join-Path $projectRoot "FIRST_TIME_CHECKLIST.md") (Join-Path $packageRoot "FIRST_TIME_CHECKLIST.md") -Force
Copy-Item (Join-Path $projectRoot "run-bridge.ps1") (Join-Path $packageRoot "run-bridge.ps1") -Force
Copy-Item (Join-Path $projectRoot "preflight-v0.ps1") (Join-Path $packageRoot "preflight-v0.ps1") -Force
Copy-Item (Join-Path $projectRoot "repair-elevated-v0.ps1") (Join-Path $packageRoot "repair-elevated-v0.ps1") -Force
Copy-Item (Join-Path $projectRoot "diagnostics-v0.ps1") (Join-Path $packageRoot "diagnostics-v0.ps1") -Force

# Ensure SimConnect DLLs exist both in root (runtime load path) and lib (diagnostics/reference).
Copy-Item $managedDll (Join-Path $packageRoot "Microsoft.FlightSimulator.SimConnect.dll") -Force
Copy-Item $nativeDll (Join-Path $packageRoot "SimConnect.dll") -Force

$packageLibDir = Join-Path $packageRoot "lib"
New-Item -ItemType Directory -Path $packageLibDir -Force | Out-Null
Copy-Item $managedDll (Join-Path $packageLibDir "Microsoft.FlightSimulator.SimConnect.dll") -Force
Copy-Item $nativeDll (Join-Path $packageLibDir "SimConnect.dll") -Force

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
