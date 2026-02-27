param(
  [Parameter(ValueFromRemainingArguments = $true)]
  [string[]]$Args
)

$ErrorActionPreference = "Stop"

$starter = Join-Path $PSScriptRoot "start-msfs-sync.ps1"
if (-not (Test-Path $starter)) {
  throw "Missing required script: $starter"
}

# One-click default entrypoint for testers.
# Advanced options remain available via start-msfs-sync.ps1 directly.
$defaultArgs = @(
  "-LocalDomain", "ao.home.arpa",
  "-RequireWss"
)

if ($Args -and $Args.Count -gt 0) {
  $defaultArgs += $Args
}

& $starter @defaultArgs
exit $LASTEXITCODE
