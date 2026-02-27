param(
  [int]$Port = 39000,
  [int]$WssPort = 39002,
  [string]$LocalDomain = "ao.home.arpa",
  [string]$CertDir = "certs",
  [switch]$Strict
)

$ErrorActionPreference = "Stop"

$script:FailCount = 0
$script:WarnCount = 0

function Write-Check {
  param(
    [ValidateSet("PASS", "WARN", "FAIL")]
    [string]$Status,
    [string]$Message
  )

  switch ($Status) {
    "PASS" {
      Write-Host "[PASS] $Message" -ForegroundColor Green
    }
    "WARN" {
      $script:WarnCount += 1
      Write-Host "[WARN] $Message" -ForegroundColor Yellow
    }
    "FAIL" {
      $script:FailCount += 1
      Write-Host "[FAIL] $Message" -ForegroundColor Red
    }
  }
}

function Test-RequiredFile {
  param(
    [string]$Path,
    [string]$Label
  )

  if (Test-Path $Path) {
    Write-Check -Status PASS -Message "$Label found: $Path"
  }
  else {
    Write-Check -Status FAIL -Message "$Label missing: $Path"
  }
}

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

function Get-SafeCertBaseName {
  param([string]$Domain)
  return ($Domain -replace '[^a-zA-Z0-9._-]', '_')
}

function Find-MkcertPath {
  $command = Get-Command mkcert -ErrorAction SilentlyContinue
  if ($null -ne $command -and $command.Source -and (Test-Path $command.Source)) {
    return [System.IO.Path]::GetFullPath($command.Source)
  }

  $candidatePaths = @(
    (Join-Path $env:LOCALAPPDATA "Microsoft\WinGet\Links\mkcert.exe"),
    (Join-Path $env:ProgramData "chocolatey\bin\mkcert.exe"),
    (Join-Path $env:ProgramFiles "mkcert\mkcert.exe"),
    (Join-Path $env:ProgramFiles "mkcert\bin\mkcert.exe")
  )

  $programFilesX86 = ${env:ProgramFiles(x86)}
  if ($programFilesX86) {
    $candidatePaths += (Join-Path $programFilesX86 "mkcert\mkcert.exe")
    $candidatePaths += (Join-Path $programFilesX86 "mkcert\bin\mkcert.exe")
  }

  foreach ($candidate in ($candidatePaths | Where-Object { $_ } | Select-Object -Unique)) {
    if (Test-Path $candidate) {
      return [System.IO.Path]::GetFullPath($candidate)
    }
  }

  try {
    $whereMatches = @(& where.exe mkcert 2>$null)
    foreach ($match in $whereMatches) {
      if ($match -and (Test-Path $match.Trim())) {
        return [System.IO.Path]::GetFullPath($match.Trim())
      }
    }
  }
  catch {
    # Ignore where.exe failures.
  }

  return $null
}

function Test-PortAvailability {
  param([int]$RulePort)

  $portLines = @(netstat -ano | Select-String ":$RulePort")
  $listeningLines = @($portLines | Where-Object { $_.Line -match "LISTENING" })

  if ($listeningLines.Count -eq 0) {
    Write-Check -Status PASS -Message "TCP $RulePort is free (no current LISTENING process)"
    return
  }

  foreach ($entry in $listeningLines) {
    $line = ($entry.Line -replace "\s+", " ").Trim()
    $parts = $line.Split(" ")
    $pidText = $parts[-1]

    $parsedPid = 0
    if (-not [int]::TryParse($pidText, [ref]$parsedPid)) {
      Write-Check -Status WARN -Message "Unable to parse PID from: $line"
      continue
    }

    $pid = $parsedPid
    $process = Get-Process -Id $pid -ErrorAction SilentlyContinue
    if ($null -eq $process) {
      Write-Check -Status WARN -Message "TCP $RulePort in use by PID $pid (process not found)"
      continue
    }

    if ($process.ProcessName -eq "node") {
      Write-Check -Status WARN -Message "TCP $RulePort in use by node.exe (likely mock sender)"
    }
    else {
      Write-Check -Status WARN -Message "TCP $RulePort already in use by $($process.ProcessName).exe (PID $pid)"
    }
  }
}

Write-Host "MSFS Local Bridge v0 Preflight" -ForegroundColor Cyan
Write-Host "Project: $PSScriptRoot"
Write-Host ""

$managedDll = Join-Path $PSScriptRoot "lib\Microsoft.FlightSimulator.SimConnect.dll"
$nativeDll = Join-Path $PSScriptRoot "lib\SimConnect.dll"

