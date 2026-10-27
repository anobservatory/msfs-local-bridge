using System.Text.Json;
using MsfsLocalBridge.Models;

namespace MsfsLocalBridge.Services;

internal sealed class BridgeDiagnosticsService
{
    internal static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(30);
    private readonly Func<CancellationToken, Task<(DiagnosticsResult Result, string RawJson)>> _query;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private (DiagnosticsResult Result, string RawJson)? _cached;
    private long _cachedAt;
    private long _cachedVersion = -1;
    private long _version;

    public BridgeDiagnosticsService(BridgeWorkspace workspace, PowerShellRunner runner)
        : this(token => QueryAsync(workspace, runner, token), TimeProvider.System)
    {
    }

    internal BridgeDiagnosticsService(
        Func<CancellationToken, Task<(DiagnosticsResult Result, string RawJson)>> query,
        TimeProvider clock)
    {
        _query = query;
        _clock = clock;
    }

    public void Invalidate() => Interlocked.Increment(ref _version);

    public async Task<(DiagnosticsResult Result, string RawJson)> GetAsync(
        CancellationToken cancellationToken = default, bool forceRefresh = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (forceRefresh) Invalidate();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var version = Interlocked.Read(ref _version);
            if (_cached is { } cached && _cachedVersion == version &&
                _clock.GetElapsedTime(_cachedAt) < CacheLifetime)
            {
                return cached;
            }

            var result = await _query(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            // A host change can invalidate a query already in progress.
            if (version == Interlocked.Read(ref _version))
            {
                _cached = result;
                _cachedAt = _clock.GetTimestamp();
                _cachedVersion = version;
            }
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<(DiagnosticsResult Result, string RawJson)> QueryAsync(
        BridgeWorkspace workspace, PowerShellRunner runner, CancellationToken cancellationToken)
    {
        var arguments = $"-ExecutionPolicy Bypass -File \"{workspace.DiagnosticsScriptPath}\" -Format Json";
        var result = await runner.RunAsync(workspace.BridgeRepoRoot, arguments, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            throw new InvalidOperationException($"Diagnostics failed with exit code {result.ExitCode}: {result.StandardError}");
        }

        var diagnostics = JsonSerializer.Deserialize<DiagnosticsResult>(result.StandardOutput,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (diagnostics is null)
        {
            throw new InvalidOperationException("Diagnostics output could not be parsed.");
        }

        return (diagnostics, result.StandardOutput);
    }
}
