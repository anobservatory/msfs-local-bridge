using System.Globalization;
using BridgeAssistant.WinUI.Models;

namespace BridgeAssistant.WinUI.Services;

public sealed class BridgeController : IDisposable
{
  private readonly IBridgeHostApi _api;

  public BridgeStateStore Store { get; }

  public BridgeController(IBridgeHostApi api, BridgeStateStore store)
  {
    _api = api;
    Store = store;
    _api.LogReceived += OnApiLogReceived;
  }

  public async Task<DiagnosticsReport> RunDiagnosticsAsync(CancellationToken cancellationToken = default)
  {
    Store.AppendLog("Running diagnostics-v0.ps1 -Format Json ...");

    var report = await _api.RunDiagnosticsAsync(Store.Settings, cancellationToken);
    var normalized = NormalizeAndMergeReport(report, Store.Settings);
    Store.SetDiagnostics(normalized);
    return normalized;
  }

  public async Task<ProcessResult> RunSetupCertificateAsync(CancellationToken cancellationToken = default)
  {
    if (Store.DiagnosticsRan && !Store.IsMkcertReady())
    {
      var blocked = ProcessResult.Failed("mkcert is not ready. Install mkcert and re-run diagnostics first.");
      Store.AppendLog(blocked.StandardError);
      return blocked;
    }

    Store.AppendLog("Running setup-wss-cert-v0.ps1 ...");
    var result = await _api.RunSetupCertificateAsync(Store.Settings, cancellationToken);

    if (result.Success)
    {
      Store.AppendLog("Certificate setup script completed. Re-running diagnostics ...");
      await RunDiagnosticsAsync(cancellationToken);
    }
    else
    {
      Store.AppendLog($"Certificate setup failed: {result.StandardError}");
    }

    return result;
  }

  public async Task<ProcessResult> VerifyTrustAsync(CancellationToken cancellationToken = default)
  {
    Store.AppendLog("Verifying trust via diagnostics ...");
    var result = await _api.VerifyTrustAsync(Store.Settings, cancellationToken);

    if (!result.Success)
    {
      Store.AppendLog($"Trust not verified: {result.StandardError}");
    }

    await RunDiagnosticsAsync(cancellationToken);
    return result;
  }

  public async Task<ProcessResult> StartBridgeAsync(CancellationToken cancellationToken = default)
  {
    var reason = Store.GetStartBlockingReason();
    if (!string.IsNullOrWhiteSpace(reason))
    {
      var blocked = ProcessResult.Failed(reason);
      Store.AppendLog($"Start blocked: {reason}");
      return blocked;
    }

    var command = BuildStartCommandLine(Store.Settings);
    Store.AppendLog($"Starting bridge: {command}");

    var result = await _api.StartBridgeAsync(Store.Settings, cancellationToken);
    if (result.Success)
    {
      Store.SetBridgeRunning(true, "Bridge: Running");
      Store.AppendLog("Bridge start command dispatched.");
    }
    else
    {
      Store.SetBridgeRunning(false, $"Bridge start failed: {result.StandardError}");
    }

    return result;
  }

  public async Task<ProcessResult> StopBridgeAsync(CancellationToken cancellationToken = default)
  {
    var result = await _api.StopBridgeAsync(cancellationToken);
    if (result.Success)
    {
      Store.SetBridgeRunning(false, "Bridge: Idle");
      Store.AppendLog("Bridge stopped.");
    }
    else
    {
      Store.AppendLog($"Bridge stop failed: {result.StandardError}");
    }

    return result;
  }

  public void Dispose()
  {
    _api.LogReceived -= OnApiLogReceived;
    _api.Dispose();
  }

  private void OnApiLogReceived(object? sender, string line)
  {
    Store.AppendLog(line);
  }

  private static string BuildStartCommandLine(BridgeSettings settings)
  {
    var args = settings.BuildStartScriptArguments();
    return ".\\start-msfs-sync.ps1 " + string.Join(" ", args.Select(QuoteIfNeeded));
  }

