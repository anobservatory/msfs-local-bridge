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
  [switch]$SkipLanHints,
  [switch]$DisableWss,
  [switch]$RequireWss
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

function Get-PrivateLanIPv4 {
  $ordered = New-Object System.Collections.Generic.List[string]
  $seen = @{}

  function Add-PrivateCandidate {
    param([string]$Ip)
    if ([string]::IsNullOrWhiteSpace($Ip)) { return }
    if ($Ip -eq "127.0.0.1" -or $Ip -like "169.254.*") { return }
    if ($Ip -notmatch '^(10\.|192\.168\.|172\.(1[6-9]|2[0-9]|3[0-1])\.)') { return }
    if (-not $seen.ContainsKey($Ip)) {
      $seen[$Ip] = $true
      $ordered.Add($Ip) | Out-Null
    }
  }

  try {
    $preferred = @(Get-NetIPConfiguration -ErrorAction Stop |
      Where-Object {
        $_.IPv4Address -and
        $_.IPv4DefaultGateway -and
        $_.NetAdapter -and
        $_.NetAdapter.Status -eq "Up"
      } |
      ForEach-Object { @($_.IPv4Address) } |
      ForEach-Object { $_.IPAddress })

    foreach ($ip in $preferred) {
      Add-PrivateCandidate -Ip $ip
    }
  }
  catch {
    # Fallback probe below.
  }

  try {
    $netIps = @(Get-NetIPAddress -AddressFamily IPv4 -ErrorAction Stop |
      Where-Object {
        $_.IPAddress -and
        $_.AddressState -eq "Preferred"
      } |
      Select-Object -ExpandProperty IPAddress -Unique)

    foreach ($ip in $netIps) {
      Add-PrivateCandidate -Ip $ip
    }
  }
  catch {
    # Fallback below if Get-NetIPAddress is unavailable.
  }

  if ($ordered.Count -eq 0) {
    try {
      $hostName = [System.Net.Dns]::GetHostName()
      $dnsIps = @([System.Net.Dns]::GetHostAddresses($hostName) |
        Where-Object {
          $_.AddressFamily -eq [System.Net.Sockets.AddressFamily]::InterNetwork
        } |
        ForEach-Object { $_.IPAddressToString })

      foreach ($ip in $dnsIps) {
        Add-PrivateCandidate -Ip $ip
      }
    }
    catch {
      # Ignore DNS lookup failures.
    }
  }

  return @($ordered)
}

function Get-FirewallRuleName {
  param([int]$RulePort)
  return "AO MSFS Bridge TCP $RulePort (Private)"
}

function Test-ManagedFirewallRule {
  param([int]$RulePort)
  try {
    $ruleName = Get-FirewallRuleName -RulePort $RulePort
    $rules = @(Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue |
      Where-Object {
        $_.Enabled -eq "True" -and
        $_.Direction -eq "Inbound" -and
        $_.Action -eq "Allow"
      })
    return $rules.Count -gt 0
  }
  catch {
    return $false
  }
}

function Get-SafeCertBaseName {
  param([string]$Domain)
  return ($Domain -replace '[^a-zA-Z0-9._-]', '_')
}

function Resolve-PathUnderRoot {
  param(
    [string]$Root,
    [string]$PathValue
  )

  if ([System.IO.Path]::IsPathRooted($PathValue)) {
    return [System.IO.Path]::GetFullPath($PathValue)
  }

  return [System.IO.Path]::GetFullPath((Join-Path $Root $PathValue))
}

$env:MSFS_BRIDGE_BIND = "$BindHost"
$env:MSFS_BRIDGE_PORT = "$Port"
$env:MSFS_BRIDGE_PATH = "$StreamPath"
$env:MSFS_BRIDGE_SAMPLE_MS = "$SampleIntervalMs"
$env:MSFS_BRIDGE_POLL_MS = "$PollIntervalMs"
$env:MSFS_BRIDGE_RECONNECT_MS = "$ReconnectDelayMs"
$env:MSFS_BRIDGE_RECONNECT_MAX_MS = "$ReconnectMaxDelayMs"

$safeCertBase = Get-SafeCertBaseName -Domain $LocalDomain
$certRoot = Resolve-PathUnderRoot -Root $PSScriptRoot -PathValue $CertDir
$certPath = Join-Path $certRoot "$safeCertBase.pem"
$keyPath = Join-Path $certRoot "$safeCertBase-key.pem"
$pfxPath = Join-Path $certRoot "$safeCertBase.p12"
$rootCaPath = Join-Path $certRoot "rootCA.pem"
$wssRequested = -not $DisableWss
$wssReady = $false
$wssUsesPfx = $false
$lanIps = @(Get-PrivateLanIPv4)
$bootstrapHostIp = if ($lanIps.Count -gt 0) { $lanIps[0] } else { "" }
$wssConnectHost = if (-not [string]::IsNullOrWhiteSpace($bootstrapHostIp)) { $bootstrapHostIp } else { $LocalDomain }

