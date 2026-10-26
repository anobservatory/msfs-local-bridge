using System.Net;
using MsfsLocalBridge.Models;

namespace MsfsLocalBridge.Services;

internal sealed class AppStateBuilder
{
    public AppState Build(DiagnosticsResult diagnostics, string diagnosticsJson, BridgeSessionService session, PrerequisiteStatus prerequisites)
    {
        var lanCheck = FindCheck(diagnostics, "network.lan_ipv4");
        var firewallBridgeCheck = FindCheck(diagnostics, "network.firewall_private_39000");
        var portBridgeCheck = FindCheck(diagnostics, "network.port_39000");

        var hostIp = ExtractLanIp(lanCheck?.Message) ?? "Not available";
        var localBridgeUrl = hostIp == "Not available" ? "Not available" : $"ws://{hostIp}:39000/stream";
        var bootstrapUrl = hostIp == "Not available" ? "Not available" : $"http://{hostIp}:39000/bootstrap";
        var connectUrl = localBridgeUrl == "Not available"
            ? "Not available"
            : $"https://anobservatory.com/?msfsBridgeUrl={WebUtility.UrlEncode(localBridgeUrl)}";

        var firewallReady = firewallBridgeCheck?.Status == "pass";
        var portAvailable = portBridgeCheck?.Status == "pass" || session.IsRunning;
        var hasRequiredHostRuntime = prerequisites.HasRequiredDotNetRuntimes && prerequisites.HasVcRedist;
        var bridgeStartReady = hasRequiredHostRuntime && firewallReady && portAvailable;
        var hasLocalBridgeAddress = localBridgeUrl != "Not available";
        var listenerSetupReady = bridgeStartReady && session.IsRunning && hasLocalBridgeAddress;
        var issues = new List<string>();
        var startFailure = session.LastFailureReason;
        var hasStartFailure = !string.IsNullOrWhiteSpace(startFailure);

        if (hasStartFailure)
        {
            issues.Add(startFailure!);
        }

        if (!prerequisites.HasRequiredDotNetRuntimes)
        {
            issues.Add(prerequisites.DotNetRuntimeStatus);
        }

        if (!prerequisites.HasVcRedist)
        {
            issues.Add(prerequisites.VcRedistStatus);
        }

        var prioritizedChecks = diagnostics.Checks
            .Where(check => check.Status != "pass")
            .Where(check => IsRelevantLnaCheck(check.Id, session.IsRunning))
            .OrderBy(check => PriorityFor(check.Id))
            .ThenBy(check => check.Id, StringComparer.OrdinalIgnoreCase)
            .Select(check => check.Message);
        issues.AddRange(prioritizedChecks);

        var blockerCount = (prerequisites.HasRequiredDotNetRuntimes ? 0 : 1)
            + (prerequisites.HasVcRedist ? 0 : 1)
            + (firewallReady ? 0 : 1)
            + (portAvailable ? 0 : 1);

        var bootstrapStatus = BuildBootstrapStatus(hasRequiredHostRuntime, firewallReady, portAvailable, session.IsRunning, hasLocalBridgeAddress);
        var startBridgeState = session.IsRunning ? "Running" : bridgeStartReady ? "Action" : "Locked";
        var startBridgeNote = BuildStartBridgeNote(hasRequiredHostRuntime, firewallReady, portAvailable, session.IsRunning, localBridgeUrl, hasStartFailure, startFailure);
        var listenerSetupNote = BuildListenerSetupNote(hasRequiredHostRuntime, firewallReady, portAvailable, session.IsRunning, hasLocalBridgeAddress);

        var bridgeStatus = session.IsRunning
            ? "Running"
            : hasStartFailure
                ? "Start failed"
                : bridgeStartReady
                    ? "Ready to start"
                    : "Setup needed";

        var bridgeControlText = session.IsRunning
            ? "Running"
            : hasStartFailure
                ? "Failed"
                : bridgeStartReady
                    ? "Ready"
                    : "Setup needed";

        var primaryActionText = session.IsRunning
            ? "Bridge Running"
            : hasStartFailure
                ? "Retry Start"
                : bridgeStartReady
                    ? "Start Bridge"
                    : "Finish Setup";

        var simConnectStatus = session.IsRunning
            ? "Waiting for flight"
            : hasStartFailure
                ? "Bridge failed"
                : bridgeStartReady
                    ? "Waiting for bridge"
                    : "Finish setup";

        return new AppState
        {
            BlockerText = blockerCount == 1 ? "1 blocker" : $"{blockerCount} blockers",
            DotNetStatus = prerequisites.DotNetRuntimeStatus,
            SimConnectStatus = simConnectStatus,
            BridgeStatus = bridgeStatus,
            BootstrapStatus = bootstrapStatus,
            BridgeControlText = bridgeControlText,
            PrimaryActionText = primaryActionText,
            HostIp = hostIp,
            SecureStream = localBridgeUrl == "Not available" ? "Not available" : "39000 /stream",
            LastIssue = issues.FirstOrDefault() ?? "No issues",
            ConnectUrl = connectUrl,
            LocalBridgeUrl = localBridgeUrl,
            BootstrapUrl = bootstrapUrl,
            RuntimeLog = string.IsNullOrWhiteSpace(session.RuntimeLog) ? diagnosticsJson : session.RuntimeLog,
            DotNetStepText = prerequisites.HasRequiredDotNetRuntimes ? "Installed" : "Action",
            DotNetButtonText = prerequisites.HasRequiredDotNetRuntimes ? "Installed" : "Open .NET Download",
            DotNetCurrentNote = prerequisites.DotNetRuntimeStatus,
            VcRedistStepText = prerequisites.HasVcRedist ? "Installed" : "Action",
            VcRedistButtonText = prerequisites.HasVcRedist ? "Installed" : "Install VC++ Runtime",
            VcRedistCurrentNote = prerequisites.HasVcRedist ? prerequisites.VcRedistStatus : "not installed on this PC.",
            FirewallStepText = firewallReady ? "Ready" : (hasRequiredHostRuntime ? "Action" : "Locked"),
            StartBridgeStepText = startBridgeState,
            StartBridgeButtonText = session.IsRunning ? "Bridge Running" : "Start Bridge",
            StartBridgeCurrentNote = startBridgeNote,
            ListenerSetupState = listenerSetupReady ? "Ready" : bootstrapStatus,
            ListenerSetupNote = listenerSetupNote,
            CanStartBridge = !session.IsRunning && bridgeStartReady,
            CanStopBridge = session.IsRunning,
            CanRestartBridge = session.IsRunning,
            CanInstallDotNet = !prerequisites.HasRequiredDotNetRuntimes,
            CanInstallVcRedist = !prerequisites.HasVcRedist,
            CanOpenFirewallRules = hasRequiredHostRuntime,
            CanUseListenerSetup = listenerSetupReady
        };
    }

