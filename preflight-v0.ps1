param(
  [int]$Port = 39000,
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

$portLines = @(netstat -ano | Select-String ":$Port")
$listeningLines = @($portLines | Where-Object { $_.Line -match "LISTENING" })

if ($listeningLines.Count -eq 0) {
  Write-Check -Status PASS -Message "TCP $Port is free (no current LISTENING process)"
}
else {
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
      Write-Check -Status WARN -Message "TCP $Port in use by PID $pid (process not found)"
      continue
    }

    if ($process.ProcessName -eq "node") {
      Write-Check -Status WARN -Message "TCP $Port in use by node.exe (likely mock sender)"
    }
    else {
      Write-Check -Status WARN -Message "TCP $Port already in use by $($process.ProcessName).exe (PID $pid)"
    }
  }
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