Test-RequiredFile -Path $managedDll -Label "Managed SimConnect DLL"
Test-RequiredFile -Path $nativeDll -Label "Native SimConnect DLL"

$projectFile = Join-Path $PSScriptRoot "MsfsLocalBridge.csproj"
$releaseExe = Join-Path $PSScriptRoot "MsfsLocalBridge.exe"
$releaseLayout = (Test-Path $releaseExe) -and (-not (Test-Path $projectFile))

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if ($releaseLayout) {
  if ($null -eq $dotnet) {
    Write-Check -Status PASS -Message "dotnet not found (expected for self-contained release usage)"
  }
  else {
    Write-Check -Status PASS -Message "dotnet found (optional for self-contained release): $($dotnet.Source)"
  }
}
else {
  if ($null -eq $dotnet) {
    Write-Check -Status FAIL -Message "dotnet SDK not found in PATH (required for source build layout)"
  }
  else {
    Write-Check -Status PASS -Message "dotnet found: $($dotnet.Source)"
  }
}

if ($releaseLayout) {
  $managedRootDll = Join-Path $PSScriptRoot "Microsoft.FlightSimulator.SimConnect.dll"
  $nativeRootDll = Join-Path $PSScriptRoot "SimConnect.dll"
  Test-RequiredFile -Path $managedRootDll -Label "Release root managed SimConnect DLL"
  Test-RequiredFile -Path $nativeRootDll -Label "Release root native SimConnect DLL"
}

$outputRoots = @(
  (Join-Path $PSScriptRoot "bin\Debug\net8.0-windows\win-x64"),
  (Join-Path $PSScriptRoot "bin\Release\net8.0-windows\win-x64")
)

$existingOutput = $outputRoots | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($null -eq $existingOutput) {
  Write-Check -Status PASS -Message "No build output found yet (normal before first source run or when using release zip)"
}
else {
  $managedOut = Join-Path $existingOutput "Microsoft.FlightSimulator.SimConnect.dll"
  $nativeOut = Join-Path $existingOutput "SimConnect.dll"
  Test-RequiredFile -Path $managedOut -Label "Output managed SimConnect DLL"
  Test-RequiredFile -Path $nativeOut -Label "Output native SimConnect DLL"
}

if ([Environment]::Is64BitOperatingSystem) {
  Write-Check -Status PASS -Message "OS architecture is x64"
}
else {
  Write-Check -Status FAIL -Message "OS is not x64"
}

if ([Environment]::Is64BitProcess) {
  Write-Check -Status PASS -Message "PowerShell process is x64"
}
else {
  Write-Check -Status WARN -Message "PowerShell process is not x64 (use x64 shell)"
}

$isAdmin = Test-IsAdministrator
if ($isAdmin) {
  Write-Check -Status WARN -Message "PowerShell is running elevated. Normal bridge operation should run as standard user."
}
else {
  Write-Check -Status PASS -Message "PowerShell is running as standard user (recommended)"
}

$lanIps = Get-PrivateLanIPv4
if ($lanIps.Count -gt 0) {
  $preview = @($lanIps | Select-Object -First 2)
  $sampleUrl = "ws://$($preview[0]):$Port/stream"
  $sampleSecureUrl = "wss://$LocalDomain`:$WssPort/stream"
  Write-Check -Status PASS -Message "Private LAN IPv4 detected: $($preview -join ', ') (sample bridge URL: $sampleUrl, sample secure URL: $sampleSecureUrl)"
}
else {
  Write-Check -Status WARN -Message "No private LAN IPv4 detected. Verify same-network setup before split-device sync."
}

if (Test-ManagedFirewallRule -RulePort $Port) {
  Write-Check -Status PASS -Message "Managed firewall rule present for inbound TCP $Port."
}
else {
  Write-Check -Status WARN -Message "Managed firewall rule not found for inbound TCP $Port (needed when Mac/mobile cannot connect)."
  Write-Host "  -> Repair: Run as Administrator:"
  Write-Host "     .\repair-elevated-v0.ps1 -Action OpenFirewall39000 -Port $Port"
}

if (Test-ManagedFirewallRule -RulePort $WssPort) {
  Write-Check -Status PASS -Message "Managed firewall rule present for inbound TCP $WssPort."
}
else {
  Write-Check -Status WARN -Message "Managed firewall rule not found for inbound TCP $WssPort (needed for WSS clients)."
  Write-Host "  -> Repair: Run as Administrator:"
  Write-Host "     .\repair-elevated-v0.ps1 -Action OpenFirewall39002 -Port $WssPort"
}

