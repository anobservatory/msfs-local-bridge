param(
  [string]$LocalDomain = "ao.home.arpa",
  [string]$CertDir = "certs",
  [switch]$SkipInstall
)

$ErrorActionPreference = "Stop"

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
    # Ignore network probe failures.
  }

  return @($ordered)
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

$mkcertPath = Find-MkcertPath
if ($null -eq $mkcertPath) {
  Write-Host "[FAIL] mkcert is not installed or not in PATH." -ForegroundColor Red
  Write-Host "Install one of these options and rerun:" -ForegroundColor Yellow
  Write-Host "  winget install FiloSottile.mkcert"
  Write-Host "  choco install mkcert"
  Write-Host ""
  Write-Host "Then run:"
  Write-Host "  .\\setup-wss-cert-v0.ps1 -LocalDomain $LocalDomain"
  exit 1
}

$safeBase = Get-SafeCertBaseName -Domain $LocalDomain
$certRoot = Resolve-PathUnderRoot -Root $PSScriptRoot -PathValue $CertDir
New-Item -ItemType Directory -Path $certRoot -Force | Out-Null

$certPath = Join-Path $certRoot "$safeBase.pem"
$keyPath = Join-Path $certRoot "$safeBase-key.pem"
$pfxPath = Join-Path $certRoot "$safeBase.p12"
$rootCaExportPath = Join-Path $certRoot "rootCA.pem"
$lanIps = @(Get-PrivateLanIPv4)
$subjects = @($LocalDomain, "localhost", "127.0.0.1", "::1")
if ($lanIps.Count -gt 0) {
  $subjects += $lanIps
}
$subjects = @($subjects | Select-Object -Unique)

Write-Host "MSFS Local Bridge WSS Certificate Setup" -ForegroundColor Cyan
Write-Host "  domain: $LocalDomain"
Write-Host "  cert:   $certPath"
Write-Host "  key:    $keyPath"
Write-Host "  pfx:    $pfxPath"
Write-Host "  mkcert: $mkcertPath"
Write-Host ""

if (-not $SkipInstall) {
  Write-Host "Installing local CA (mkcert -install)..."
  & $mkcertPath -install
  if ($LASTEXITCODE -ne 0) {
    throw "mkcert -install failed with exit code $LASTEXITCODE"
  }
}

Write-Host "Generating PEM certificate..."
& $mkcertPath -cert-file $certPath -key-file $keyPath @subjects
if ($LASTEXITCODE -ne 0) {
  throw "mkcert certificate generation failed with exit code $LASTEXITCODE"
}

Write-Host "Generating PKCS#12 bundle..."
& $mkcertPath -pkcs12 -p12-file $pfxPath @subjects
if ($LASTEXITCODE -ne 0) {
  throw "mkcert PKCS#12 generation failed with exit code $LASTEXITCODE"
}

try {
  $caRoot = (& $mkcertPath -CAROOT).Trim()
  if ($caRoot) {
    $rootCaSourcePath = Join-Path $caRoot "rootCA.pem"
    if (Test-Path $rootCaSourcePath) {
      Copy-Item -Path $rootCaSourcePath -Destination $rootCaExportPath -Force
    }
  }
}
catch {
  Write-Host "[WARN] Could not export root CA automatically: $($_.Exception.Message)" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "[PASS] WSS certificate bundle generated." -ForegroundColor Green
Write-Host "Subjects: $($subjects -join ', ')"
if (Test-Path $rootCaExportPath) {
  Write-Host "Root CA: $rootCaExportPath"
}

if ($lanIps.Count -gt 0) {
  Write-Host ""
  Write-Host "Recommended listener connect target (no hosts mapping required when cert includes LAN IP SAN):" -ForegroundColor Yellow
  Write-Host "  wss://$($lanIps[0]):39002/stream"
  Write-Host ""
  Write-Host "Fallback target (requires domain mapping):" -ForegroundColor Yellow
  Write-Host "  wss://$LocalDomain`:39002/stream"
}

Write-Host ""
Write-Host "Run bridge:"
Write-Host "  .\\start-msfs-sync.ps1 -LocalDomain $LocalDomain"


