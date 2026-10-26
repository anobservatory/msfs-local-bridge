[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)]
  [string]$PackageDirectory,
  [string]$TestWorkerPath
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $PackageDirectory).Path
$required = @(
  'MsfsLocalBridge.exe', 'MsfsLocalBridge.runtimeconfig.json', 'WebView2Loader.dll',
  'resources\host-console\index.html', 'resources\host-console\app.js',
  'resources\host-console\styles.css', 'bridge\MsfsLocalBridge.exe',
  'bridge\MsfsLocalBridge.runtimeconfig.json', 'bridge\SimConnect.dll',
  'bridge\Microsoft.FlightSimulator.SimConnect.dll', 'bridge\lib\SimConnect.dll',
  'bridge\lib\Microsoft.FlightSimulator.SimConnect.dll', 'bridge\run-bridge.ps1',
  'bridge\diagnostics-v0.ps1', 'bridge\repair-elevated-v0.ps1', 'bridge\preflight-v0.ps1'
)
foreach ($file in $required) {
  if (-not (Test-Path -LiteralPath (Join-Path $root $file))) { throw "Missing package file: $file" }
}
if (Test-Path -LiteralPath (Join-Path $root 'bridge\desktop')) { throw 'Desktop sources leaked into the bridge payload.' }
Write-Host 'PASS: Standalone desktop/bridge package contents.'

$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$listener.Start()
$port = $listener.LocalEndpoint.Port
$listener.Stop()
$info = [Diagnostics.ProcessStartInfo]::new((Join-Path $root 'bridge\MsfsLocalBridge.exe'))
$info.WorkingDirectory = Join-Path $root 'bridge'
$info.UseShellExecute = $false
$info.CreateNoWindow = $true
$info.RedirectStandardOutput = $true
$info.RedirectStandardError = $true
$info.Environment['MSFS_BRIDGE_BIND'] = '127.0.0.1'
$info.Environment['MSFS_BRIDGE_PORT'] = "$port"
$info.Environment['MSFS_BRIDGE_WSS_ENABLED'] = 'false'
$info.Environment['MSFS_BRIDGE_BOOTSTRAP_ENABLED'] = 'false'
$info.Environment['MSFS_BRIDGE_SIMCONNECT_MODE'] = 'embedded'
if ($TestWorkerPath) {
  $info.Environment['MSFS_BRIDGE_SIMCONNECT_MODE'] = 'worker'
  $info.Environment['MSFS_BRIDGE_SIMCONNECT_WORKER_PATH'] = (Resolve-Path -LiteralPath $TestWorkerPath).Path
  $info.Environment['MSFS_BRIDGE_SIMCONNECT_WORKER_ARGS'] = '--test-worker'
}
$process = [Diagnostics.Process]::new()
$process.StartInfo = $info
$socket = [Net.WebSockets.ClientWebSocket]::new()
try {
  if (-not $process.Start()) { throw 'Bridge process did not start.' }
  $stdout = $process.StandardOutput.ReadToEndAsync()
  $stderr = $process.StandardError.ReadToEndAsync()
  $deadline = [DateTime]::UtcNow.AddSeconds(20)
  $health = $null
  do {
    if ($process.HasExited) { throw "Bridge exited: $($stderr.GetAwaiter().GetResult()) $($stdout.GetAwaiter().GetResult())" }
    try { $health = Invoke-RestMethod -Uri "http://127.0.0.1:$port/" -TimeoutSec 2 } catch { Start-Sleep -Milliseconds 200 }
  } while (-not $health -and [DateTime]::UtcNow -lt $deadline)
  if (-not $health -or $health.name -ne 'msfs-local-bridge') { throw 'Bridge health endpoint failed.' }
  if ($health.wssEnabled) { throw 'Default package unexpectedly enabled WSS.' }
  Write-Host "PASS: Packaged bridge HTTP health ($($health.simConnectMode))."

  $timeout = [Threading.CancellationTokenSource]::new(10000)
  try {
    $null = $socket.ConnectAsync([Uri]"ws://127.0.0.1:$port/stream", $timeout.Token).GetAwaiter().GetResult()
    if ($socket.State -ne [Net.WebSockets.WebSocketState]::Open) { throw 'WebSocket handshake failed.' }
    Write-Host 'PASS: Packaged bridge WebSocket handshake.'
    if ($TestWorkerPath) {
      $buffer = [byte[]]::new(65536)
      $message = [IO.MemoryStream]::new()
      try {
        do {
          $received = $socket.ReceiveAsync([ArraySegment[byte]]::new($buffer), $timeout.Token).GetAwaiter().GetResult()
          if ($received.MessageType -ne [Net.WebSockets.WebSocketMessageType]::Text) { throw 'Expected a text telemetry frame.' }
          $message.Write($buffer, 0, $received.Count)
        } while (-not $received.EndOfMessage)
        $frame = [Text.Encoding]::UTF8.GetString($message.ToArray()) | ConvertFrom-Json
        if ($frame.source -ne 'msfs_local' -or $frame.version -ne 1 -or $frame.ownship.callsign -ne 'AOTEST' -or $frame.ownship.lat -ne 37.6189) {
          throw 'IPC fixture telemetry did not match the browser stream contract.'
        }
        if (-not $frame.sessionMeta.companionVersion) { throw 'Bridge version metadata is missing.' }
        Write-Host "PASS: Test fixture worker -> packaged bridge -> telemetry v1 (bridge $($frame.sessionMeta.companionVersion))."
      } finally { $message.Dispose() }
    }
  } finally { $timeout.Dispose() }
} finally {
  $socket.Dispose()
  if ($process.Id -and -not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
  $process.Dispose()
}