$vcDisplayNames = @(
  "Microsoft Visual C++ 2015-2022 Redistributable (x64)",
  "Microsoft Visual C++ 2015-2022 Redistributable",
  "Microsoft Visual C++ 2015-2019 Redistributable (x64)",
  "Microsoft Visual C++ 2015-2019 Redistributable"
)

$vcEntries = @()
$uninstallRoots = @(
  "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*",
  "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*"
)

foreach ($root in $uninstallRoots) {
  $vcEntries += Get-ItemProperty -Path $root -ErrorAction SilentlyContinue |
    Where-Object {
      $null -ne $_.DisplayName -and (
        $vcDisplayNames -contains $_.DisplayName -or
        $_.DisplayName -like "Microsoft Visual C++ 2015-2022 Redistributable*"
      )
    }
}

$vcRuntimeKeyPaths = @(
  "HKLM:\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64",
  "HKLM:\SOFTWARE\WOW6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes\x64"
)

$vcRuntimeDetected = $false
$vcRuntimeVersion = $null

foreach ($keyPath in $vcRuntimeKeyPaths) {
  $runtimeEntry = Get-ItemProperty -Path $keyPath -ErrorAction SilentlyContinue
  if ($null -ne $runtimeEntry -and $runtimeEntry.Installed -eq 1) {
    $vcRuntimeDetected = $true
    $vcRuntimeVersion = $runtimeEntry.Version
    break
  }
}

if ($vcEntries.Count -gt 0 -or $vcRuntimeDetected) {
  if ($vcEntries.Count -gt 0) {
    $vcVersion = ($vcEntries | Select-Object -First 1).DisplayVersion
    Write-Check -Status PASS -Message "Visual C++ Redistributable found (version: $vcVersion)"
  }
  else {
    Write-Check -Status PASS -Message "Visual C++ Redistributable runtime detected (version: $vcRuntimeVersion)"
  }
}
else {
  Write-Check -Status WARN -Message "Visual C++ Redistributable (x64) not detected (bridge can still work if already present by policy/runtime image)"
}

$safeCertBase = Get-SafeCertBaseName -Domain $LocalDomain
$certRoot = Resolve-PathUnderRoot -Root $PSScriptRoot -PathValue $CertDir
$certPath = Join-Path $certRoot "$safeCertBase.pem"
$keyPath = Join-Path $certRoot "$safeCertBase-key.pem"
$rootCaPath = Join-Path $certRoot "rootCA.pem"

if (Test-Path $certPath) {
  Write-Check -Status PASS -Message "WSS certificate found: $certPath"
}
else {
  Write-Check -Status WARN -Message "WSS certificate missing: $certPath"
}

if (Test-Path $keyPath) {
  Write-Check -Status PASS -Message "WSS key found: $keyPath"
}
else {
  Write-Check -Status WARN -Message "WSS key missing: $keyPath"
}

if (Test-Path $rootCaPath) {
  Write-Check -Status PASS -Message "Root CA export found: $rootCaPath"
}
else {
  Write-Check -Status WARN -Message "Root CA export missing: $rootCaPath (listener bootstrap scripts may fail)"
}

$mkcertPath = Find-MkcertPath
if ($null -ne $mkcertPath) {
  Write-Check -Status PASS -Message "mkcert found: $mkcertPath"
}
else {
  Write-Check -Status WARN -Message "mkcert not found (needed for first-time WSS certificate setup)"
  Write-Host "  -> Repair: Install mkcert then run:"
  Write-Host "     .\setup-wss-cert-v0.ps1 -LocalDomain $LocalDomain -CertDir `"$CertDir`""
}

Test-PortAvailability -RulePort $Port
if ($WssPort -ne $Port) {
  Test-PortAvailability -RulePort $WssPort
}

Write-Host ""
Write-Host "Summary: PASS/FAIL/WARN checks complete" -ForegroundColor Cyan
Write-Host "  Failures: $($script:FailCount)"
Write-Host "  Warnings: $($script:WarnCount)"
Write-Host ""

if ($script:FailCount -gt 0) {
  Write-Host "Blocking issues found. Fix FAIL items before running bridge." -ForegroundColor Red
}
else {
  Write-Host "No blocking issues found. Next step:" -ForegroundColor Green
  Write-Host "  .\\run-bridge.ps1"
}

if ($Strict -and $script:FailCount -gt 0) {
  exit 1
}

exit 0
