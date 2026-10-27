[CmdletBinding()]
param(
  [string]$RepositoryRoot = (Join-Path $PSScriptRoot '..'),
  [switch]$FailIfOutdated
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$source = [IO.File]::ReadAllText((Join-Path $root 'desktop\MsfsLocalBridge\Services\PrerequisiteInstallerService.cs'))
$channelMatch = [regex]::Match($source, 'RequiredDotNetChannel\s*=\s*"(?<value>\d+\.\d+)"')
$versionMatch = [regex]::Match($source, 'RecommendedDotNetRuntimeVersion\s*=\s*"(?<value>\d+\.\d+\.\d+)"')
if (-not $channelMatch.Success -or -not $versionMatch.Success) { throw 'Runtime policy constants were not found.' }
$channel = $channelMatch.Groups['value'].Value
$recommended = [version]$versionMatch.Groups['value'].Value
$metadataUrl = "https://builds.dotnet.microsoft.com/dotnet/release-metadata/$channel/releases.json"
$metadata = Invoke-RestMethod -Uri $metadataUrl -TimeoutSec 30
if ($metadata.'channel-version' -ne $channel -or $metadata.'latest-runtime' -notmatch '^\d+\.\d+\.\d+$') {
  throw 'Unexpected runtime channel metadata.'
}
$latest = [version]$metadata.'latest-runtime'
if ("$($latest.Major).$($latest.Minor)" -ne $channel) { throw 'Latest runtime is outside the supported channel.' }
$release = $metadata.releases | Where-Object { $_.'release-version' -eq "$latest" } | Select-Object -First 1
if (-not $release) { throw 'Latest stable runtime release was not found.' }
$installers = @()
foreach ($component in @('windowsdesktop', 'aspnetcore-runtime')) {
  if ($release.$component.version -ne "$latest") { throw "Unexpected component version: $component" }
  $file = @($release.$component.files | Where-Object { $_.rid -eq 'win-x64' -and $_.name -like '*.exe' })
  if ($file.Count -ne 1 -or $file[0].hash -notmatch '^[0-9a-fA-F]{128}$') { throw "Invalid Windows x64 installer metadata: $component" }
  $uri = [Uri]$file[0].url
  if ($uri.Scheme -ne 'https' -or $uri.Host -ne 'builds.dotnet.microsoft.com') { throw 'Unexpected installer download host.' }
  $installers += [pscustomobject]@{ component = $component; url = $uri.AbsoluteUri; sha512 = $file[0].hash }
}
$outdated = $latest -gt $recommended
[pscustomobject]@{
  channel = $channel
  recommendedVersion = "$recommended"
  latestStableVersion = "$latest"
  releaseDate = $release.'release-date'
  updateAvailable = $outdated
  metadataUrl = $metadataUrl
  installers = $installers
  note = 'Read-only release check. Does not download installers, modify source, or install/update software.'
} | ConvertTo-Json -Depth 4
if ($FailIfOutdated -and $outdated) { exit 1 }
