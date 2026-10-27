using MsfsLocalBridge.Services;

internal static class PrerequisiteChecks
{
    public static void Run(string fixture)
    {
        var root = Path.Combine(fixture, "runtime-detection");
        foreach (var framework in new[] { "Microsoft.WindowsDesktop.App", "Microsoft.AspNetCore.App" })
        {
            var missing = PrerequisiteInstallerService.DetectSharedRuntime(framework, "Runtime", root);
            Require(!missing.Installed && missing.Status.Contains("10.0.x x64"), "Missing runtime names the required channel and architecture.");
            var path = Path.Combine(root, "shared", framework);
            foreach (var version in new[] { "8.0.14", "9.0.9", "11.0.0", "10.0.15-preview.1", "invalid" })
                Directory.CreateDirectory(Path.Combine(path, version));
            Require(!PrerequisiteInstallerService.DetectSharedRuntime(framework, "Runtime", root).Installed,
                "Other major versions and preview runtimes do not satisfy the Bridge runtime requirement.");
            foreach (var version in new[] { "10.0.0", "10.0.9", "10.0.14" })
                Directory.CreateDirectory(Path.Combine(path, version));
            var installed = PrerequisiteInstallerService.DetectSharedRuntime(framework, "Runtime", root);
            Require(installed.Installed && installed.Version == "10.0.14",
                "Select the newest compatible stable patch numerically, independently for each framework.");
            Directory.CreateDirectory(Path.Combine(path, "10.0.99"));
            Require(PrerequisiteInstallerService.DetectSharedRuntime(framework, "Runtime", root).Version == "10.0.99",
                "Future stable patches in the supported channel remain compatible.");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine($"PASS: {message}");
    }
}