if ($wssRequested) {
  if (Test-Path $pfxPath) {
    $wssReady = $true
    $wssUsesPfx = $true
  }
  elseif ((Test-Path $certPath) -and (Test-Path $keyPath)) {
    $wssReady = $true
  }
  elseif ($RequireWss) {
    throw "WSS is required but certificate files are missing. Expected pfx='$pfxPath' or cert='$certPath', key='$keyPath'. Run .\setup-wss-cert-v0.ps1 -LocalDomain $LocalDomain"
  }

  if (-not $wssReady) {
    Write-Host "[WARN] WSS certificate files are missing; starting in WS-only mode." -ForegroundColor Yellow
    Write-Host "[HINT] Generate certs:" -ForegroundColor Yellow
    Write-Host "  .\\setup-wss-cert-v0.ps1 -LocalDomain $LocalDomain -CertDir `"$CertDir`"" -ForegroundColor Yellow
  }
}

$env:MSFS_BRIDGE_WSS_ENABLED = if ($wssReady) { "true" } else { "false" }
$env:MSFS_BRIDGE_WSS_BIND = "$BindHost"
$env:MSFS_BRIDGE_WSS_PORT = "$WssPort"
$env:MSFS_BRIDGE_PUBLIC_WSS_HOST = "$wssConnectHost"
$env:MSFS_BRIDGE_TLS_CERT_PATH = "$certPath"
$env:MSFS_BRIDGE_TLS_KEY_PATH = "$keyPath"
$env:MSFS_BRIDGE_TLS_PFX_PATH = if ($wssUsesPfx) { "$pfxPath" } else { "" }
$env:MSFS_BRIDGE_TLS_PFX_PASSWORD = if ($wssUsesPfx) { "changeit" } else { "" }
$env:MSFS_BRIDGE_BOOTSTRAP_ENABLED = "true"
$env:MSFS_BRIDGE_BOOTSTRAP_PATH = "/bootstrap"
$env:MSFS_BRIDGE_BOOTSTRAP_HOST_IP = "$bootstrapHostIp"
$env:MSFS_BRIDGE_BOOTSTRAP_CA_PATH = "$rootCaPath"

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
Write-Host "  bind:   ws://$BindHost`:$Port$StreamPath"
Write-Host "  local:  ws://127.0.0.1`:$Port$StreamPath"
if ($wssReady) {
  Write-Host "  secure: wss://$wssConnectHost`:$WssPort$StreamPath"
  Write-Host "  secure cert mode: $(if ($wssUsesPfx) { 'pfx' } else { 'pem' })"
  if ($wssConnectHost -ne $LocalDomain) {
    Write-Host "  fallback secure (hosts mapping required): wss://$LocalDomain`:$WssPort$StreamPath"
  }
}

if (-not $SkipLanHints) {
  if ($lanIps.Count -gt 0) {
    Write-Host "  LAN URL candidates:"
    foreach ($ip in $lanIps) {
      $bridgeUrl = "ws://$ip`:$Port$StreamPath"
      Write-Host "    $bridgeUrl"
    }

    $preferredUrl = "ws://$($lanIps[0])`:$Port$StreamPath"
    $encodedPreferredUrl = [System.Uri]::EscapeDataString($preferredUrl)
    Write-Host "  Quick open from Mac browser:"
    Write-Host "    http://localhost:3000/?msfsBridgeUrl=$encodedPreferredUrl"
    if ($wssReady) {
      $secureUrl = "wss://$wssConnectHost`:$WssPort$StreamPath"
      $encodedSecureUrl = [System.Uri]::EscapeDataString($secureUrl)
      Write-Host "  Quick open on anobservatory.com:"
      Write-Host "    https://anobservatory.com/?msfsBridgeUrl=$encodedSecureUrl"
      $bootstrapUrl = "http://$bootstrapHostIp`:$Port/bootstrap"
      Write-Host "  Listener onboarding page:"
      Write-Host "    $bootstrapUrl"
      Write-Host "  Listener bootstrap scripts:"
      Write-Host "    Mac:     curl -fsSL $bootstrapUrl/listener/mac.sh | bash"
      Write-Host "    Windows: powershell -ExecutionPolicy Bypass -Command `"iwr '$bootstrapUrl/listener/windows.ps1' -UseBasicParsing | iex`""
    }
  }
  else {
    Write-Host "  [WARN] Could not detect private LAN IPv4 automatically." -ForegroundColor Yellow
    Write-Host "  [HINT] Run ipconfig and use IPv4 in: ws://<WINDOWS_IP>:$Port$StreamPath" -ForegroundColor Yellow
  }

  if (Test-ManagedFirewallRule -RulePort $Port) {
    Write-Host "  [PASS] Managed firewall rule is present for TCP $Port." -ForegroundColor Green
  }
  else {
    Write-Host "  [WARN] Managed firewall rule is not present for TCP $Port." -ForegroundColor Yellow
    Write-Host "  [HINT] If another device cannot connect, run (Admin):" -ForegroundColor Yellow
    Write-Host "    .\\repair-elevated-v0.ps1 -Action OpenFirewall39000 -Port $Port" -ForegroundColor Yellow
  }

  if ($wssReady) {
    if (Test-ManagedFirewallRule -RulePort $WssPort) {
      Write-Host "  [PASS] Managed firewall rule is present for TCP $WssPort." -ForegroundColor Green
    }
    else {
      Write-Host "  [WARN] Managed firewall rule is not present for TCP $WssPort." -ForegroundColor Yellow
      Write-Host "  [HINT] If WSS clients cannot connect, run (Admin):" -ForegroundColor Yellow
      Write-Host "    .\\repair-elevated-v0.ps1 -Action OpenFirewall39002 -Port $WssPort" -ForegroundColor Yellow
    }
  }
}

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




