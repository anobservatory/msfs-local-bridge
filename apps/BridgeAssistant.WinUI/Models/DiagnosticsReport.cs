namespace BridgeAssistant.WinUI.Models;

public enum CheckStatus
{
  Pass,
  Warn,
  Fail,
}

public sealed class DiagnosticCheck
{
  public string Id { get; init; } = string.Empty;

  public string Label { get; init; } = string.Empty;

  public CheckStatus Status { get; init; } = CheckStatus.Warn;

  public string RepairAction { get; init; } = string.Empty;

  public string StatusText => Status.ToString().ToLowerInvariant();
}

public sealed class DiagnosticsReport
{
  public IReadOnlyList<DiagnosticCheck> Checks { get; init; } = Array.Empty<DiagnosticCheck>();

  public int ExitCode { get; init; }

  public string RawOutput { get; init; } = string.Empty;

  public string ErrorOutput { get; init; } = string.Empty;
}
