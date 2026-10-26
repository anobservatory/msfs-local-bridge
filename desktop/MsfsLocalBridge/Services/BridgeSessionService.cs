using System.Diagnostics;
using System.IO;
using System.Text;

namespace MsfsLocalBridge.Services;

internal sealed class BridgeSessionService
{
    private readonly BridgeWorkspace _workspace;
    private readonly PowerShellRunner _runner;
    private readonly StringBuilder _runtimeLog = new();
    private readonly object _sync = new();
    private Process? _process;
    private string? _lastFailureReason;
    private int? _lastExitCode;
    private int? _expectedExitProcessId;

    public BridgeSessionService(BridgeWorkspace workspace, PowerShellRunner runner)
    {
        _workspace = workspace;
        _runner = runner;
    }

    public bool IsRunning => _process is { HasExited: false };

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
        if (IsRunning)
        {
            return;
        }

        ResetFailureState();

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
            AppendLog,
            line => AppendLog($"ERROR: {line}"));
        process.Exited += (_, _) => OnProcessExited(process);
        _process = process;

        await Task.Delay(1500);
        if (process.HasExited)
        {
            _process = null;
            throw new InvalidOperationException(LastFailureReason ?? $"Bridge exited with code {process.ExitCode}.");
        }
    }

    public Task StopAsync()
    {
        if (!IsRunning || _process is null)
        {
            return Task.CompletedTask;
        }

        lock (_sync)
        {
            _expectedExitProcessId = _process.Id;
        }

        AppendLog("bridge: stopping process");
        _process.Kill(entireProcessTree: true);
        _process.Dispose();
        _process = null;
        return Task.CompletedTask;
    }

    public async Task RestartAsync()
    {
        await StopAsync();
        await StartAsync();
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

    private void OnProcessExited(Process process)
    {
        bool wasIntentionalStop;

        lock (_sync)
        {
            _lastExitCode = process.ExitCode;
            wasIntentionalStop = _expectedExitProcessId == process.Id;

            if (wasIntentionalStop)
            {
                _expectedExitProcessId = null;
            }
            else
            {
                _lastFailureReason ??= BuildFailureReason(_runtimeLog.ToString(), process.ExitCode);
            }
        }

        if (wasIntentionalStop)
        {
            AppendLog("bridge: stopped");
            return;
        }

        AppendLog($"bridge: exited with code {process.ExitCode}");
    }

    private void ResetFailureState()
    {
        lock (_sync)
        {
            _lastFailureReason = null;
            _lastExitCode = null;
        }
    }

    private void AppendLog(string line)
    {
        lock (_sync)
        {
            if (_runtimeLog.Length > 0)
            {
                _runtimeLog.AppendLine();
            }

            _runtimeLog.Append($"[{DateTime.Now:HH:mm:ss}] {line}");
            _lastFailureReason = DetectFailureReason(line) ?? _lastFailureReason;
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

    private static string BuildFailureReason(string runtimeLog, int exitCode)
    {
        if (runtimeLog.Contains("address already in use", StringComparison.OrdinalIgnoreCase))
        {
            return "Port 39000 is already in use.";
        }

        if (runtimeLog.Contains("Failed to bind to address", StringComparison.OrdinalIgnoreCase))
        {
            return "Bridge could not bind to the required port.";
        }

        if (runtimeLog.Contains("Worker executable not found", StringComparison.OrdinalIgnoreCase))
        {
            return "SimConnect worker executable is missing.";
        }

        return $"Bridge exited with code {exitCode}.";
    }

    private sealed record BridgeLaunchProfile(string Arguments, string ModeLabel, string? Note);
}

