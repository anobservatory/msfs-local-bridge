param(
  [string]$BindHost = "0.0.0.0",
  [int]$Port = 39000,
  [string]$StreamPath = "/stream",
  [int]$SampleIntervalMs = 200,
  [int]$PollIntervalMs = 25,
  [int]$ReconnectDelayMs = 2000
)

$ErrorActionPreference = "Stop"

function Test-IsAdministrator {
  try {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
  }
  catch {
    return $false
  }
}

$env:MSFS_BRIDGE_BIND = "$BindHost"
$env:MSFS_BRIDGE_PORT = "$Port"
$env:MSFS_BRIDGE_PATH = "$StreamPath"
$env:MSFS_BRIDGE_SAMPLE_MS = "$SampleIntervalMs"
$env:MSFS_BRIDGE_POLL_MS = "$PollIntervalMs"
$env:MSFS_BRIDGE_RECONNECT_MS = "$ReconnectDelayMs"

$isAdmin = Test-IsAdministrator
if ($isAdmin) {
  Write-Host "[WARN] Running as Administrator." -ForegroundColor Yellow
  Write-Host "[WARN] Normal bridge usage should run as a standard user." -ForegroundColor Yellow
  Write-Host "[WARN] Keep elevated mode only for explicit repair actions." -ForegroundColor Yellow
}
else {
  Write-Host "[PASS] Running as standard user (recommended)." -ForegroundColor Green
}

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
