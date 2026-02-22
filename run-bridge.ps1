param(
  [string]$BindHost = "0.0.0.0",
  [int]$Port = 39000,
  [string]$StreamPath = "/stream",
  [int]$SampleIntervalMs = 200,
  [int]$PollIntervalMs = 25,
  [int]$ReconnectDelayMs = 2000
)

$ErrorActionPreference = "Stop"

$env:MSFS_BRIDGE_BIND = "$BindHost"
$env:MSFS_BRIDGE_PORT = "$Port"
$env:MSFS_BRIDGE_PATH = "$StreamPath"
$env:MSFS_BRIDGE_SAMPLE_MS = "$SampleIntervalMs"
$env:MSFS_BRIDGE_POLL_MS = "$PollIntervalMs"
$env:MSFS_BRIDGE_RECONNECT_MS = "$ReconnectDelayMs"

Write-Host "Starting MSFS Local Bridge..."
Write-Host "  ws://$BindHost`:$Port$StreamPath"
Write-Host "Press Ctrl+C to stop."
Write-Host ""

$exePath = Join-Path $PSScriptRoot "MsfsLocalBridge.exe"
$dllPath = Join-Path $PSScriptRoot "MsfsLocalBridge.dll"
$projectPath = Join-Path $PSScriptRoot "MsfsLocalBridge.csproj"

# Prefer release executable when present (packaged zip scenario).
if (Test-Path $exePath) {
  & $exePath
  exit $LASTEXITCODE
}

# Fallback to source project execution.
if (Test-Path $projectPath) {
  dotnet run --project $projectPath
  exit $LASTEXITCODE
}

# Last fallback for runtime-only directory without project file.
if (Test-Path $dllPath) {
  dotnet $dllPath
  exit $LASTEXITCODE
}

throw "No runnable bridge target found. Expected one of: MsfsLocalBridge.exe, MsfsLocalBridge.csproj, MsfsLocalBridge.dll"
