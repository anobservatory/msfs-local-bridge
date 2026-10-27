using System.Diagnostics;
using System.IO;
using System.Text;

namespace MsfsLocalBridge.Services;

internal sealed class BridgeSessionService
{
    private readonly BridgeWorkspace _workspace;
    private readonly PowerShellRunner _runner;
    private readonly ConnectionSettings _connectionSettings;
    private readonly StringBuilder _runtimeLog = new();
    private readonly object _sync = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private Process? _process;
    private string? _lastFailureReason;
    private int? _lastExitCode;
    private int? _expectedExitProcessId;
    private int? _observedExitProcessId;
    private int _runId;

    public BridgeSessionService(BridgeWorkspace workspace, PowerShellRunner runner, ConnectionSettings connectionSettings)
    {
        _workspace = workspace;
        _runner = runner;
        _connectionSettings = connectionSettings;
    }

    public bool IsRunning
    {
        get
        {
            lock (_sync) { return _process is { HasExited: false }; }
        }
    }

    public string? LastFailureReason
    {
        get
        {
            lock (_sync)
            {
                return _lastFailureReason;
            }
        }
    }

    public int? LastExitCode
    {
        get
        {
            lock (_sync)
            {
                return _lastExitCode;
            }
        }
    }

    public string RuntimeLog
    {
        get
        {
            lock (_sync)
            {
                return _runtimeLog.ToString();
            }
        }
    }