    private static string BuildBootstrapStatus(bool hasRequiredHostRuntime, bool firewallReady, bool portAvailable, bool bridgeRunning, bool hasLocalBridgeAddress)
    {
        if (!hasRequiredHostRuntime)
        {
            return "Install runtimes";
        }

        if (!firewallReady)
        {
            return "Firewall first";
        }

        if (!portAvailable)
        {
            return "Port in use";
        }

        if (!bridgeRunning)
        {
            return "Start bridge first";
        }

        if (!hasLocalBridgeAddress)
        {
            return "LAN IP needed";
        }

        return "Ready";
    }

    private static string BuildStartBridgeNote(bool hasRequiredHostRuntime, bool firewallReady, bool portAvailable, bool bridgeRunning, string localBridgeUrl, bool hasStartFailure, string? startFailure)
    {
        if (bridgeRunning)
        {
            return localBridgeUrl == "Not available"
                ? "Bridge is running, but the host LAN address is not available yet."
                : $"Bridge is running at {localBridgeUrl}. Open AO and allow browser local network access.";
        }

        if (hasStartFailure)
        {
            return startFailure ?? "Bridge startup failed.";
        }

        if (!hasRequiredHostRuntime)
        {
            return "Install .NET and VC++ on this host PC first.";
        }

        if (!firewallReady)
        {
            return "Open inbound TCP 39000 on the private network before starting the bridge.";
        }

        if (!portAvailable)
        {
            return "TCP 39000 is already in use. Stop the conflicting process before starting the bridge.";
        }

        return "Start the local stream before opening AO in the browser.";
    }

    private static string BuildListenerSetupNote(bool hasRequiredHostRuntime, bool firewallReady, bool portAvailable, bool bridgeRunning, bool hasLocalBridgeAddress)
    {
        if (!hasRequiredHostRuntime)
        {
            return "Install .NET and VC++ on the host PC first.";
        }

        if (!firewallReady)
        {
            return "Open inbound TCP 39000 on the private network before opening AO.";
        }

        if (!portAvailable)
        {
            return "TCP 39000 is already in use. Stop the conflicting process before starting the bridge.";
        }

        if (!bridgeRunning)
        {
            return "Start the bridge on the host PC before opening AO.";
        }

        if (!hasLocalBridgeAddress)
        {
            return "The host LAN address could not be detected yet, so AO cannot connect from this network.";
        }

        return "Open AO with this session's bridge URL. After that, use anobservatory.com normally.";
    }

    private static int PriorityFor(string id)
    {
        if (id.StartsWith("network.port_", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (id.StartsWith("runtime.", StringComparison.OrdinalIgnoreCase) || id.StartsWith("dependency.", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (id.StartsWith("network.wss_", StringComparison.OrdinalIgnoreCase) || id.StartsWith("network.root_ca", StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        if (id.StartsWith("network.firewall_", StringComparison.OrdinalIgnoreCase))
        {
            return 3;
        }

        return 4;
    }

    private static bool IsRelevantLnaCheck(string id, bool bridgeRunning)
    {
        if (id.StartsWith("network.wss_", StringComparison.OrdinalIgnoreCase) ||
            id.StartsWith("network.root_ca", StringComparison.OrdinalIgnoreCase) ||
            id.StartsWith("network.firewall_private_39002", StringComparison.OrdinalIgnoreCase) ||
            id.StartsWith("network.port_39002", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (bridgeRunning && id.StartsWith("network.port_39000", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static DiagnosticsCheck? FindCheck(DiagnosticsResult diagnostics, string id)
    {
        return diagnostics.Checks.FirstOrDefault(check => string.Equals(check.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    private static string? ExtractLanIp(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        var marker = "detected: ";
        var markerIndex = message.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return null;
        }

        var start = markerIndex + marker.Length;
        var end = message.IndexOf(" (", start, StringComparison.Ordinal);
        if (end < 0)
        {
            end = message.Length;
        }

        var candidates = message[start..end]
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var candidate in candidates)
        {
            var normalized = candidate.Trim().TrimEnd('.', ';', ':', ')', ']');
            if (IPAddress.TryParse(normalized, out _))
            {
                return normalized;
            }
        }

        return null;
    }
}

