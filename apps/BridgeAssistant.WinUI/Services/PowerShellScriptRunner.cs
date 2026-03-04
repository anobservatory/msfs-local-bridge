using System.Diagnostics;
using System.Text;

namespace BridgeAssistant.WinUI.Services;

public sealed class PowerShellScriptRunner
{
  private const string PowerShellExecutable = "powershell.exe";

  public async Task<ProcessResult> RunScriptAsync(
    string scriptPath,
    IReadOnlyList<string> scriptArguments,
    TimeSpan timeout,
    CancellationToken cancellationToken = default)
  {
    using var process = CreateProcess(scriptPath, scriptArguments);

    var stdout = new StringBuilder();
    var stderr = new StringBuilder();
    var stdoutClosed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var stderrClosed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

    process.OutputDataReceived += (_, args) =>
    {
      if (args.Data is null)
      {
        stdoutClosed.TrySetResult(true);
      }
      else
      {
        stdout.AppendLine(args.Data);
      }
    };

    process.ErrorDataReceived += (_, args) =>
    {
      if (args.Data is null)
      {
        stderrClosed.TrySetResult(true);
      }
      else
      {
        stderr.AppendLine(args.Data);
      }
    };

    process.Start();
    process.BeginOutputReadLine();
    process.BeginErrorReadLine();

    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    timeoutCts.CancelAfter(timeout);

    try
    {
      await process.WaitForExitAsync(timeoutCts.Token);
      await Task.WhenAll(stdoutClosed.Task, stderrClosed.Task);
      return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString(), false);
    }
    catch (OperationCanceledException)
    {
      try
      {
        if (!process.HasExited)
        {
          process.Kill(entireProcessTree: true);
        }
      }
      catch
      {
      }

      await Task.WhenAll(stdoutClosed.Task, stderrClosed.Task);
      var timedOut = !cancellationToken.IsCancellationRequested;
      return new ProcessResult(-1, stdout.ToString(), stderr.ToString(), timedOut);
    }
  }

  public Process StartScriptProcess(
    string scriptPath,
    IReadOnlyList<string> scriptArguments,
    DataReceivedEventHandler? outputHandler = null,
    DataReceivedEventHandler? errorHandler = null)
  {
    var process = CreateProcess(scriptPath, scriptArguments);

    if (outputHandler is not null)
    {
      process.OutputDataReceived += outputHandler;
    }

    if (errorHandler is not null)
    {
      process.ErrorDataReceived += errorHandler;
    }

    process.Start();
    process.BeginOutputReadLine();
    process.BeginErrorReadLine();
    return process;
  }

  private static Process CreateProcess(string scriptPath, IReadOnlyList<string> scriptArguments)
  {
    var startInfo = new ProcessStartInfo
    {
      FileName = PowerShellExecutable,
      UseShellExecute = false,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      CreateNoWindow = true,
      WorkingDirectory = Path.GetDirectoryName(scriptPath) ?? Directory.GetCurrentDirectory(),
    };

    startInfo.ArgumentList.Add("-NoProfile");
    startInfo.ArgumentList.Add("-ExecutionPolicy");
    startInfo.ArgumentList.Add("Bypass");
    startInfo.ArgumentList.Add("-File");
    startInfo.ArgumentList.Add(scriptPath);

    foreach (var arg in scriptArguments)
    {
      startInfo.ArgumentList.Add(arg);
    }

    return new Process
    {
      StartInfo = startInfo,
      EnableRaisingEvents = true,
    };
  }
}
