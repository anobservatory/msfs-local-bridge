namespace MsfsLocalBridge.Models;

internal sealed class AppState
{
    public string BlockerText { get; set; } = "0 blockers";
    public string DotNetStatus { get; set; } = "Unknown";
    public string SimConnectStatus { get; set; } = "Unknown";
    public string BridgeStatus { get; set; } = "Stopped";
    public string BootstrapStatus { get; set; } = "Locked";
    public string BridgeControlText { get; set; } = "Stopped";
    public string PrimaryActionText { get; set; } = "Start Bridge";
    public string HostIp { get; set; } = "Not available";
    public string SecureStream { get; set; } = "Not available";
    public string LastIssue { get; set; } = "No issues";
    public string ConnectUrl { get; set; } = "Not available";
    public string LocalBridgeUrl { get; set; } = "Not available";
    public string BootstrapUrl { get; set; } = "Not available";
    public string RuntimeLog { get; set; } = string.Empty;
    public string DotNetStepText { get; set; } = "Action";
    public string DotNetButtonText { get; set; } = "Install .NET Runtime";
    public string DotNetCurrentNote { get; set; } = "not installed on this PC.";
    public string VcRedistStepText { get; set; } = "Action";
    public string VcRedistButtonText { get; set; } = "Install VC++ Runtime";
    public string VcRedistCurrentNote { get; set; } = "not installed on this PC.";
    public string FirewallStepText { get; set; } = "Locked";
    public string StartBridgeStepText { get; set; } = "Locked";
    public string StartBridgeButtonText { get; set; } = "Start Bridge";
    public string StartBridgeCurrentNote { get; set; } = "Complete host setup before starting the bridge.";
    public string ListenerSetupState { get; set; } = "Locked";
    public string ListenerSetupNote { get; set; } = "Complete host setup and start the bridge before onboarding listener devices.";
    public bool CanStartBridge { get; set; } = true;
    public bool CanStopBridge { get; set; }
    public bool CanRestartBridge { get; set; }
    public bool CanInstallDotNet { get; set; } = true;
    public bool CanInstallVcRedist { get; set; } = true;
    public bool CanOpenFirewallRules { get; set; } = true;
    public bool CanUseListenerSetup { get; set; }
}

