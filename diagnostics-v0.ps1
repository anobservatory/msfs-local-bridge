param(
  [int]$Port = 39000,
  [ValidateSet("Text", "Json")]
  [string]$Format = "Text"
)

$ErrorActionPreference = "Stop"

$script:Checks = New-Object System.Collections.Generic.List[object]

function Add-Check {
  param(
    [string]$Id,
    [ValidateSet("pass", "warn", "fail")]
    [string]$Status,
    [string]$Message,
    [string]$RepairAction = ""
  )

  $script:Checks.Add([pscustomobject]@{
    id = $Id
    status = $Status
    message = $Message
    repairAction = $RepairAction
  }) | Out-Null
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

function Write-TextCheck {
  param(
    [string]$Status,
    [string]$Message
  )

  switch ($Status) {
    "pass" { Write-Host "[PASS] $Message" -ForegroundColor Green }
    "warn" { Write-Host "[WARN] $Message" -ForegroundColor Yellow }
    "fail" { Write-Host "[FAIL] $Message" -ForegroundColor Red }
  }
}

function Get-CountByStatus {
  param([string]$Status)
  return @($script:Checks | Where-Object { $_.status -eq $Status }).Count
}

$projectRoot = $PSScriptRoot
$projectFile = Join-Path $projectRoot "MsfsLocalBridge.csproj"
$releaseExe = Join-Path $projectRoot "MsfsLocalBridge.exe"
$releaseLayout = (Test-Path $releaseExe) -and (-not (Test-Path $projectFile))

$managedDll = Join-Path $projectRoot "lib\Microsoft.FlightSimulator.SimConnect.dll"
$nativeDll = Join-Path $projectRoot "lib\SimConnect.dll"
$managedRootDll = Join-Path $projectRoot "Microsoft.FlightSimulator.SimConnect.dll"
$nativeRootDll = Join-Path $projectRoot "SimConnect.dll"

if (Test-Path $managedDll) {
  Add-Check -Id "simconnect.managed_dll" -Status "pass" -Message "Managed SimConnect DLL found: $managedDll"
}
else {
  Add-Check -Id "simconnect.managed_dll" -Status "fail" -Message "Managed SimConnect DLL missing: $managedDll" -RepairAction "Copy Microsoft.FlightSimulator.SimConnect.dll into lib folder."
}

if (Test-Path $nativeDll) {
  Add-Check -Id "simconnect.native_dll" -Status "pass" -Message "Native SimConnect DLL found: $nativeDll"
}
else {
  Add-Check -Id "simconnect.native_dll" -Status "fail" -Message "Native SimConnect DLL missing: $nativeDll" -RepairAction "Copy SimConnect.dll into lib folder."
}

if ($releaseLayout) {
  if (Test-Path $managedRootDll) {
    Add-Check -Id "release.root_managed_dll" -Status "pass" -Message "Release root managed DLL found: $managedRootDll"
  }
  else {
    Add-Check -Id "release.root_managed_dll" -Status "fail" -Message "Release root managed DLL missing: $managedRootDll" -RepairAction "Copy managed DLL from lib to release root."
  }

  if (Test-Path $nativeRootDll) {
    Add-Check -Id "release.root_native_dll" -Status "pass" -Message "Release root native DLL found: $nativeRootDll"
  }
  else {
    Add-Check -Id "release.root_native_dll" -Status "fail" -Message "Release root native DLL missing: $nativeRootDll" -RepairAction "Copy native DLL from lib to release root."
  }
}

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if ($releaseLayout) {
  if ($null -eq $dotnet) {
    Add-Check -Id "runtime.dotnet" -Status "pass" -Message "dotnet not found (expected for self-contained release)"
  }
  else {
    Add-Check -Id "runtime.dotnet" -Status "pass" -Message "dotnet found (optional for release): $($dotnet.Source)"
  }
}
else {
  if ($null -eq $dotnet) {
    Add-Check -Id "runtime.dotnet" -Status "fail" -Message "dotnet SDK not found (required for source layout)" -RepairAction "Install .NET 8 SDK or use release zip."
  }
  else {
    Add-Check -Id "runtime.dotnet" -Status "pass" -Message "dotnet found: $($dotnet.Source)"
  }
}

if ([Environment]::Is64BitOperatingSystem) {
  Add-Check -Id "runtime.os_x64" -Status "pass" -Message "OS architecture is x64"
}
else {
  Add-Check -Id "runtime.os_x64" -Status "fail" -Message "OS architecture is not x64" -RepairAction "Use x64 Windows environment."
}

if ([Environment]::Is64BitProcess) {
  Add-Check -Id "runtime.shell_x64" -Status "pass" -Message "PowerShell process is x64"
}
else {
  Add-Check -Id "runtime.shell_x64" -Status "warn" -Message "PowerShell process is not x64" -RepairAction "Use x64 PowerShell."
}

$isAdmin = Test-IsAdministrator
if ($isAdmin) {
  Add-Check -Id "runtime.standard_user" -Status "warn" -Message "Running elevated. Standard-user mode is recommended." -RepairAction "Use normal PowerShell unless running explicit repair action."
}
else {
  Add-Check -Id "runtime.standard_user" -Status "pass" -Message "Running as standard user (recommended)"
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
foreach ($keyPath in $vcRuntimeKeyPaths) {
  $runtimeEntry = Get-ItemProperty -Path $keyPath -ErrorAction SilentlyContinue
  if ($null -ne $runtimeEntry -and $runtimeEntry.Installed -eq 1) {
    $vcRuntimeDetected = $true
    break
  }
}

if ($vcEntries.Count -gt 0 -or $vcRuntimeDetected) {
  Add-Check -Id "dependency.vc_redist_x64" -Status "pass" -Message "Visual C++ Redistributable runtime detected"
}
else {
  Add-Check -Id "dependency.vc_redist_x64" -Status "warn" -Message "Visual C++ Redistributable (x64) not detected" -RepairAction "Install Microsoft Visual C++ 2015-2022 Redistributable (x64)."
}

$portLines = @(netstat -ano | Select-String ":$Port")
$listeningLines = @($portLines | Where-Object { $_.Line -match "LISTENING" })
if ($listeningLines.Count -eq 0) {
  Add-Check -Id "network.port_$Port" -Status "pass" -Message "TCP $Port is free (no current LISTENING process)"
}
else {
  $conflict = ($listeningLines[0].Line -replace "\s+", " ").Trim()
  $parts = $conflict.Split(" ")
  $pidText = $parts[-1]
  $parsedPid = 0
  $processName = ""

  if ([int]::TryParse($pidText, [ref]$parsedPid)) {
    $process = Get-Process -Id $parsedPid -ErrorAction SilentlyContinue
    if ($null -ne $process) {
      $processName = $process.ProcessName
    }
  }

  if ($processName -eq "node") {
    Add-Check -Id "network.port_$Port" -Status "warn" -Message "TCP $Port is in use by node/mock sender" -RepairAction "Stop mock sender process using TCP $Port."
  }
  elseif ($processName) {
    Add-Check -Id "network.port_$Port" -Status "warn" -Message "TCP $Port is in use by $processName (PID $parsedPid)" -RepairAction "Stop conflicting process or use another port."
  }
  else {
    Add-Check -Id "network.port_$Port" -Status "warn" -Message "TCP $Port is already in use: $conflict" -RepairAction "Stop conflicting process or use another port."
  }
}

$passCount = Get-CountByStatus -Status "pass"
$warnCount = Get-CountByStatus -Status "warn"
$failCount = Get-CountByStatus -Status "fail"

$overallStatus = "pass"
if ($failCount -gt 0) {
  $overallStatus = "fail"
}
elseif ($warnCount -gt 0) {
  $overallStatus = "warn"
}

$report = [pscustomobject]@{
  name = "msfs-local-bridge-diagnostics"
  overallStatus = $overallStatus
  generatedAt = (Get-Date).ToString("o")
  summary = [pscustomobject]@{
    pass = $passCount
    warn = $warnCount
    fail = $failCount
  }
  checks = $script:Checks
}

if ($Format -eq "Json") {
  $report | ConvertTo-Json -Depth 6
}
else {
  Write-Host "MSFS Local Bridge Diagnostics" -ForegroundColor Cyan
  Write-Host "Project: $projectRoot"
  Write-Host ""
  foreach ($check in $script:Checks) {
    Write-TextCheck -Status $check.status -Message $check.message
    if ($check.repairAction) {
      Write-Host "  -> Repair: $($check.repairAction)"
    }
  }
  Write-Host ""
  Write-Host "Summary: pass=$passCount warn=$warnCount fail=$failCount (overall=$overallStatus)" -ForegroundColor Cyan
}

if ($failCount -gt 0) {
  exit 1
}

exit 0
