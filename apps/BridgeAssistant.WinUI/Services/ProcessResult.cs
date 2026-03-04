namespace BridgeAssistant.WinUI.Services;

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut)
{
  public bool Success => ExitCode == 0 && !TimedOut;

  public static ProcessResult Ok(string standardOutput = "", string standardError = "")
  {
    return new ProcessResult(0, standardOutput, standardError, false);
  }

  public static ProcessResult Failed(string errorMessage, int exitCode = -1)
  {
    return new ProcessResult(exitCode, string.Empty, errorMessage, false);
  }
}
