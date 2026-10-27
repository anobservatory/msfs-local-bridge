using System.Diagnostics;
using MsfsLocalBridge.Services;

internal static class BridgeSessionChecks
{
    public static async Task RunAsync(string root)
    {
        var failures = new List<string>();
        foreach (var (name, check) in new (string, Func<string, Task>)[]
        {
            ("Repeated start/stop owns one process tree", CheckStartStopAsync),
            ("Restart releases the old process and applies sharing settings", CheckRestartAsync),
            ("Stop during startup completes without a disposed-process error", CheckStopDuringStartAsync),
            ("Window shutdown does not need the blocked UI context", CheckShutdownContextAsync),
            ("Startup failure identifies a port conflict and permits retry", CheckStartupFailureAsync),
            ("A prior port error cannot overwrite a later exit reason", CheckFailureIsolationAsync),
            ("Unexpected clean exit is still reported as a stopped bridge", CheckUnexpectedExitAsync),
            ("Cancelling diagnostics terminates its process tree", CheckCancellationAsync),
        })
        {
            try
            {
                await check(root).WaitAsync(TimeSpan.FromSeconds(20));
                Console.WriteLine($"PASS: {name}.");
            }
            catch (Exception ex)
            {
                failures.Add($"{name}: {ex.Message}");
                Console.Error.WriteLine($"FAIL: {failures[^1]}");
            }
        }
        if (failures.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, failures));
    }

    private static async Task CheckStartStopAsync(string root)
    {
        await using var fixture = new ProcessFixture(root);
        fixture.WriteRunningScript(spawnChild: true);
        await Task.WhenAll(fixture.Session.StartAsync(), fixture.Session.StartAsync(), fixture.Session.StartAsync());
        var parent = await fixture.CaptureAsync("parent.pid");
        var child = await fixture.CaptureAsync("child.pid");
        Require(File.ReadAllLines(fixture.FilePath("launches.txt")).Length == 1, "Repeated Start launched duplicate processes.");
        await fixture.WaitForLogAsync("fixture: bind=127.0.0.1");
        await fixture.Session.StopAsync();
        await fixture.Session.StopAsync();
        await WaitUntilAsync(() => parent.HasExited && child.HasExited);
        Require(!fixture.Session.IsRunning && fixture.Session.LastFailureReason is null, "Intentional Stop was reported as a failure.");
    }

    private static async Task CheckRestartAsync(string root)
    {
        await using var fixture = new ProcessFixture(root);
        fixture.WriteRunningScript(spawnChild: true);
        await fixture.Session.StartAsync();
        var parent = await fixture.CaptureAsync("parent.pid");
        var child = await fixture.CaptureAsync("child.pid");
        fixture.Settings.Save(true);
        await fixture.Session.RestartAsync();
        var replacement = await fixture.CaptureAsync("parent.pid");
        await WaitUntilAsync(() => parent.HasExited && child.HasExited);
        Require(replacement.Id != parent.Id && !replacement.HasExited, "Restart did not replace the process.");
        Require(File.ReadAllLines(fixture.FilePath("launches.txt")).Length == 2, "Restart launched an unexpected number of processes.");
        await fixture.WaitForLogAsync("fixture: bind=0.0.0.0");
        Require(fixture.Session.LastFailureReason is null, "Restart retained an intentional-stop failure.");
        await fixture.CaptureAsync("child.pid");
    }

    private static async Task CheckStopDuringStartAsync(string root)
    {
        await using var fixture = new ProcessFixture(root);
        fixture.WriteRunningScript();
        var starting = fixture.Session.StartAsync();
        var parent = await fixture.CaptureAsync("parent.pid");
        await Task.WhenAll(starting, fixture.Session.StopAsync());
        Require(parent.HasExited && !fixture.Session.IsRunning, "Stop during Start left the bridge running.");
        Require(fixture.Session.LastFailureReason is null, "Stop during Start recorded a spurious failure.");
    }

    private static async Task CheckStartupFailureAsync(string root)
    {
        await using var fixture = new ProcessFixture(root);
        fixture.WriteFailureScript();
        await ExpectPortConflictAsync(fixture.Session);
        Require(!fixture.Session.IsRunning, "Failed startup still reports a running bridge.");
        fixture.WriteRunningScript();
        await fixture.Session.StartAsync();
        await fixture.CaptureAsync("parent.pid");
        Require(fixture.Session.IsRunning && fixture.Session.LastFailureReason is null, "Retry did not clear the startup failure.");
    }

    private static async Task CheckShutdownContextAsync(string root)
    {
        await using var fixture = new ProcessFixture(root);
        fixture.WriteRunningScript();
        using var closeAllowed = new ManualResetEventSlim();
        var closing = Task.Run(() =>
        {
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new NonPumpingContext());
            try
            {
                var starting = fixture.Session.StartAsync();
                if (!closeAllowed.Wait(TimeSpan.FromSeconds(8))) throw new TimeoutException("Fixture did not become ready for window shutdown.");
                fixture.Session.StopAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                starting.WaitAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
                Require(!fixture.Session.IsRunning, "Synchronous window shutdown left a process running.");
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
        });
        try { await fixture.CaptureAsync("parent.pid"); }
        finally { closeAllowed.Set(); }
        await closing;
    }

    private sealed class NonPumpingContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state) { }
    }

    private static async Task CheckFailureIsolationAsync(string root)
    {
        await using var fixture = new ProcessFixture(root);
        fixture.WriteFailureScript();
        await ExpectPortConflictAsync(fixture.Session);
        fixture.WriteRunningScript();
        await fixture.Session.StartAsync();
        var parent = await fixture.CaptureAsync("parent.pid");
        File.WriteAllText(fixture.FilePath("exit.request"), "19");
        await WaitUntilAsync(() => parent.HasExited && fixture.Session.LastExitCode == 19);
        Require(fixture.Session.LastFailureReason == "Bridge exited with code 19.",
            $"Current failure was contaminated by historical output: {fixture.Session.LastFailureReason}");
    }

    private static async Task CheckUnexpectedExitAsync(string root)
    {
        await using var fixture = new ProcessFixture(root);
        fixture.WriteRunningScript();
        await fixture.Session.StartAsync();
        await fixture.CaptureAsync("parent.pid");
        File.WriteAllText(fixture.FilePath("exit.request"), "0");
        await WaitUntilAsync(() => !fixture.Session.IsRunning && fixture.Session.LastExitCode == 0);
        Require(fixture.Session.LastFailureReason == "Bridge exited with code 0.", "Unexpected exit was silently treated as a requested Stop.");
    }

    private static async Task CheckCancellationAsync(string root)
    {
        await using var fixture = new ProcessFixture(root);
        fixture.WriteRunningScript(spawnChild: true);
        using var cancellation = new CancellationTokenSource();
        var running = new PowerShellRunner().RunAsync(fixture.Root, $"-ExecutionPolicy Bypass -File \"{fixture.FilePath("run-bridge.ps1")}\"", cancellation.Token);
        var parent = await fixture.CaptureAsync("parent.pid");
        var child = await fixture.CaptureAsync("child.pid");
        cancellation.Cancel();
        try
        {
            await running;
            throw new InvalidOperationException("Cancelled diagnostics returned normally.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        try { await WaitUntilAsync(() => parent.HasExited && child.HasExited, timeoutMs: 2500); }
        catch (TimeoutException ex)
        {
            throw new InvalidOperationException($"Cancellation cleanup: parent exited={parent.HasExited}, child exited={child.HasExited}.", ex);
        }
    }

    private static async Task ExpectPortConflictAsync(BridgeSessionService session)
    {
        try { await session.StartAsync(); }
        catch (InvalidOperationException ex) when (ex.Message == "Port 39000 is already in use.") { return; }
        throw new InvalidOperationException($"Missing startup port-conflict diagnosis: {session.LastFailureReason}");
    }

    private static async Task WaitUntilAsync(Func<bool> ready, int timeoutMs = 5000)
    {
        var deadline = Stopwatch.StartNew();
        while (!ready())
        {
            if (deadline.ElapsedMilliseconds >= timeoutMs) throw new TimeoutException("Owned fixture process did not reach the expected state.");
            await Task.Delay(30);
        }
    }

    private static void Require(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }

    // These are isolated PowerShell fixtures, not the user's Bridge or simulator.
    private sealed class ProcessFixture : IAsyncDisposable
    {
        private readonly List<Process> _owned = [];
        public string Root { get; }
        public ConnectionSettings Settings { get; }
        public BridgeSessionService Session { get; }

        public ProcessFixture(string root)
        {
            Root = Path.Combine(root, "process fixture with spaces", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            File.WriteAllText(FilePath("MsfsLocalBridge.csproj"), "<Project />");
            File.WriteAllText(FilePath("diagnostics-v0.ps1"), "# fixture");
            File.WriteAllText(FilePath("run-bridge.ps1"), "# fixture");
            Settings = new ConnectionSettings(FilePath("connection.json"));
            Session = new BridgeSessionService(new BridgeWorkspace(Root), new PowerShellRunner(), Settings);
        }

        public string FilePath(string name) => Path.Combine(Root, name);

        public async Task WaitForLogAsync(string expected)
        {
            try { await WaitUntilAsync(() => Session.RuntimeLog.Contains(expected)); }
            catch (TimeoutException ex)
            {
                throw new InvalidOperationException($"Missing fixture output '{expected}'. Session log:{Environment.NewLine}{Session.RuntimeLog}", ex);
            }
        }

        public void WriteFailureScript() => WriteScript("Write-Output 'address already in use'\nStart-Sleep -Milliseconds 100\nexit 17");

        public void WriteRunningScript(bool spawnChild = false)
        {
            File.WriteAllText(FilePath("child.ps1"), "Start-Sleep -Seconds 120");
            var child = spawnChild ? """
                $childArguments = '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + (Join-Path $PSScriptRoot 'child.ps1') + '"'
                $fixtureChild = Start-Process -FilePath (Join-Path $PSHOME 'powershell.exe') -ArgumentList $childArguments -WindowStyle Hidden -PassThru
                [IO.File]::WriteAllText((Join-Path $PSScriptRoot 'child.pid'), [string]$fixtureChild.Id)
                """ : "";
            WriteScript(child + "\n" + """
                Write-Output ('fixture: bind=' + $BindHost)
                $exitRequest = Join-Path $PSScriptRoot 'exit.request'
                while (-not (Test-Path -LiteralPath $exitRequest)) { Start-Sleep -Milliseconds 50 }
                exit ([int](Get-Content -LiteralPath $exitRequest))
                """);
        }

        private void WriteScript(string body) => File.WriteAllText(FilePath("run-bridge.ps1"), """
            param([switch]$SkipLanHints, [switch]$DisableWss, [string]$BindHost)
            $ErrorActionPreference = 'Stop'
            [IO.File]::AppendAllText((Join-Path $PSScriptRoot 'launches.txt'), "start`n")
            [IO.File]::WriteAllText((Join-Path $PSScriptRoot 'parent.pid'), [string]$PID)
            """ + "\n" + body);

        public async Task<Process> CaptureAsync(string name)
        {
            Process? process = null;
            try
            {
                await WaitUntilAsync(() =>
                {
                    Process? candidate = null;
                    try
                    {
                        if (!int.TryParse(File.ReadAllText(FilePath(name)), out var pid)) return false;
                        candidate = Process.GetProcessById(pid);
                        _ = candidate.Handle; // Hold this exact process, even after it exits.
                        process = candidate;
                        return true;
                    }
                    catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException)
                    {
                        candidate?.Dispose();
                        return false;
                    }
                });
            }
            catch (TimeoutException ex)
            {
                throw new TimeoutException($"Fixture {name} never became available. Session log:{Environment.NewLine}{Session.RuntimeLog}", ex);
            }
            _owned.Add(process!);
            return process!;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Session.StopAsync().WaitAsync(TimeSpan.FromSeconds(3));
            }
            finally
            {
                foreach (var process in _owned)
                {
                    try
                    {
                        if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
                    }
                    catch (InvalidOperationException) { }
                    finally { process.Dispose(); }
                }
            }
        }
    }
}
