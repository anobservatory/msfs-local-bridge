[CmdletBinding()]
param(
  [string]$Version = '0.2.23',
  [ValidateSet('Debug', 'Release')]
  [string]$Configuration = 'Release',
  [ValidateSet('lite', 'self-contained')]
  [string]$Package = 'lite',
  [switch]$BuildNativeWorker
)

$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)(?:[-+][0-9A-Za-z.-]+)?$') {
  throw 'Use a semantic version such as 0.2.14 or 0.2.15-preview.1.'
}
$assemblyVersion = "$($Matches.major).$($Matches.minor).$($Matches.patch).0"
$root = $PSScriptRoot
$project = Join-Path $root 'desktop\MsfsLocalBridge\MsfsLocalBridge.Desktop.csproj'
$packageName = "msfs-local-bridge-v$Version-windows-x64-$Package"
$dist = [IO.Path]::GetFullPath((Join-Path $root 'dist\windows'))
$output = [IO.Path]::GetFullPath((Join-Path $dist $packageName))
$zip = Join-Path $dist "$packageName.zip"

# Only remove this generated package, after validating its absolute destination.
if (-not $output.StartsWith($dist + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
  throw "Package output is outside the distribution directory: $output"
}
if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Recurse -Force }
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
New-Item -ItemType Directory -Force -Path $output | Out-Null

if ($BuildNativeWorker) {
  $worker = Join-Path $root 'workers\simconnect-native'
  & cmake -S $worker -B (Join-Path $worker 'build') -A x64
  if ($LASTEXITCODE -ne 0) { throw 'Native worker configuration failed.' }
  & cmake --build (Join-Path $worker 'build') --config $Configuration
  if ($LASTEXITCODE -ne 0) { throw 'Native worker build failed.' }
}

$selfContained = if ($Package -eq 'self-contained') { 'true' } else { 'false' }
& dotnet publish $project -c $Configuration -r win-x64 --self-contained $selfContained `
  "-p:BundledBridgeSelfContained=$selfContained" `
  "-p:Version=$Version" "-p:AssemblyVersion=$assemblyVersion" `
  "-p:FileVersion=$assemblyVersion" "-p:InformationalVersion=$Version" `
  -p:NuGetAudit=false -o $output
if ($LASTEXITCODE -ne 0) { throw 'Windows desktop publish failed.' }

if (-not (Test-Path -LiteralPath (Join-Path $output 'bridge\MsfsLocalBridge.exe'))) {
  throw 'Published package is missing its bundled bridge.'
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($output, $zip)
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$zip.sha256", "$hash  $packageName.zip`n")
Write-Host "Portable app: $output"
Write-Host "ZIP: $zip"
Write-Host "SHA256: $hash"
