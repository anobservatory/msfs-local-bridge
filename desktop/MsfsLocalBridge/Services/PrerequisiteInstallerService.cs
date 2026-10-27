using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using Microsoft.Win32;
using MsfsLocalBridge.Models;

namespace MsfsLocalBridge.Services;

internal sealed class PrerequisiteInstallerService
{
    internal const string RecommendedDotNetRuntimeVersion = "10.0.14";
    internal const string RequiredDotNetChannel = "10.0";
    private static readonly string DotNetDesktopInstallerUrl = $"https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/{RecommendedDotNetRuntimeVersion}/windowsdesktop-runtime-{RecommendedDotNetRuntimeVersion}-win-x64.exe";
    private static readonly string AspNetCoreInstallerUrl = $"https://builds.dotnet.microsoft.com/dotnet/aspnetcore/Runtime/{RecommendedDotNetRuntimeVersion}/aspnetcore-runtime-{RecommendedDotNetRuntimeVersion}-win-x64.exe";
    private const string VcRedistInstallerUrl = "https://aka.ms/vc14/vc_redist.x64.exe";

    public PrerequisiteStatus DetectStatus()
    {
        var (desktopStatus, hasDesktop, desktopVersion) = DetectSharedRuntime("Microsoft.WindowsDesktop.App", "Desktop runtime");
        var (aspNetStatus, hasAspNet, aspNetVersion) = DetectSharedRuntime("Microsoft.AspNetCore.App", "ASP.NET Core runtime");
        var (vcStatus, hasVcRedist) = DetectVcRedist();

        return new PrerequisiteStatus
        {
            DotNetDesktopRuntimeStatus = desktopStatus,
            HasDotNetDesktopRuntime = hasDesktop,
            AspNetCoreRuntimeStatus = aspNetStatus,
            HasAspNetCoreRuntime = hasAspNet,
            DotNetRuntimeStatus = BuildDotNetRuntimeStatus(hasDesktop, desktopVersion, hasAspNet, aspNetVersion),
            VcRedistStatus = vcStatus,
            HasVcRedist = hasVcRedist
        };
    }

    public Task<string> InstallDotNetDesktopRuntimeAsync()
    {
        var status = DetectStatus();
        var opened = new List<string>();

        if (!status.HasDotNetDesktopRuntime)
        {
            OpenExternal(DotNetDesktopInstallerUrl);
            opened.Add(".NET Desktop Runtime x64");
        }

        if (!status.HasAspNetCoreRuntime)
        {
            OpenExternal(AspNetCoreInstallerUrl);
            opened.Add("ASP.NET Core Runtime x64");
        }

        return Task.FromResult(opened.Count == 0
            ? ".NET runtimes are already installed."
            : $"Opened official download link(s): {string.Join(", ", opened)}. Install them, then return to the app.");
    }

    public async Task<string> InstallVcRedistAsync()
    {
        var installerPath = await DownloadInstallerAsync(VcRedistInstallerUrl, "vc_redist.x64.exe");
        var installerExitCode = await RunElevatedAsync(installerPath, "/quiet /norestart");
        return installerExitCode switch
        {
            0 => "VC++ Redistributable x64 installer completed.",
            3010 => "VC++ Redistributable x64 installer completed and requires restart.",
            1223 => "VC++ Redistributable install was canceled.",
            _ => $"VC++ Redistributable x64 installer exited with code {installerExitCode}."
        };
    }

    internal static (string Status, bool Installed, string Version) DetectSharedRuntime(
        string sharedFrameworkName, string displayName, string? dotNetRoot = null)
    {
        var root = Path.Combine(dotNetRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet"), "shared", sharedFrameworkName);
        var missingStatus = $"Missing {displayName} ({RequiredDotNetChannel}.x x64)";
        if (!Directory.Exists(root))
        {
            return (missingStatus, false, string.Empty);
        }

        var channel = Version.Parse(RequiredDotNetChannel);
        var versions = Directory.GetDirectories(root)
            .Select(Path.GetFileName)
            .Select(name => (Name: name, Parsed: Version.TryParse(name, out var version) ? version : null))
            .Where(entry => entry.Parsed is { } version && version.Major == channel.Major &&
                version.Minor == channel.Minor && version.Build >= 0 && version.Revision < 0)
            .OrderByDescending(entry => entry.Parsed)
            .Select(entry => entry.Name)
            .ToArray();

        if (versions.Length == 0)
        {
            return (missingStatus, false, string.Empty);
        }

        return ($"Installed {displayName} ({versions[0]})", true, versions[0]!);
    }

    private static string BuildDotNetRuntimeStatus(bool hasDesktop, string desktopVersion, bool hasAspNet, string aspNetVersion)
    {
        if (hasDesktop && hasAspNet)
        {
            return $"Installed (Desktop {desktopVersion}; ASP.NET Core {aspNetVersion})";
        }

        var missing = new List<string>();
        if (!hasDesktop)
        {
            missing.Add("Desktop");
        }

        if (!hasAspNet)
        {
            missing.Add("ASP.NET Core");
        }

        return $"Missing ({string.Join(", ", missing)})";
    }

    private static (string Status, bool Installed) DetectVcRedist()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64");
            if (key is null)
            {
                return ("Missing", false);
            }

            var installed = Convert.ToInt32(key.GetValue("Installed", 0)) == 1;
            var version = key.GetValue("Version")?.ToString() ?? "unknown";
            return installed ? ($"Installed ({version})", true) : ("Missing", false);
        }
        catch (Exception ex)
        {
            return ($"Unknown ({ex.Message})", false);
        }
    }

    private static async Task<string> DownloadInstallerAsync(string url, string fileName)
    {
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        var tempRoot = Path.Combine(Path.GetTempPath(), "msfs-local-bridge-desktop-prereqs");
        Directory.CreateDirectory(tempRoot);
        var targetPath = Path.Combine(tempRoot, fileName);

        using var response = await httpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync();
        await using var output = File.Create(targetPath);
        await input.CopyToAsync(output);

        return targetPath;
    }

    private static async Task<int> RunElevatedAsync(string fileName, string arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = true,
                Verb = "runas"
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                throw new InvalidOperationException($"Failed to start installer: {fileName}");
            }

            await process.WaitForExitAsync();
            return process.ExitCode;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return 1223;
        }
    }

    private static void OpenExternal(string target)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = target,
            UseShellExecute = true
        });
    }
}

