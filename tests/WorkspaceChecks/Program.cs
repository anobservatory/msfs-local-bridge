using MsfsLocalBridge.Services;
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
