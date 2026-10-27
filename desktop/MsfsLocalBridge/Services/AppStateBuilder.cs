using System.Net;
using MsfsLocalBridge.Models;

namespace MsfsLocalBridge.Services;

internal sealed class AppStateBuilder
{
    public AppState Build(DiagnosticsResult diagnostics, string diagnosticsJson, BridgeSessionService session, PrerequisiteStatus prerequisites, bool allowLanAccess = false)
    {
        var lanCheck = FindCheck(diagnostics, "network.lan_ipv4");
        var firewallBridgeCheck = FindCheck(diagnostics, "network.firewall_private_39000");
        var sharingProfileCheck = FindCheck(diagnostics, "network.sharing_profile");
        var sharingProfileReady = sharingProfileCheck?.Status == "pass";
        var portBridgeCheck = FindCheck(diagnostics, "network.port_39000");

        var hostIp = ExtractLanIp(lanCheck?.Message) ?? "Not available";
        var sharedBridgeUrl = hostIp == "Not available" ? "Not available" : $"ws://{hostIp}:39000/stream";
        var localBridgeUrl = "ws://127.0.0.1:39000/stream";
        var bootstrapUrl = hostIp == "Not available" ? "Not available" : $"http://{hostIp}:39000/bootstrap";
        var connectUrl = sharedBridgeUrl == "Not available"
            ? "Not available"
            : $"https://anobservatory.com/?msfsBridgeUrl={WebUtility.UrlEncode(sharedBridgeUrl)}";

        var firewallReady = firewallBridgeCheck?.Status == "pass";
        var portAvailable = portBridgeCheck?.Status == "pass" || session.IsRunning;
        var hasRequiredHostRuntime = prerequisites.HasRequiredDotNetRuntimes && prerequisites.HasVcRedist;
        var requiredFirewallReady = !allowLanAccess || firewallReady;
        var bridgeStartReady = hasRequiredHostRuntime && requiredFirewallReady && portAvailable;
        var hasLocalBridgeAddress = sharedBridgeUrl != "Not available";
        var listenerSetupReady = hasRequiredHostRuntime && session.IsRunning;
        var canShareConnection = listenerSetupReady && allowLanAccess && firewallReady && hasLocalBridgeAddress && sharingProfileReady;
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
            .Where(check => allowLanAccess || (check.Id != "network.firewall_private_39000" && check.Id != "network.lan_ipv4" && check.Id != "network.sharing_profile"))
            .OrderBy(check => PriorityFor(check.Id))
            .ThenBy(check => check.Id, StringComparer.OrdinalIgnoreCase)
            .Select(check => check.Message);
        issues.AddRange(prioritizedChecks);

        var blockerCount = (prerequisites.HasRequiredDotNetRuntimes ? 0 : 1)
            + (prerequisites.HasVcRedist ? 0 : 1)
            + (requiredFirewallReady ? 0 : 1)
            + (portAvailable ? 0 : 1);

        var bootstrapStatus = BuildBootstrapStatus(hasRequiredHostRuntime, requiredFirewallReady, portAvailable, session.IsRunning);
        var startBridgeState = session.IsRunning ? "Running" : bridgeStartReady ? "Action" : "Locked";
        var startBridgeNote = BuildStartBridgeNote(hasRequiredHostRuntime, requiredFirewallReady, portAvailable, session.IsRunning, localBridgeUrl, hasStartFailure, startFailure);
        var listenerSetupNote = session.IsRunning ? "Visit anobservatory.com, select MSFS Connect, and Start Sync. Allow browser local network access if asked." : "Start Bridge, then open anobservatory.com.";

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
            SecureStream = "39000 /stream",
            LastIssue = issues.FirstOrDefault() ?? "No issues",
            ConnectUrl = connectUrl,
            AllowLanAccess = allowLanAccess,
            CanShareConnection = canShareConnection,
            CanEnableSharing = hasRequiredHostRuntime && hasLocalBridgeAddress && sharingProfileReady,
            SharingStatus = !sharingProfileReady ? (sharingProfileCheck?.Status == "fail" ? "Private network required" : "Check network profile")
                : !allowLanAccess ? "Off" : !firewallReady ? "Firewall setup needed" : !session.IsRunning ? "Start Bridge" : !hasLocalBridgeAddress ? "LAN address needed" : "Ready",
            SharingNote = !sharingProfileReady ? $"{sharingProfileCheck?.Message ?? "Network profile has not been verified."} In Windows network settings, choose Private only for a trusted home network, then return here. Local sync is still available."
                : !allowLanAccess ? "Other device access is off. Enable it to share telemetry on your private network. Windows may ask for administrator permission."
                : !firewallReady ? "Private network firewall access needs repair. Enable access again to repair it."
                : !hasLocalBridgeAddress ? "No LAN address found. Connect the flight PC to your private network."
                : !session.IsRunning ? "Access enabled. Start Bridge to show the QR and connection link."
                : "Scan the QR or open the link on a device on the same private network. Allow browser local network access if asked.",
            LocalBridgeUrl = localBridgeUrl,
            BootstrapUrl = bootstrapUrl,
            RuntimeLog = string.IsNullOrWhiteSpace(session.RuntimeLog) ? diagnosticsJson : session.RuntimeLog,
            DotNetStepText = prerequisites.HasRequiredDotNetRuntimes ? "Installed" : "Action",
            DotNetButtonText = prerequisites.HasRequiredDotNetRuntimes ? "Installed" : "Open .NET Download",
            DotNetCurrentNote = prerequisites.DotNetRuntimeStatus,
            VcRedistStepText = prerequisites.HasVcRedist ? "Installed" : "Action",
            VcRedistButtonText = prerequisites.HasVcRedist ? "Installed" : "Install VC++ Runtime",
            VcRedistCurrentNote = prerequisites.HasVcRedist ? prerequisites.VcRedistStatus : "not installed on this PC.",
            FirewallStepText = !allowLanAccess ? "Optional" : firewallReady ? "Ready" : (hasRequiredHostRuntime ? "Action" : "Locked"),
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

    private static string BuildBootstrapStatus(bool hasRequiredHostRuntime, bool firewallReady, bool portAvailable, bool bridgeRunning)
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

        return "Ready";
    }

    private static string BuildStartBridgeNote(bool hasRequiredHostRuntime, bool firewallReady, bool portAvailable, bool bridgeRunning, string localBridgeUrl, bool hasStartFailure, string? startFailure)
    {
        if (bridgeRunning)
        {
            return $"Bridge is running at {localBridgeUrl}. Open AO and allow browser local network access.";
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