  private static string QuoteIfNeeded(string value)
  {
    if (string.IsNullOrEmpty(value) || !value.Contains(' '))
    {
      return value;
    }

    return $"\"{value.Replace("\"", "\\\"")}\"";
  }

  private static DiagnosticsReport NormalizeAndMergeReport(DiagnosticsReport report, BridgeSettings settings)
  {
    var fallback = BuildFallbackChecks(settings);
    var byId = fallback.ToDictionary(check => check.Id, StringComparer.OrdinalIgnoreCase);

    foreach (var check in report.Checks)
    {
      var normalizedId = NormalizeCheckId(check.Id, settings);

      if (byId.TryGetValue(normalizedId, out var existing))
      {
        byId[normalizedId] = new DiagnosticCheck
        {
          Id = normalizedId,
          Label = string.IsNullOrWhiteSpace(check.Label) ? existing.Label : check.Label,
          Status = check.Status,
          RepairAction = string.IsNullOrWhiteSpace(check.RepairAction) ? existing.RepairAction : check.RepairAction,
        };
      }
      else
      {
        byId[normalizedId] = new DiagnosticCheck
        {
          Id = normalizedId,
          Label = string.IsNullOrWhiteSpace(check.Label) ? normalizedId : check.Label,
          Status = check.Status,
          RepairAction = check.RepairAction,
        };
      }
    }

    var ordered = new List<DiagnosticCheck>();
    foreach (var fallbackCheck in fallback)
    {
      if (byId.TryGetValue(fallbackCheck.Id, out var check))
      {
        ordered.Add(check);
      }
    }

    foreach (var extra in byId.Values.Where(check => fallback.All(defaultCheck =>
                 !string.Equals(defaultCheck.Id, check.Id, StringComparison.OrdinalIgnoreCase)))
               .OrderBy(check => check.Id, StringComparer.OrdinalIgnoreCase))
    {
      ordered.Add(extra);
    }

    return new DiagnosticsReport
    {
      Checks = ordered,
      ExitCode = report.ExitCode,
      RawOutput = report.RawOutput,
      ErrorOutput = report.ErrorOutput,
    };
  }

  private static string NormalizeCheckId(string rawId, BridgeSettings settings)
  {
    var ws = settings.WsPort.ToString(CultureInfo.InvariantCulture);
    var wss = settings.WssPort.ToString(CultureInfo.InvariantCulture);

    if (string.Equals(rawId, $"network.firewall_private_{ws}", StringComparison.OrdinalIgnoreCase))
    {
      return "network.firewall_private_ws";
    }

    if (string.Equals(rawId, $"network.firewall_private_{wss}", StringComparison.OrdinalIgnoreCase))
    {
      return "network.firewall_private_wss";
    }

    if (string.Equals(rawId, $"network.port_{ws}", StringComparison.OrdinalIgnoreCase))
    {
      return "network.port_ws";
    }

    if (string.Equals(rawId, $"network.port_{wss}", StringComparison.OrdinalIgnoreCase))
    {
      return "network.port_wss";
    }

    return rawId;
  }

  private static List<DiagnosticCheck> BuildFallbackChecks(BridgeSettings settings)
  {
    return new List<DiagnosticCheck>
    {
      new()
      {
        Id = "runtime.dotnet",
        Label = ".NET 8 runtime availability",
        Status = CheckStatus.Pass,
        RepairAction = "Install .NET 8 runtime (winget install Microsoft.DotNet.Runtime.8)",
      },
      new()
      {
        Id = "dependency.vc_redist_x64",
        Label = "Visual C++ Redistributable (x64)",
        Status = CheckStatus.Pass,
        RepairAction = "Install Microsoft Visual C++ 2015-2022 Redistributable (x64).",
      },
      new()
      {
        Id = "runtime.standard_user",
        Label = "PowerShell standard-user mode",
        Status = CheckStatus.Warn,
        RepairAction = "Use normal PowerShell for bridge run. Use Administrator shell only for repair script.",
      },
      new()
      {
        Id = "simconnect.managed_dll",
        Label = "Managed SimConnect DLL in lib/",
        Status = CheckStatus.Pass,
        RepairAction = "Copy Microsoft.FlightSimulator.SimConnect.dll into lib/.",
      },
      new()
      {
        Id = "simconnect.native_dll",
        Label = "Native SimConnect DLL in lib/",
        Status = CheckStatus.Pass,
        RepairAction = "Copy SimConnect.dll into lib/.",
      },
      new()
      {
        Id = "network.firewall_private_ws",
        Label = $"Firewall rule for inbound TCP {settings.WsPort}",
        Status = CheckStatus.Warn,
        RepairAction = $"Run as Administrator: .\\repair-elevated-v0.ps1 -Action OpenFirewall39000 -Port {settings.WsPort}",
      },
      new()
      {
        Id = "network.firewall_private_wss",
        Label = $"Firewall rule for inbound TCP {settings.WssPort}",
        Status = CheckStatus.Warn,
        RepairAction = $"Run as Administrator: .\\repair-elevated-v0.ps1 -Action OpenFirewall39002 -Port {settings.WssPort}",
      },
      new()
      {
        Id = "network.mkcert",
        Label = "mkcert executable on PATH",
        Status = CheckStatus.Warn,
        RepairAction = "Install mkcert and rerun certificate setup script.",
      },
      new()
      {
        Id = "network.wss_cert",
        Label = "WSS certificate file",
        Status = CheckStatus.Warn,
        RepairAction = $"Run: .\\setup-wss-cert-v0.ps1 -LocalDomain {settings.Domain} -CertDir \"{settings.CertDirectory}\"",
      },
      new()
      {
        Id = "network.wss_key",
        Label = "WSS private key file",
        Status = CheckStatus.Warn,
        RepairAction = $"Run: .\\setup-wss-cert-v0.ps1 -LocalDomain {settings.Domain} -CertDir \"{settings.CertDirectory}\"",
      },
      new()
      {
        Id = "network.root_ca",
        Label = "Root CA export file",
        Status = CheckStatus.Warn,
        RepairAction = $"Run: .\\setup-wss-cert-v0.ps1 -LocalDomain {settings.Domain} -CertDir \"{settings.CertDirectory}\"",
      },
      new()
      {
        Id = "network.port_ws",
        Label = $"TCP {settings.WsPort} availability",
        Status = CheckStatus.Pass,
        RepairAction = $"Stop conflicting process or choose another port than {settings.WsPort}.",
      },
      new()
      {
        Id = "network.port_wss",
        Label = $"TCP {settings.WssPort} availability",
        Status = CheckStatus.Pass,
        RepairAction = $"Stop conflicting process or choose another port than {settings.WssPort}.",
      },
    };
  }
}
