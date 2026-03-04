using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using BridgeAssistant.WinUI.Models;

namespace BridgeAssistant.WinUI.Services;

public sealed class LocalBridgeHostApi : IBridgeHostApi
{
  private readonly PowerShellScriptRunner _runner;
  private readonly string _repositoryRoot;
  private Process? _bridgeProcess;

  public event EventHandler<string>? LogReceived;

  public bool IsBridgeRunning => _bridgeProcess is { HasExited: false };

  public LocalBridgeHostApi(string? repositoryRoot = null)
  {
    _runner = new PowerShellScriptRunner();
    _repositoryRoot = repositoryRoot ?? ResolveRepositoryRoot();
    EmitLog($"Repository root detected: {_repositoryRoot}");
  }

  public async Task<DiagnosticsReport> RunDiagnosticsAsync(
    BridgeSettings settings,
    CancellationToken cancellationToken = default)
  {
    var scriptPath = GetScriptPath("diagnostics-v0.ps1");
    var args = new List<string>
    {
      "-Port", settings.WsPort.ToString(CultureInfo.InvariantCulture),
      "-WssPort", settings.WssPort.ToString(CultureInfo.InvariantCulture),
      "-LocalDomain", settings.Domain,
      "-CertDir", settings.CertDirectory,
      "-Format", "Json",
    };

    var result = await _runner.RunScriptAsync(scriptPath, args, TimeSpan.FromSeconds(45), cancellationToken);
    EmitLog($"diagnostics-v0.ps1 finished with exit code {result.ExitCode}.");
    return ParseDiagnostics(result);
  }

  public async Task<ProcessResult> RunSetupCertificateAsync(
    BridgeSettings settings,
    CancellationToken cancellationToken = default)
  {
    var scriptPath = GetScriptPath("setup-wss-cert-v0.ps1");
    var args = new List<string>
    {
      "-LocalDomain", settings.Domain,
      "-CertDir", settings.CertDirectory,
    };

    var result = await _runner.RunScriptAsync(scriptPath, args, TimeSpan.FromSeconds(90), cancellationToken);
    EmitLog($"setup-wss-cert-v0.ps1 finished with exit code {result.ExitCode}.");
    return result;
  }

  public async Task<ProcessResult> VerifyTrustAsync(
    BridgeSettings settings,
    CancellationToken cancellationToken = default)
  {
    var report = await RunDiagnosticsAsync(settings, cancellationToken);
    var rootCaCheck = report.Checks.FirstOrDefault(check => check.Id == "network.root_ca");

    if (rootCaCheck is not null && rootCaCheck.Status == CheckStatus.Pass)
    {
      return ProcessResult.Ok("Root CA trust is verified by diagnostics.");
    }

    var detail = rootCaCheck?.RepairAction ?? "Run diagnostics and inspect trust related checks.";
    return ProcessResult.Failed(detail, report.ExitCode == 0 ? -1 : report.ExitCode);
  }

  public Task<ProcessResult> StartBridgeAsync(BridgeSettings settings, CancellationToken cancellationToken = default)
  {
    if (IsBridgeRunning)
    {
      return Task.FromResult(ProcessResult.Failed("Bridge is already running."));
    }

    var scriptPath = GetScriptPath("start-msfs-sync.ps1");
    var args = settings.BuildStartScriptArguments();

    try
    {
      _bridgeProcess = _runner.StartScriptProcess(
        scriptPath,
        args,
        (_, e) =>
        {
          if (!string.IsNullOrWhiteSpace(e.Data))
          {
            EmitLog(e.Data);
          }
        },
        (_, e) =>
        {
          if (!string.IsNullOrWhiteSpace(e.Data))
          {
            EmitLog($"ERR: {e.Data}");
          }
        });

      EmitLog("start-msfs-sync.ps1 launched.");
      return Task.FromResult(ProcessResult.Ok("Bridge process started."));
    }
    catch (Exception ex)
    {
      EmitLog($"Bridge start failed: {ex.Message}");
      return Task.FromResult(ProcessResult.Failed(ex.Message));
    }
  }

  public async Task<ProcessResult> StopBridgeAsync(CancellationToken cancellationToken = default)
  {
    if (!IsBridgeRunning)
    {
      return ProcessResult.Ok("Bridge is already stopped.");
    }

    try
    {
      _bridgeProcess?.Kill(entireProcessTree: true);
      if (_bridgeProcess is not null)
      {
        await _bridgeProcess.WaitForExitAsync(cancellationToken);
        _bridgeProcess.Dispose();
      }

      _bridgeProcess = null;
      EmitLog("Bridge process stopped.");
      return ProcessResult.Ok("Bridge process stopped.");
    }
    catch (Exception ex)
    {
      EmitLog($"Bridge stop failed: {ex.Message}");
      return ProcessResult.Failed(ex.Message);
    }
  }

