using System.Diagnostics;
using System.IO;

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
        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) when (process.HasExited) { }
            await process.WaitForExitAsync().ConfigureAwait(false);
            await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
            throw;
        }

        return new PowerShellResult(process.ExitCode, await outputTask.ConfigureAwait(false), await errorTask.ConfigureAwait(false));
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

    public async Task<int> RunElevatedAsync(string workingDirectory, string arguments)
    {
        using var process = Process.Start(CreateStartInfo(workingDirectory, arguments,
            redirectIo: false, useShellExecute: true, createNoWindow: true, verb: "runas"))
            ?? throw new InvalidOperationException("Windows did not start firewall setup.");
        await process.WaitForExitAsync();
        return process.ExitCode;
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
            Arguments = "-NoLogo -NoProfile -NonInteractive " + arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = redirectIo,
            RedirectStandardError = redirectIo,
            UseShellExecute = useShellExecute,
            CreateNoWindow = createNoWindow,
            WindowStyle = ProcessWindowStyle.Hidden
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

