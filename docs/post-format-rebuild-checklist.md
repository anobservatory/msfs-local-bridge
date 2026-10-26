# Windows Rebuild Checklist

Build the desktop app, bridge, and optional native SimConnect worker from the
single `anobservatory/msfs-local-bridge` checkout. Its folder name and parent
folder are unrestricted. The web application remains a separate repository.

## Developer prerequisites

- Windows x64, Git, and the .NET SDK for the projects' target framework.
- For native worker builds: MSVC, Windows SDK, and CMake (Desktop development
  with C++ in Visual Studio Build Tools).
- SimConnect DLLs in `lib/`: `Microsoft.FlightSimulator.SimConnect.dll` and
  `SimConnect.dll`. Keep these files in the checkout and release payload.

## Build and package

From the repository root:

```powershell
dotnet build .\MsfsLocalBridge.Windows.slnx -c Release -m:1
dotnet run --project .\tests\WorkspaceChecks\WorkspaceChecks.csproj -c Release --no-build
.\publish-windows.ps1 -Package lite
```

The desktop build publishes the root bridge project into its own `bridge/`
payload. It has no dependency on another repository or its build output.

For the optional native worker:

```powershell
.\publish-windows.ps1 -Package lite -BuildNativeWorker
```

The desktop selects the worker when the packaged executable exists, otherwise
it uses the existing embedded SimConnect mode. Building native code requires
the C++ toolchain; the .NET-only fallback does not.

Outputs are under `dist/windows/`: a runnable folder, portable ZIP, and SHA256
file. The desktop entry point is the top-level `MsfsLocalBridge.exe`; the
similarly named executable inside `bridge/` is the telemetry runtime.

`-Package self-contained` includes .NET for both processes. The default `lite`
package keeps the existing system-runtime installation model. These modes do
not remove native VC++ or WebView2 requirements.

The root `publish-v0.ps1` remains the advanced/manual bridge-only publisher.
It does not produce the desktop app.

## Verify the extracted portable package

Use PowerShell 7 for the integration smoke script:

```powershell
.\tests\smoke-windows.ps1 -PackageDirectory '<extracted portable folder>'
.\tests\smoke-windows.ps1 -PackageDirectory '<extracted portable folder>' -TestWorkerPath '.\tests\WorkspaceChecks\bin\Release\net10.0-windows\WorkspaceChecks.exe'
```

The first check launches the packaged embedded bridge on a temporary loopback
port and checks HTTP health and a WebSocket handshake. The second sends clearly
identified synthetic IPC telemetry through the packaged bridge and verifies
its version-1 stream. The fixture is never copied into the release package.
Neither check proves an actual MSFS flight or native SimConnect connection.

Open the extracted desktop app and confirm:

1. Host checks finish and the console resources load.
2. Start Bridge opens the stream; HTTP health answers on port 39000.
3. Restart Bridge restores the stream.
4. Stop Bridge and closing the desktop app release the stream/processes.
5. Open AO uses the generated LAN connect link and browser permission flow.

Run with MSFS in a loaded cockpit to verify actual aircraft position and
telemetry. Separately verify another LAN device and permission/firewall failure
recovery before a public release. The current default is plain local-network
WebSocket on port 39000; certificates and port 39002 are not required.

## Repository migration

The desktop source was imported under `desktop/` with its Git history intact.
Its project is now `desktop/MsfsLocalBridge/MsfsLocalBridge.Desktop.csproj`.
The imported desktop history is preserved on the default branch. Build and
source references use this repository, so the former standalone desktop
repository is no longer required for development, packaging, or runtime use.
