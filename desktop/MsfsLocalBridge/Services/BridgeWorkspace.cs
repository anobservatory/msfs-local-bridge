using System.IO;

namespace MsfsLocalBridge.Services;

internal sealed class BridgeWorkspace
{
    public string HostConsoleRoot { get; }
    public string HostConsoleIndexPath { get; }
    public string BridgeRepoRoot { get; }
    public string BridgeScriptPath { get; }
    public string DiagnosticsScriptPath { get; }
    public string RepairScriptPath { get; }
    public bool UsesBundledBridge { get; }

    public BridgeWorkspace(string? baseDirectory = null)
    {
        baseDirectory = Path.GetFullPath(baseDirectory ?? AppContext.BaseDirectory);
        HostConsoleRoot = ResolveHostConsoleRoot(baseDirectory);
        HostConsoleIndexPath = Path.Combine(HostConsoleRoot, "index.html");

        var bundledBridgeRoot = ResolveBundledBridgeRoot(baseDirectory);
        var sourceBridgeRoot = ResolveBridgeRepoRoot(baseDirectory);

        if (bundledBridgeRoot is not null)
        {
            BridgeRepoRoot = bundledBridgeRoot;
            UsesBundledBridge = true;
        }
        else if (sourceBridgeRoot is not null)
        {
            BridgeRepoRoot = sourceBridgeRoot;
            UsesBundledBridge = false;
        }
        else
        {
            throw new DirectoryNotFoundException("Could not find bridge payload.");
        }

        BridgeScriptPath = Path.Combine(BridgeRepoRoot, "run-bridge.ps1");
        DiagnosticsScriptPath = Path.Combine(BridgeRepoRoot, "diagnostics-v0.ps1");
        RepairScriptPath = Path.Combine(BridgeRepoRoot, "repair-elevated-v0.ps1");
    }

    private static string ResolveHostConsoleRoot(string baseDirectory)
    {
        var bundledCandidate = Path.Combine(baseDirectory, "resources", "host-console");
        if (HasHostConsole(bundledCandidate))
        {
            return bundledCandidate;
        }

        var legacyCandidate = Path.Combine(baseDirectory, "windows-host-onboarding-lna");
        if (HasHostConsole(legacyCandidate))
        {
            return legacyCandidate;
        }

        return bundledCandidate;
    }

    private static string? ResolveBundledBridgeRoot(string baseDirectory)
    {
        var candidate = Path.Combine(baseDirectory, "bridge");
        return HasBridgePayload(candidate) ? candidate : null;
    }

    private static string? ResolveBridgeRepoRoot(string baseDirectory)
    {
        var current = new DirectoryInfo(baseDirectory);

        while (current is not null)
        {
            // Source builds live inside this repository, regardless of its folder name.
            if (File.Exists(Path.Combine(current.FullName, "MsfsLocalBridge.csproj"))
                && HasBridgePayload(current.FullName))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return null;
    }

    private static bool HasBridgePayload(string candidate)
    {
        return Directory.Exists(candidate)
            && File.Exists(Path.Combine(candidate, "run-bridge.ps1"))
            && File.Exists(Path.Combine(candidate, "diagnostics-v0.ps1"));
    }

    private static bool HasHostConsole(string candidate)
    {
        return Directory.Exists(candidate)
            && File.Exists(Path.Combine(candidate, "index.html"));
    }
}

