using System.Text.Json;
using MsfsLocalBridge.Models;

namespace MsfsLocalBridge.Services;

internal sealed class BridgeDiagnosticsService
{
    private readonly BridgeWorkspace _workspace;
    private readonly PowerShellRunner _runner;
    private readonly JsonSerializerOptions _serializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public BridgeDiagnosticsService(BridgeWorkspace workspace, PowerShellRunner runner)
    {
        _workspace = workspace;
        _runner = runner;
    }

    public async Task<(DiagnosticsResult Result, string RawJson)> GetAsync(CancellationToken cancellationToken = default)
    {
        var arguments = $"-ExecutionPolicy Bypass -File \"{_workspace.DiagnosticsScriptPath}\" -Format Json";
        var result = await _runner.RunAsync(_workspace.BridgeRepoRoot, arguments, cancellationToken);

        if (string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            throw new InvalidOperationException($"Diagnostics failed with exit code {result.ExitCode}: {result.StandardError}");
        }

        var diagnostics = JsonSerializer.Deserialize<DiagnosticsResult>(result.StandardOutput, _serializerOptions);
        if (diagnostics is null)
        {
            throw new InvalidOperationException("Diagnostics output could not be parsed.");
        }

        return (diagnostics, result.StandardOutput);
    }
}
