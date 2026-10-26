using System.Diagnostics;
using System.IO;
using System.Text;

namespace MsfsLocalBridge.Services;

internal sealed class PowerShellRunner
{
    private readonly string _powerShellPath;

    public PowerShellRunner()
    {
        _powerShellPath = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
    }

    public async Task<PowerShellResult> RunAsync(string workingDirectory, string arguments, CancellationToken cancellationToken = default)
    {
        var startInfo = CreateStartInfo(workingDirectory, arguments, redirectIo: true, useShellExecute: false, createNoWindow: true);

        using var process = new Process { StartInfo = startInfo };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        stdout.Append(await outputTask);
        stderr.Append(await errorTask);

        return new PowerShellResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    public Process StartStreaming(
        string workingDirectory,
        string arguments,
        Action<string> onOutput,
        Action<string> onError)
    {
        var startInfo = CreateStartInfo(workingDirectory, arguments, redirectIo: true, useShellExecute: false, createNoWindow: true);

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                onOutput(args.Data);
            }
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                onError(args.Data);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    public void StartElevated(string workingDirectory, string arguments)
    {
        Process.Start(CreateStartInfo(workingDirectory, arguments, redirectIo: false, useShellExecute: true, createNoWindow: false, verb: "runas"));
    }

    private ProcessStartInfo CreateStartInfo(
        string workingDirectory,
        string arguments,
        bool redirectIo,
        bool useShellExecute,
        bool createNoWindow,
        string? verb = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _powerShellPath,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = redirectIo,
            RedirectStandardError = redirectIo,
            UseShellExecute = useShellExecute,
            CreateNoWindow = createNoWindow
        };

        if (!string.IsNullOrWhiteSpace(verb))
        {
            startInfo.Verb = verb;
        }

        if (!useShellExecute)
        {
            NormalizePathEnvironment(startInfo);
        }

        return startInfo;
    }

    private static void NormalizePathEnvironment(ProcessStartInfo startInfo)
    {
        var pathValue = Environment.GetEnvironmentVariable("Path")
            ?? Environment.GetEnvironmentVariable("PATH")
            ?? string.Empty;

        if (startInfo.Environment.ContainsKey("PATH"))
        {
            startInfo.Environment.Remove("PATH");
        }

        startInfo.Environment["Path"] = pathValue;
    }
}

internal sealed record PowerShellResult(int ExitCode, string StandardOutput, string StandardError);

