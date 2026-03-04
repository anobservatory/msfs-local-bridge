using BridgeAssistant.WinUI.Models;

namespace BridgeAssistant.WinUI.Services;

public sealed class BridgeStateStore
{
  private readonly List<string> _activityLog = new();

  public event EventHandler? Changed;

  public BridgeSettings Settings { get; }

  public DiagnosticsReport LastDiagnosticsReport { get; private set; } = new DiagnosticsReport
  {
    Checks = Array.Empty<DiagnosticCheck>(),
    ExitCode = 0,
    RawOutput = string.Empty,
    ErrorOutput = string.Empty,
  };

  public bool DiagnosticsRan { get; private set; }

  public bool BridgeRunning { get; private set; }

  public DateTimeOffset? BridgeStartedAt { get; private set; }

  public int RuntimeConnections { get; private set; }

  public string LastStatusMessage { get; private set; } = "Bridge Assistant ready. Run diagnostics first.";

  public IReadOnlyList<string> ActivityLog => _activityLog;

  public BridgeStateStore(BridgeSettings settings)
  {
    Settings = settings;
    AppendLog(LastStatusMessage, notify: false);
  }

  public void SetDiagnostics(DiagnosticsReport report)
  {
    LastDiagnosticsReport = report;
    DiagnosticsRan = true;

    LastStatusMessage = $"Diagnostics complete. ExitCode={report.ExitCode}, Issues={TotalIssues()}";
    AppendLog(LastStatusMessage, notify: false);
    NotifyChanged();
  }

  public void SetBridgeRunning(bool running, string statusMessage)
  {
    BridgeRunning = running;
    RuntimeConnections = running ? Math.Max(RuntimeConnections, 1) : 0;
    BridgeStartedAt = running ? DateTimeOffset.Now : null;
    LastStatusMessage = statusMessage;

    AppendLog(statusMessage, notify: false);
    NotifyChanged();
  }

  public void SetRuntimeConnections(int count)
  {
    RuntimeConnections = Math.Max(0, count);
    NotifyChanged();
  }

  public void AppendLog(string line)
  {
    AppendLog(line, notify: true);
  }

  public void ClearActivityLog()
  {
    _activityLog.Clear();
    NotifyChanged();
  }

  public int Count(CheckStatus status)
  {
    return LastDiagnosticsReport.Checks.Count(check => check.Status == status);
  }

  public int TotalIssues()
  {
    return Count(CheckStatus.Warn) + Count(CheckStatus.Fail);
  }

  public DiagnosticCheck? FindCheck(string checkId)
  {
    return LastDiagnosticsReport.Checks.FirstOrDefault(check =>
      string.Equals(check.Id, checkId, StringComparison.OrdinalIgnoreCase));
  }

  public bool IsMkcertReady()
  {
    return FindCheck("network.mkcert")?.Status == CheckStatus.Pass;
  }

  public bool IsWssCertificateReady()
  {
    return FindCheck("network.wss_cert")?.Status == CheckStatus.Pass &&
           FindCheck("network.wss_key")?.Status == CheckStatus.Pass;
  }

  public bool IsRootCaTrusted()
  {
    return FindCheck("network.root_ca")?.Status == CheckStatus.Pass;
  }

  public bool IsFirewallReady()
  {
    return FindCheck("network.firewall_private_ws")?.Status == CheckStatus.Pass &&
           FindCheck("network.firewall_private_wss")?.Status == CheckStatus.Pass;
  }

  public string BuildWsEndpoint()
  {
    return $"ws://{Settings.BindHost}:{Settings.WsPort}/stream";
  }

  public string BuildWssEndpoint()
  {
    if (Settings.WssMode == WssMode.Disabled)
    {
      return "disabled by -DisableWss";
    }

    if (IsWssCertificateReady())
    {
      return $"wss://{Settings.Domain}:{Settings.WssPort}/stream";
    }

    if (Settings.WssMode == WssMode.Required)
    {
      return "required but not ready (missing cert)";
    }

    return $"wss://{Settings.Domain}:{Settings.WssPort}/stream (fallback to WS)";
  }

  public string BuildBootstrapEndpoint()
  {
    return $"http://<windows-ip>:{Settings.WsPort}/bootstrap";
  }

  public string GetWssModeLabel()
  {
    return Settings.WssMode switch
    {
      WssMode.Disabled => "Disabled (WS only)",
      WssMode.Required => "Required",
      _ => "Auto (fallback allowed)",
    };
  }

  public string GetStartBlockingReason()
  {
    if (BridgeRunning)
    {
      return "Bridge is already running.";
    }

    if (Settings.WssMode == WssMode.Required && !IsWssCertificateReady())
    {
      return "WSS mode is Required. Complete certificate setup and re-run diagnostics first.";
    }

    return string.Empty;
  }

  private void AppendLog(string line, bool notify)
  {
    if (string.IsNullOrWhiteSpace(line))
    {
      return;
    }

    _activityLog.Add($"[{DateTime.Now:HH:mm:ss}] {line}");
    if (_activityLog.Count > 600)
    {
      _activityLog.RemoveRange(0, _activityLog.Count - 600);
    }

    if (notify)
    {
      NotifyChanged();
    }
  }

  private void NotifyChanged()
  {
    Changed?.Invoke(this, EventArgs.Empty);
  }
}
