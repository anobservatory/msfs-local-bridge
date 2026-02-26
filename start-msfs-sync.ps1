param(
  [string]$BindHost = "0.0.0.0",
  [int]$Port = 39000,
  [string]$StreamPath = "/stream",
  [int]$SampleIntervalMs = 200,
  [int]$PollIntervalMs = 25,
  [int]$ReconnectDelayMs = 2000,
  [int]$ReconnectMaxDelayMs = 10000,
  [switch]$SkipPreflight,
  [switch]$SkipLanHints,
  [switch]$Force
)

$ErrorActionPreference = "Stop"

$preflightScript = Join-Path $PSScriptRoot "preflight-v0.ps1"
$runScript = Join-Path $PSScriptRoot "run-bridge.ps1"

if (-not (Test-Path $runScript)) {
  throw "Missing required script: $runScript"
}

if (-not $SkipPreflight) {
  if (-not (Test-Path $preflightScript)) {
    throw "Missing required script: $preflightScript"
  }

  Write-Host "MSFS Sync Starter (LAN)" -ForegroundColor Cyan
  Write-Host "Step 1/2: Running preflight..."
  Write-Host ""

  & $preflightScript -Port $Port -Strict
  $preflightExitCode = $LASTEXITCODE
  if ($preflightExitCode -ne 0) {
    Write-Host ""
    Write-Host "[FAIL] Preflight reported blocking checks." -ForegroundColor Red
    if (-not $Force) {
      Write-Host "[HINT] Fix FAIL checks first, then rerun." -ForegroundColor Yellow
      Write-Host "[HINT] To bypass once (not recommended): .\\start-msfs-sync.ps1 -Force" -ForegroundColor Yellow
      exit $preflightExitCode
    }

    Write-Host "[WARN] Continuing because -Force was provided." -ForegroundColor Yellow
  }
}
else {
  Write-Host "MSFS Sync Starter (LAN)" -ForegroundColor Cyan
  Write-Host "Step 1/2: Skipped preflight by request (-SkipPreflight)." -ForegroundColor Yellow
}

Write-Host ""
Write-Host "Step 2/2: Starting bridge..."
Write-Host ""

$runArgs = @{
  BindHost = $BindHost
  Port = $Port
  StreamPath = $StreamPath
  SampleIntervalMs = $SampleIntervalMs
  PollIntervalMs = $PollIntervalMs
  ReconnectDelayMs = $ReconnectDelayMs
  ReconnectMaxDelayMs = $ReconnectMaxDelayMs
}

if ($SkipLanHints) {
  $runArgs.SkipLanHints = $true
}

& $runScript @runArgs
exit $LASTEXITCODE
