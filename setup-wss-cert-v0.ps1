param(
  [string]$LocalDomain = "ao.home.arpa",
  [string]$CertDir = "certs",
  [switch]$SkipInstall
)

$ErrorActionPreference = "Stop"

$RootCaSubject = "CN=AO MSFS Local Bridge Root CA"
$RootCaFriendlyName = "AO MSFS Local Bridge Root CA"
$ServerFriendlyName = "AO MSFS Local Bridge TLS"
$PfxPasswordPlain = "changeit"

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
  }

  return @($ordered)
}

function Write-CertificatePem {
  param(
    [System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate,
    [string]$Path
  )

  $base64 = [System.Convert]::ToBase64String(
    $Certificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert),
    [System.Base64FormattingOptions]::InsertLineBreaks
  )
  $pem = "-----BEGIN CERTIFICATE-----`r`n$base64`r`n-----END CERTIFICATE-----`r`n"
  [System.IO.File]::WriteAllText($Path, $pem, [System.Text.Encoding]::ASCII)
}

function Write-PrivateKeyPem {
  param(
    [System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate,
    [string]$Path
  )

  $rsa = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($Certificate)
  if ($null -eq $rsa) {
    throw "Certificate does not contain an RSA private key."
  }

  try {
    if ($rsa -is [System.Security.Cryptography.RSA] -and ($rsa | Get-Member -Name ExportPkcs8PrivateKey -MemberType Method -ErrorAction SilentlyContinue)) {
      $pkcs8 = $rsa.ExportPkcs8PrivateKey()
      $base64 = [System.Convert]::ToBase64String($pkcs8, [System.Base64FormattingOptions]::InsertLineBreaks)
      $pem = "-----BEGIN PRIVATE KEY-----`r`n$base64`r`n-----END PRIVATE KEY-----`r`n"
      [System.IO.File]::WriteAllText($Path, $pem, [System.Text.Encoding]::ASCII)
      return
    }
  } finally {
    $rsa.Dispose()
  }

  $opensslCandidates = @(
    'C:\\Program Files\\Git\\mingw64\\bin\\openssl.exe',
    'C:\\Program Files\\Git\\usr\\bin\\openssl.exe'
  )
  $opensslPath = $opensslCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
  if ([string]::IsNullOrWhiteSpace($opensslPath)) {
    throw "OpenSSL not found and current PowerShell cannot export PKCS#8 private keys."
  }

  $tempPfx = Join-Path ([System.IO.Path]::GetTempPath()) ("ao-bridge-" + [System.Guid]::NewGuid().ToString("N") + ".p12")
  try {
    [System.IO.File]::WriteAllBytes($tempPfx, $Certificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Pkcs12, $PfxPasswordPlain))
    $raw = & $opensslPath pkcs12 -in $tempPfx -nocerts -nodes -passin "pass:$PfxPasswordPlain"
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($raw)) {
      throw "OpenSSL failed to export the private key."
    }
    $pemLines = @($raw -split "`r?`n") | Where-Object { $_ -match "^-+BEGIN PRIVATE KEY-+$" -or $_ -match "^-+END PRIVATE KEY-+$" -or $_ -match "^[A-Za-z0-9+/=]+$" }
    [System.IO.File]::WriteAllText($Path, (($pemLines -join "`r`n") + "`r`n"), [System.Text.Encoding]::ASCII)
  } finally {
    Remove-Item $tempPfx -Force -ErrorAction SilentlyContinue
  }
}

function Load-PfxCertificateFromFile {
  param([string]$Path)
  return New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 -ArgumentList @($Path, $PfxPasswordPlain)
}
function Find-RootCaCertificate {
  $store = New-Object System.Security.Cryptography.X509Certificates.X509Store('My', 'CurrentUser')
  $store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly)
  try {
    return @($store.Certificates |
      Where-Object { $_.Subject -eq $RootCaSubject -and $_.HasPrivateKey } |
      Sort-Object NotBefore -Descending |
      Select-Object -First 1)
  }
  finally {
    $store.Close()
  }
}

function Ensure-CertificateInStore {
  param(
    [System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate,
    [string]$StoreName,
    [string]$StoreLocation,
    [switch]$PublicOnly
  )

  $candidate = $Certificate
  if ($PublicOnly) {
    $candidate = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 -ArgumentList @(,$Certificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))
  }

  $store = New-Object System.Security.Cryptography.X509Certificates.X509Store($StoreName, $StoreLocation)
  $store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
  try {
    $matches = $store.Certificates.Find(
      [System.Security.Cryptography.X509Certificates.X509FindType]::FindByThumbprint,
      $candidate.Thumbprint,
      $false
    )
    if ($matches.Count -eq 0) {
      $store.Add($candidate)
      return $true
    }

    return $false
  }
  finally {
    $store.Close()
    if ($PublicOnly) {
      $candidate.Dispose()
    }
  }
}

