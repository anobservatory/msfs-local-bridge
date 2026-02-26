param(
  [string]$BindHost = "0.0.0.0",
  [int]$Port = 39000,
  [int]$WssPort = 39002,
  [string]$LocalDomain = "ao.home.arpa",
  [string]$CertDir = "certs",
  [string]$StreamPath = "/stream",
  [int]$SampleIntervalMs = 200,
  [int]$PollIntervalMs = 25,
  [int]$ReconnectDelayMs = 2000,
  [int]$ReconnectMaxDelayMs = 10000,
  [switch]$SkipPreflight,
  [switch]$SkipCertSetup,
  [switch]$SkipLanHints,
  [switch]$DisableWss,
  [switch]$RequireWss,
  [switch]$Force
)

$ErrorActionPreference = "Stop"

$preflightScript = Join-Path $PSScriptRoot "preflight-v0.ps1"
$setupCertScript = Join-Path $PSScriptRoot "setup-wss-cert-v0.ps1"
$runScript = Join-Path $PSScriptRoot "run-bridge.ps1"

if (-not (Test-Path $runScript)) {
  throw "Missing required script: $runScript"
}

$safeCertBase = ($LocalDomain -replace '[^a-zA-Z0-9._-]', '_')
$certRoot = if ([System.IO.Path]::IsPathRooted($CertDir)) {
  [System.IO.Path]::GetFullPath($CertDir)
} else {
  [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot $CertDir))
}
$certPath = Join-Path $certRoot "$safeCertBase.pem"
$keyPath = Join-Path $certRoot "$safeCertBase-key.pem"
$wssRequested = -not $DisableWss

if (-not $SkipPreflight) {
  if (-not (Test-Path $preflightScript)) {
    throw "Missing required script: $preflightScript"
  }

  Write-Host "MSFS Sync Starter (LAN)" -ForegroundColor Cyan
  Write-Host "Step 1/3: Running preflight..."
  Write-Host ""

  & $preflightScript -Port $Port -WssPort $WssPort -LocalDomain $LocalDomain -CertDir $CertDir -Strict
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
  Write-Host "Step 1/3: Skipped preflight by request (-SkipPreflight)." -ForegroundColor Yellow
}

Write-Host ""
if ($wssRequested) {
  if ((Test-Path $certPath) -and (Test-Path $keyPath)) {
    Write-Host "Step 2/3: WSS certificate already present."
  }
  elseif ($SkipCertSetup) {
    Write-Host "Step 2/3: WSS certificate setup skipped by request (-SkipCertSetup)." -ForegroundColor Yellow
    if ($RequireWss) {
      throw "WSS is required but certificate setup was skipped and cert files are missing."
    }
  }
  else {
    if (-not (Test-Path $setupCertScript)) {
      throw "Missing required script: $setupCertScript"
    }
    Write-Host "Step 2/3: Creating WSS certificate..."
    Write-Host ""
    & $setupCertScript -LocalDomain $LocalDomain -CertDir $CertDir
    $certExitCode = $LASTEXITCODE
    if ($certExitCode -ne 0) {
      if ($RequireWss) {
        throw "WSS certificate setup failed and -RequireWss is set."
      }
      Write-Host ""
      Write-Host "[WARN] WSS certificate setup failed; continuing in WS-only mode." -ForegroundColor Yellow
    }
  }
}
else {
  Write-Host "Step 2/3: WSS disabled by request (-DisableWss)." -ForegroundColor Yellow
}

Write-Host ""
Write-Host "Step 3/3: Starting bridge..."
Write-Host ""

$runArgs = @{
  BindHost = $BindHost
  Port = $Port
  WssPort = $WssPort
  LocalDomain = $LocalDomain
  CertDir = $CertDir
  StreamPath = $StreamPath
  SampleIntervalMs = $SampleIntervalMs
  PollIntervalMs = $PollIntervalMs
  ReconnectDelayMs = $ReconnectDelayMs
  ReconnectMaxDelayMs = $ReconnectMaxDelayMs
}

if ($SkipLanHints) {
  $runArgs.SkipLanHints = $true
}
if ($DisableWss) {
  $runArgs.DisableWss = $true
}
if ($RequireWss) {
  $runArgs.RequireWss = $true
}

& $runScript @runArgs
exit $LASTEXITCODE
