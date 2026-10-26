using MsfsLocalBridge.Services;
using MsfsLocalBridge.Models;
using System.Text.Json;

// A deterministic IPC fixture for package smoke tests, never a production worker.
if (args.Contains("--test-worker"))
{
    while (true)
    {
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            type = "telemetry",
            snapshot = new
            {
                id = "test:package-smoke", callsign = "AOTEST", simVersionLabel = "TEST FIXTURE",
                lat = 37.6189, lon = -122.375, altBaroFt = 3500, altGeomFt = 3520,
                gsKt = 110, headingDegTrue = 182, trackDegTrue = 181, vsFpm = -300,
                onGround = false, timestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            }
        }));
        await Task.Delay(200);
    }
}

var fixture = Path.Combine(Path.GetTempPath(), "ao-workspace-checks", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(fixture);
try
{
    var source = Path.Combine(fixture, "renamed checkout with spaces");
    var output = Path.Combine(source, "desktop", "MsfsLocalBridge", "bin", "Release");
    Directory.CreateDirectory(output);
    CreatePayload(source);
    File.WriteAllText(Path.Combine(source, "MsfsLocalBridge.csproj"), "<Project />");
    var workspace = new BridgeWorkspace(output);
    Require(workspace.BridgeRepoRoot == source && !workspace.UsesBundledBridge,
        "Find the unified source repository without relying on its folder name or a sibling checkout.");

    var settingsPath = Path.Combine(fixture, "settings", "connection.json");
    var settings = new ConnectionSettings(settingsPath);
    Require(!settings.AllowLanAccess, "First launch defaults to local access only.");
    settings.Save(true);
    Require(new ConnectionSettings(settingsPath).AllowLanAccess, "Remember optional sharing across restarts.");
    settings.Save(false);
    Require(!new ConnectionSettings(settingsPath).AllowLanAccess, "Disabling sharing is persistent.");
    File.WriteAllText(settingsPath, "invalid");
    Require(!new ConnectionSettings(settingsPath).AllowLanAccess, "Invalid settings fail closed to local access.");

    var diagnostics = new DiagnosticsResult { Checks = [
        new() { Id = "network.firewall_private_39000", Status = "warn", Message = "Firewall missing" },
        new() { Id = "network.lan_ipv4", Status = "warn", Message = "No LAN address" },
        new() { Id = "network.port_39000", Status = "pass" }
    ] };
    var prerequisites = new PrerequisiteStatus { HasDotNetDesktopRuntime = true, HasAspNetCoreRuntime = true, HasVcRedist = true };
    var session = new BridgeSessionService(workspace, new PowerShellRunner(), settings);
    var state = new AppStateBuilder().Build(diagnostics, "{}", session, prerequisites);
    Require(state.CanStartBridge && state.BlockerText == "0 blockers" && state.LastIssue == "No issues",
        "Local access does not depend on an inbound rule or LAN address.");
    Require(state.OpenAoUrl == "https://anobservatory.com/", "Open AO uses the ordinary site address.");
    Require(!state.CanShareConnection && state.FirewallStepText == "Optional", "No sharing link before access is enabled.");
    state = new AppStateBuilder().Build(diagnostics, "{}", session, prerequisites, allowLanAccess: true);
    Require(!state.CanStartBridge && state.BlockerText == "1 blocker", "Enabled LAN access requires its private firewall rule.");

    var bundled = Path.Combine(output, "bridge");
    CreatePayload(bundled);
    var console = Path.Combine(output, "resources", "host-console");
    Directory.CreateDirectory(console);
    File.WriteAllText(Path.Combine(console, "index.html"), "<!doctype html>");
    workspace = new BridgeWorkspace(output);
    Require(workspace.BridgeRepoRoot == bundled && workspace.UsesBundledBridge,
        "Prefer the bundled bridge even when a source checkout is present.");
    Require(workspace.HostConsoleIndexPath == Path.Combine(console, "index.html"),
        "Resolve the packaged WebView2 console.");

    var missing = Path.Combine(fixture, "missing payload");
    Directory.CreateDirectory(missing);
    try
    {
        _ = new BridgeWorkspace(missing);
        throw new Exception("A missing bridge payload was accepted.");
    }
    catch (DirectoryNotFoundException)
    {
        Console.WriteLine("PASS: Reject a missing bridge payload.");
    }
}
finally
{
    Directory.Delete(fixture, recursive: true);
}

static void CreatePayload(string path)
{
    Directory.CreateDirectory(path);
    File.WriteAllText(Path.Combine(path, "run-bridge.ps1"), "# fixture");
    File.WriteAllText(Path.Combine(path, "diagnostics-v0.ps1"), "# fixture");
}

static void Require(bool condition, string description)
{
    if (!condition) throw new Exception(description);
    Console.WriteLine($"PASS: {description}");
}
