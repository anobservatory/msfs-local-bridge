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
  [switch]$EnableWss,
  [switch]$Force,
  [switch]$NoRequireWss
)

$ErrorActionPreference = "Stop"

$starter = Join-Path $PSScriptRoot "start-msfs-sync.ps1"
if (-not (Test-Path $starter)) {
  throw "Missing required script: $starter"
}

# One-click default entrypoint for testers.
# Defaults to local-network WS mode; WSS remains an explicit legacy override.
$starterArgs = @{
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

if ($SkipPreflight) {
  $starterArgs.SkipPreflight = $true
}
if ($SkipCertSetup) {
  $starterArgs.SkipCertSetup = $true
}
if ($SkipLanHints) {
  $starterArgs.SkipLanHints = $true
}
if ($EnableWss -and -not $DisableWss) {
  if (-not $NoRequireWss) {
    $starterArgs.RequireWss = $true
  }
  $starterArgs.EnableWss = $true
}
else {
  $starterArgs.DisableWss = $true
}
if ($Force) {
  $starterArgs.Force = $true
}

& $starter @starterArgs
exit $LASTEXITCODE
