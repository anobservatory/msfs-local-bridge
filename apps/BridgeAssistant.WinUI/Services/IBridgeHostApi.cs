using BridgeAssistant.WinUI.Models;

namespace BridgeAssistant.WinUI.Services;

public interface IBridgeHostApi : IDisposable
{
  event EventHandler<string>? LogReceived;

  bool IsBridgeRunning { get; }

  Task<DiagnosticsReport> RunDiagnosticsAsync(BridgeSettings settings, CancellationToken cancellationToken = default);

  Task<ProcessResult> RunSetupCertificateAsync(BridgeSettings settings, CancellationToken cancellationToken = default);

  Task<ProcessResult> VerifyTrustAsync(BridgeSettings settings, CancellationToken cancellationToken = default);

  Task<ProcessResult> StartBridgeAsync(BridgeSettings settings, CancellationToken cancellationToken = default);

  Task<ProcessResult> StopBridgeAsync(CancellationToken cancellationToken = default);
}
