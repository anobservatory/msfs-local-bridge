# Compatible prerequisite updates

The Lite package targets .NET 10.0 on Windows x64. The Desktop host needs
`Microsoft.WindowsDesktop.App`; its bundled server needs `Microsoft.AspNetCore.App`.
Only stable 10.0.x versions satisfy detection. Another major version or a preview
does not substitute for the required shared framework. Compatible versions are
sorted numerically, independently for each framework.

As checked on 2026-10-27, Microsoft's latest stable 10.0 release is 10.0.14.
The Desktop and ASP.NET Core download links now recommend that patch. Detection
continues to accept compatible older 10.0.x installations and future stable
patches; this update does not force upgrades on users' PCs.

## Periodic maintainer review

Run this read-only check from PowerShell in the repository root:

```powershell
./tests/check-runtime-updates.ps1
```

It reads the supported channel and recommended version from
`PrerequisiteInstallerService.cs`, fetches Microsoft's official release metadata,
and reports a newer stable patch together with the official x64 installer URLs
and SHA512 hashes. It never installs software or changes the repository. The
optional `-FailIfOutdated` exits with code 1 when an update is available, for a
maintainer-owned scheduled check. Network or metadata errors fail the check;
they do not mean the installed software is current.

Review monthly and after security releases. Update
`RecommendedDotNetRuntimeVersion` only after verifying both installer entries,
running the Windows solution/host checks, and validating the resulting package.
Major-channel migrations require a separate compatibility review and cannot be
handled by changing this patch constant alone.

VC++ already uses Microsoft's permalink for the latest supported v14 x64
Redistributable. WebView2 uses the Evergreen Runtime in the Desktop host; its
updates are managed by Microsoft subject to the host's update policies. These
runtime updates are separate from the WebView2 SDK NuGet version used to build
the host. Test host rendering when reviewing compatibility.

## First-run setup status

This change updates the existing prerequisite guidance. The .NET button still
opens the official installer links. VC++ still uses its existing download and
elevated-install flow. A single first-run setup helper that starts without .NET,
verifies downloads, installs missing components, and then opens the Desktop
host remains a separate implementation task. The runtime detection check does
not make the Lite WPF app start on a PC without Desktop Runtime.

Sources:

- [.NET 10 release metadata](https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json)
- [Latest supported VC++ Redistributable](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist)
- [WebView2 distribution and updates](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution)
