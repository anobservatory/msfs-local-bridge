param(
  [string]$BindHost = "0.0.0.0",
  [int]$Port = 39000,
  [string]$StreamPath = "/stream",
  [int]$SampleIntervalMs = 200,
  [int]$PollIntervalMs = 25,
  [int]$ReconnectDelayMs = 2000,
  [int]$ReconnectMaxDelayMs = 10000,
  [switch]$SkipLanHints
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
  $candidates = New-Object System.Collections.Generic.List[string]
  try {
    $netIps = @(Get-NetIPAddress -AddressFamily IPv4 -ErrorAction Stop |
      Where-Object {
        $_.IPAddress -and
        $_.IPAddress -ne "127.0.0.1" -and
        $_.IPAddress -notlike "169.254.*" -and
        $_.AddressState -eq "Preferred"
      } |
      Select-Object -ExpandProperty IPAddress -Unique)

    foreach ($ip in $netIps) {
      if ($ip -match '^(10\.|192\.168\.|172\.(1[6-9]|2[0-9]|3[0-1])\.)') {
        $candidates.Add($ip) | Out-Null
      }
    }
  }
  catch {
    # Fallback below if Get-NetIPAddress is unavailable.
  }

  if ($candidates.Count -eq 0) {
    try {
      $hostName = [System.Net.Dns]::GetHostName()
      $dnsIps = @([System.Net.Dns]::GetHostAddresses($hostName) |
        Where-Object {
          $_.AddressFamily -eq [System.Net.Sockets.AddressFamily]::InterNetwork
        } |
        ForEach-Object { $_.IPAddressToString })

      foreach ($ip in $dnsIps) {
        if (
          $ip -ne "127.0.0.1" -and
          $ip -notlike "169.254.*" -and
          $ip -match '^(10\.|192\.168\.|172\.(1[6-9]|2[0-9]|3[0-1])\.)'
        ) {
          $candidates.Add($ip) | Out-Null
        }
      }
    }
    catch {
      # Ignore DNS lookup failures.
    }
  }

  return @($candidates | Select-Object -Unique)
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

$env:MSFS_BRIDGE_BIND = "$BindHost"
$env:MSFS_BRIDGE_PORT = "$Port"
$env:MSFS_BRIDGE_PATH = "$StreamPath"
$env:MSFS_BRIDGE_SAMPLE_MS = "$SampleIntervalMs"
$env:MSFS_BRIDGE_POLL_MS = "$PollIntervalMs"
$env:MSFS_BRIDGE_RECONNECT_MS = "$ReconnectDelayMs"
$env:MSFS_BRIDGE_RECONNECT_MAX_MS = "$ReconnectMaxDelayMs"

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

if (-not $SkipLanHints) {
  $lanIps = Get-PrivateLanIPv4
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