  public void Dispose()
  {
    if (_bridgeProcess is { HasExited: false })
    {
      try
      {
        _bridgeProcess.Kill(entireProcessTree: true);
      }
      catch
      {
      }
    }

    _bridgeProcess?.Dispose();
    _bridgeProcess = null;
  }

  private DiagnosticsReport ParseDiagnostics(ProcessResult result)
  {
    if (string.IsNullOrWhiteSpace(result.StandardOutput))
    {
      return new DiagnosticsReport
      {
        Checks = Array.Empty<DiagnosticCheck>(),
        ExitCode = result.ExitCode,
        RawOutput = result.StandardOutput,
        ErrorOutput = result.StandardError,
      };
    }

    try
    {
      using var document = JsonDocument.Parse(result.StandardOutput);
      if (!document.RootElement.TryGetProperty("checks", out var checksElement) ||
          checksElement.ValueKind != JsonValueKind.Array)
      {
        return new DiagnosticsReport
        {
          Checks = Array.Empty<DiagnosticCheck>(),
          ExitCode = result.ExitCode,
          RawOutput = result.StandardOutput,
          ErrorOutput = result.StandardError,
        };
      }

      var checks = new List<DiagnosticCheck>();
      foreach (var item in checksElement.EnumerateArray())
      {
        var id = item.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? string.Empty : string.Empty;
        var label = item.TryGetProperty("label", out var labelEl) ? labelEl.GetString() ?? id : id;
        var statusRaw = item.TryGetProperty("status", out var statusEl)
          ? statusEl.GetString() ?? "warn"
          : "warn";
        var repair = item.TryGetProperty("repairAction", out var repairEl)
          ? repairEl.GetString() ?? string.Empty
          : string.Empty;

        checks.Add(new DiagnosticCheck
        {
          Id = id,
          Label = label,
          Status = ParseStatus(statusRaw),
          RepairAction = repair,
        });
      }

      return new DiagnosticsReport
      {
        Checks = checks,
        ExitCode = result.ExitCode,
        RawOutput = result.StandardOutput,
        ErrorOutput = result.StandardError,
      };
    }
    catch (Exception ex)
    {
      EmitLog($"Diagnostics JSON parse failed: {ex.Message}");
      return new DiagnosticsReport
      {
        Checks = Array.Empty<DiagnosticCheck>(),
        ExitCode = result.ExitCode,
        RawOutput = result.StandardOutput,
        ErrorOutput = string.IsNullOrWhiteSpace(result.StandardError)
          ? ex.Message
          : $"{result.StandardError}{Environment.NewLine}{ex.Message}",
      };
    }
  }

  private static CheckStatus ParseStatus(string raw)
  {
    return raw.ToLowerInvariant() switch
    {
      "pass" => CheckStatus.Pass,
      "fail" => CheckStatus.Fail,
      _ => CheckStatus.Warn,
    };
  }

  private void EmitLog(string message)
  {
    LogReceived?.Invoke(this, $"[{DateTime.Now:HH:mm:ss}] {message}");
  }

  private string GetScriptPath(string scriptName)
  {
    var path = Path.Combine(_repositoryRoot, scriptName);
    if (!File.Exists(path))
    {
      throw new FileNotFoundException($"Script not found: {path}");
    }

    return path;
  }

  private static string ResolveRepositoryRoot()
  {
    var candidates = new[]
    {
      Directory.GetCurrentDirectory(),
      AppContext.BaseDirectory,
    };

    foreach (var candidate in candidates)
    {
      var found = WalkUpForRepositoryRoot(candidate);
      if (found is not null)
      {
        return found;
      }
    }

    return Directory.GetCurrentDirectory();
  }

  private static string? WalkUpForRepositoryRoot(string startPath)
  {
    DirectoryInfo? directory;

    try
    {
      directory = new DirectoryInfo(startPath);
    }
    catch
    {
      return null;
    }

    while (directory is not null)
    {
      var diagnostics = Path.Combine(directory.FullName, "diagnostics-v0.ps1");
      var start = Path.Combine(directory.FullName, "start-msfs-sync.ps1");
      if (File.Exists(diagnostics) && File.Exists(start))
      {
        return directory.FullName;
      }

      directory = directory.Parent;
    }

    return null;
  }
}
