namespace MsfsLocalBridge.Models;

internal sealed class PrerequisiteStatus
{
    public string DotNetDesktopRuntimeStatus { get; set; } = "Missing";
    public bool HasDotNetDesktopRuntime { get; set; }
    public string AspNetCoreRuntimeStatus { get; set; } = "Missing";
    public bool HasAspNetCoreRuntime { get; set; }
    public string DotNetRuntimeStatus { get; set; } = "Missing";
    public bool HasRequiredDotNetRuntimes => HasDotNetDesktopRuntime && HasAspNetCoreRuntime;
    public string VcRedistStatus { get; set; } = "Missing";
    public bool HasVcRedist { get; set; }
}