function New-RandomSerialNumber {
  param([int]$Length = 16)
  $bytes = New-Object byte[] $Length
  $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
  try {
    $rng.GetBytes($bytes)
  }
  finally {
    $rng.Dispose()
  }
  return $bytes
}

function New-RootCaCertificate {
  $rsa = [System.Security.Cryptography.RSA]::Create(4096)
  $request = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new(
    $RootCaSubject,
    $rsa,
    [System.Security.Cryptography.HashAlgorithmName]::SHA256,
    [System.Security.Cryptography.RSASignaturePadding]::Pkcs1
  )

  $basicConstraints = [System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($true, $true, 1, $true)
  $keyUsageFlags = [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::KeyCertSign -bor
    [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::CrlSign -bor
    [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature
  $keyUsage = [System.Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new($keyUsageFlags, $true)
  $subjectKeyId = [System.Security.Cryptography.X509Certificates.X509SubjectKeyIdentifierExtension]::new($request.PublicKey, $false)

  $request.CertificateExtensions.Add($basicConstraints)
  $request.CertificateExtensions.Add($keyUsage)
  $request.CertificateExtensions.Add($subjectKeyId)

  $certificate = $request.CreateSelfSigned((Get-Date).AddDays(-1), (Get-Date).AddYears(10))
  $certificate.FriendlyName = $RootCaFriendlyName
  return $certificate
}

function New-ServerCertificate {
  param(
    [System.Security.Cryptography.X509Certificates.X509Certificate2]$RootCaCertificate,
    [string]$SubjectName,
    [string[]]$DnsNames,
    [string[]]$IpAddresses
  )

  $rsa = [System.Security.Cryptography.RSA]::Create(2048)
  $request = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new(
    "CN=$SubjectName",
    $rsa,
    [System.Security.Cryptography.HashAlgorithmName]::SHA256,
    [System.Security.Cryptography.RSASignaturePadding]::Pkcs1
  )

  $basicConstraints = [System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($false, $false, 0, $true)
  $keyUsageFlags = [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature -bor
    [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::KeyEncipherment
  $keyUsage = [System.Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new($keyUsageFlags, $true)
  $ekuOids = New-Object System.Security.Cryptography.OidCollection
  [void]$ekuOids.Add((New-Object System.Security.Cryptography.Oid '1.3.6.1.5.5.7.3.1', 'Server Authentication'))
  $eku = [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($ekuOids, $false)
  $subjectKeyId = [System.Security.Cryptography.X509Certificates.X509SubjectKeyIdentifierExtension]::new($request.PublicKey, $false)
  $sanBuilder = [System.Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder]::new()

  foreach ($dnsName in ($DnsNames | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique)) {
    $sanBuilder.AddDnsName($dnsName)
  }

  foreach ($ipAddress in ($IpAddresses | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique)) {
    $sanBuilder.AddIpAddress([System.Net.IPAddress]::Parse($ipAddress))
  }

  $request.CertificateExtensions.Add($basicConstraints)
  $request.CertificateExtensions.Add($keyUsage)
  $request.CertificateExtensions.Add($eku)
  $request.CertificateExtensions.Add($subjectKeyId)
  $request.CertificateExtensions.Add($sanBuilder.Build($true))

  $temporary = $request.Create($RootCaCertificate, (Get-Date).AddDays(-1), (Get-Date).AddYears(2), (New-RandomSerialNumber))
  $certificate = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::CopyWithPrivateKey($temporary, $rsa)
  $temporary.Dispose()
  $certificate.FriendlyName = $ServerFriendlyName
  return $certificate
}

function Ensure-RootCaCertificate {
  param(
    [string]$RootCaPfxPath,
    [switch]$PersistToStore
  )

  if ($PersistToStore) {
    try {
      $existing = Find-RootCaCertificate
      if ($null -ne $existing) {
        return $existing
      }
    }
    catch {
      Write-Host "[WARN] Unable to read CurrentUser\\My certificate store. Continuing with file-backed certificate mode." -ForegroundColor Yellow
    }
  }

  if (Test-Path $RootCaPfxPath) {
    try {
      $loaded = Load-PfxCertificateFromFile -Path $RootCaPfxPath
      if ($PersistToStore) {
        try {
          [void](Ensure-CertificateInStore -Certificate $loaded -StoreName 'My' -StoreLocation 'CurrentUser')
        }
        catch {
          Write-Host "[WARN] Unable to persist root CA into CurrentUser\\My. Continuing with file-backed certificate mode." -ForegroundColor Yellow
        }
      }
      return $loaded
    }
    catch {
      Write-Host "[WARN] Existing root CA backup at '$RootCaPfxPath' could not be loaded. Generating a new root CA." -ForegroundColor Yellow
    }
  }

  $created = New-RootCaCertificate
  if ($PersistToStore) {
    try {
      [void](Ensure-CertificateInStore -Certificate $created -StoreName 'My' -StoreLocation 'CurrentUser')
    }
    catch {
      Write-Host "[WARN] Unable to persist root CA into CurrentUser\\My. Continuing with file-backed certificate mode." -ForegroundColor Yellow
    }
  }
  return $created
}

function Ensure-TrustedRoot {
  param([System.Security.Cryptography.X509Certificates.X509Certificate2]$RootCaCertificate)
  return Ensure-CertificateInStore -Certificate $RootCaCertificate -StoreName 'Root' -StoreLocation 'CurrentUser' -PublicOnly
}

$safeBase = Get-SafeCertBaseName -Domain $LocalDomain
$certRoot = Resolve-PathUnderRoot -Root $PSScriptRoot -PathValue $CertDir
New-Item -ItemType Directory -Path $certRoot -Force | Out-Null

$certPath = Join-Path $certRoot "$safeBase.pem"
$keyPath = Join-Path $certRoot "$safeBase-key.pem"
$pfxPath = Join-Path $certRoot "$safeBase.p12"
$rootCaExportPath = Join-Path $certRoot 'rootCA.pem'
$rootCaPfxPath = Join-Path $certRoot 'rootCA.p12'
$lanIps = @(Get-PrivateLanIPv4)
$dnsNames = @($LocalDomain, 'localhost')
$ipNames = @('127.0.0.1', '::1') + $lanIps
$subjects = @($dnsNames + $ipNames | Select-Object -Unique)

Write-Host 'MSFS Local Bridge WSS Certificate Setup' -ForegroundColor Cyan
Write-Host "  domain: $LocalDomain"
Write-Host "  cert:   $certPath"
Write-Host "  key:    $keyPath"
Write-Host "  pfx:    $pfxPath"
Write-Host "  rootCA: $rootCaExportPath"
Write-Host ''

$rootCaCertificate = Ensure-RootCaCertificate -RootCaPfxPath $rootCaPfxPath -PersistToStore:(-not $SkipInstall)
[System.IO.File]::WriteAllBytes($rootCaPfxPath, $rootCaCertificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Pkcs12, $PfxPasswordPlain))
Write-CertificatePem -Certificate $rootCaCertificate -Path $rootCaExportPath

if (-not $SkipInstall) {
  try {
    $installed = Ensure-TrustedRoot -RootCaCertificate $rootCaCertificate
    if ($installed) {
      Write-Host 'Installed local root CA into CurrentUser\Root.'
    }
    else {
      Write-Host 'Local root CA already trusted in CurrentUser\Root.'
    }
  }
  catch {
    Write-Host '[WARN] Unable to trust root CA automatically in CurrentUser\Root.' -ForegroundColor Yellow
    Write-Host '       Certificate files were still generated. You may need a manual trust/import step on this PC.' -ForegroundColor Yellow
  }
}

$serverCertificate = New-ServerCertificate -RootCaCertificate $rootCaCertificate -SubjectName $LocalDomain -DnsNames $dnsNames -IpAddresses $ipNames
[System.IO.File]::WriteAllBytes($pfxPath, $serverCertificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Pkcs12, $PfxPasswordPlain))
Write-CertificatePem -Certificate $serverCertificate -Path $certPath
Write-PrivateKeyPem -Certificate $serverCertificate -Path $keyPath

Write-Host ''
Write-Host '[PASS] WSS certificate bundle generated.' -ForegroundColor Green
Write-Host "Subjects: $($subjects -join ', ')"
Write-Host "Root CA backup: $rootCaPfxPath"

if ($lanIps.Count -gt 0) {
  Write-Host ''
  Write-Host 'Recommended listener connect target (certificate includes LAN IP SAN):' -ForegroundColor Yellow
  Write-Host "  wss://$($lanIps[0]):39002/stream"
  Write-Host ''
  Write-Host 'Fallback target (requires domain mapping):' -ForegroundColor Yellow
  Write-Host "  wss://$LocalDomain`:39002/stream"
}

Write-Host ''
Write-Host 'Run bridge:'
Write-Host "  .\start-msfs-sync.ps1 -LocalDomain $LocalDomain"
