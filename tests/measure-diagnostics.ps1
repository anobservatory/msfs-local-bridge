[CmdletBinding()]
param(
  [string]$RepositoryRoot = (Join-Path $PSScriptRoot '..'),
  [ValidateRange(1, 20)][int]$Samples = 3,
  [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$scriptPath = Join-Path $root 'diagnostics-v0.ps1'
$powerShellPath = Join-Path ([Environment]::SystemDirectory) 'WindowsPowerShell\v1.0\powershell.exe'
$measurements = @()
for ($sample = 0; $sample -le $Samples; $sample++) {
  $info = [Diagnostics.ProcessStartInfo]::new($powerShellPath)
  $info.Arguments = "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$scriptPath`" -Format Json"
  $info.WorkingDirectory = $root
  $info.UseShellExecute = $false
  $info.CreateNoWindow = $true
  $info.RedirectStandardOutput = $true
  $info.RedirectStandardError = $true
  $process = [Diagnostics.Process]::new()
  $process.StartInfo = $info
  $timer = [Diagnostics.Stopwatch]::StartNew()
  try {
    if (-not $process.Start()) { throw 'Diagnostics did not start.' }
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    $peakBytes = 0L
    while (-not $process.WaitForExit(50)) {
      $process.Refresh()
      try { $peakBytes = [Math]::Max($peakBytes, $process.WorkingSet64) } catch [InvalidOperationException] { }
    }
    $timer.Stop()
    $report = $stdout.GetAwaiter().GetResult() | ConvertFrom-Json
    if ($report.name -ne 'msfs-local-bridge-diagnostics') { throw 'Invalid diagnostics JSON.' }
    $measurements += [pscustomobject]@{
      warmup = ($sample -eq 0)
      elapsedMs = [Math]::Round($timer.Elapsed.TotalMilliseconds, 1)
      powerShellCpuMs = [Math]::Round($process.TotalProcessorTime.TotalMilliseconds, 1)
      sampledPeakWorkingSetMb = [Math]::Round($peakBytes / 1MB, 1)
      exitCode = $process.ExitCode
      checkCount = @($report.checks).Count
    }
    $null = $stderr.GetAwaiter().GetResult()
  } finally {
    if ($process.Id -and -not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
    $process.Dispose()
  }
}
$measured = @($measurements | Where-Object { -not $_.warmup })
$result = [pscustomobject]@{
  samples = $measurements
  meanElapsedMs = [Math]::Round(($measured | Measure-Object elapsedMs -Average).Average, 1)
  meanPowerShellCpuMs = [Math]::Round(($measured | Measure-Object powerShellCpuMs -Average).Average, 1)
  note = 'Read-only diagnostics. CPU covers PowerShell itself; memory is sampled while it runs. Warmup is excluded from means. No simulator, firewall, or network settings are changed.'
}
$json = $result | ConvertTo-Json -Depth 4
if ($OutputPath) { [IO.File]::WriteAllText([IO.Path]::GetFullPath($OutputPath), $json) }
$json