    public async Task StartAsync()
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try { await StartCoreAsync().ConfigureAwait(false); }
        finally { _lifecycleGate.Release(); }
    }

    public async Task StopAsync()
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try { await StopCoreAsync().ConfigureAwait(false); }
        finally { _lifecycleGate.Release(); }
    }

    public async Task RestartAsync()
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
            await StartCoreAsync().ConfigureAwait(false);
        }
        finally { _lifecycleGate.Release(); }
    }

    private async Task StartCoreAsync()
    {
        if (IsRunning)
        {
            return;
        }

        await StopCoreAsync().ConfigureAwait(false);
        var runId = ResetFailureState();

        var launchProfile = CreateLaunchProfile();
        AppendLog($"bridge: starting run-bridge.ps1 ({launchProfile.ModeLabel})");
        AppendLog($"bridge: payload root = {_workspace.BridgeRepoRoot}");
        AppendLog($"bridge: payload source = {(_workspace.UsesBundledBridge ? "bundled" : "source repo")}");

        if (!string.IsNullOrWhiteSpace(launchProfile.Note))
        {
            AppendLog(launchProfile.Note);
        }

        var process = _runner.StartStreaming(
            _workspace.BridgeRepoRoot,
            launchProfile.Arguments,
            line => AppendLog(line, runId),
            line => AppendLog($"ERROR: {line}", runId));
        process.Exited += (_, _) => OnProcessExited(process, runId);
        lock (_sync) { _process = process; }

        // Window shutdown synchronously waits for StopAsync. Do not require its
        // UI synchronization context to finish a pending Start or Restart.
        await Task.Delay(1500).ConfigureAwait(false);
        if (process.HasExited)
        {
            process.WaitForExit(); // Drain redirected output before diagnosing startup.
            OnProcessExited(process, runId);
            var reason = LastFailureReason ?? $"Bridge exited with code {process.ExitCode}.";
            await StopCoreAsync().ConfigureAwait(false);
            throw new InvalidOperationException(reason);
        }
    }

    private async Task StopCoreAsync()
    {
        Process? process;
        bool shouldStop;
        lock (_sync)
        {
            process = _process;
            if (process is null) return;
            shouldStop = !process.HasExited;
            if (shouldStop) _expectedExitProcessId = process.Id;
        }

        try
        {
            if (shouldStop)
            {
                AppendLog("bridge: stopping process");
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
            }
            await process.WaitForExitAsync().ConfigureAwait(false);
            process.WaitForExit();
            OnProcessExited(process, _runId);
        }
        finally
        {
            bool hasExited;
            lock (_sync)
            {
                // Retain a live process if termination failed so Stop can retry.
                hasExited = process.HasExited;
                if (hasExited) _process = null;
            }
            if (hasExited) process.Dispose();
        }
    }

    public void ClearLog()
    {
        lock (_sync)
        {
            _runtimeLog.Clear();
        }
    }

    private BridgeLaunchProfile CreateLaunchProfile()
    {
        var arguments = new StringBuilder();
        arguments.Append("-ExecutionPolicy Bypass -File \"");
        arguments.Append(_workspace.BridgeScriptPath);
        arguments.Append("\" -SkipLanHints -DisableWss");
        arguments.Append(_connectionSettings.AllowLanAccess ? " -BindHost 0.0.0.0" : " -BindHost 127.0.0.1");

        var supportsWorkerMode = ScriptSupportsWorkerMode(_workspace.BridgeScriptPath);
        var workerExecutablePath = ResolveWorkerExecutablePath();

        if (supportsWorkerMode && workerExecutablePath is not null)
        {
            arguments.Append(" -SimConnectMode worker -SimConnectWorkerPath \"");
            arguments.Append(workerExecutablePath);
            arguments.Append("\"");

            return new BridgeLaunchProfile(
                arguments.ToString(),
                "mode=worker",
                $"bridge: simconnect worker path = {workerExecutablePath}");
        }

        if (supportsWorkerMode)
        {
            return new BridgeLaunchProfile(
                arguments.ToString(),
                "mode=embedded",
                "bridge: native SimConnect worker not found; continuing in embedded mode");
        }

        return new BridgeLaunchProfile(
            arguments.ToString(),
            "mode=embedded",
            "bridge: bridge script does not support worker mode; continuing in embedded mode");
    }

    private string? ResolveWorkerExecutablePath()
    {
        var candidate = Path.Combine(
            _workspace.BridgeRepoRoot,
            "workers",
            "simconnect-native",
            "dist",
            "msfs-simconnect-worker.exe");

        return File.Exists(candidate) ? candidate : null;
    }

    private static bool ScriptSupportsWorkerMode(string scriptPath)
    {
        if (!File.Exists(scriptPath))
        {
            return false;
        }

        try
        {
            var script = File.ReadAllText(scriptPath);
            return script.Contains("SimConnectMode", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private void OnProcessExited(Process process, int runId)
    {
        int exitCode;
        int processId;
        try
        {
            exitCode = process.ExitCode;
            processId = process.Id;
        }
        catch (InvalidOperationException)
        {
            return;
        }
        bool wasIntentionalStop;

        lock (_sync)
        {
            if (runId != _runId || _observedExitProcessId == processId) return;
            _observedExitProcessId = processId;
            _lastExitCode = exitCode;
            wasIntentionalStop = _expectedExitProcessId == processId;

            if (wasIntentionalStop)
            {
                _expectedExitProcessId = null;
            }
            else
            {
                _lastFailureReason ??= $"Bridge exited with code {exitCode}.";
            }
        }

        if (wasIntentionalStop)
        {
            AppendLog("bridge: stopped");
            return;
        }

        AppendLog($"bridge: exited with code {exitCode}");
    }

    private int ResetFailureState()
    {
        lock (_sync)
        {
            _lastFailureReason = null;
            _lastExitCode = null;
            _expectedExitProcessId = null;
            _observedExitProcessId = null;
            return ++_runId;
        }
    }

    private void AppendLog(string line, int? runId = null)
    {
        lock (_sync)
        {
            if (_runtimeLog.Length > 0)
            {
                _runtimeLog.AppendLine();
            }

            _runtimeLog.Append($"[{DateTime.Now:HH:mm:ss}] {line}");
            if (runId == _runId) _lastFailureReason = DetectFailureReason(line) ?? _lastFailureReason;
        }
    }

    private static string? DetectFailureReason(string line)
    {
        if (line.Contains("address already in use", StringComparison.OrdinalIgnoreCase))
        {
            return "Port 39000 is already in use.";
        }

        if (line.Contains("Failed to bind to address", StringComparison.OrdinalIgnoreCase))
        {
            return "Bridge could not bind to the required port.";
        }

        if (line.Contains("Worker executable not found", StringComparison.OrdinalIgnoreCase))
        {
            return "SimConnect worker executable is missing.";
        }

        if (line.Contains("WSS is required but certificate files are missing", StringComparison.OrdinalIgnoreCase))
        {
            return "Secure mode certificate files are missing.";
        }

        if (line.Contains("Unhandled exception", StringComparison.OrdinalIgnoreCase))
        {
            return "Bridge crashed during startup.";
        }

        return null;
    }

    private sealed record BridgeLaunchProfile(string Arguments, string ModeLabel, string? Note);
}

