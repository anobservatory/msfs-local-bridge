# Post-Format Rebuild Checklist

This checklist is for rebuilding the MSFS bridge stack after reinstalling Windows or moving to a new machine.

## Repositories

Clone both repositories into the same parent folder:

- `C:\Users\A\Desktop\GitHub\msfs-local-bridge`
- `C:\Users\A\Desktop\GitHub\msfs-local-bridge-desktop`

The desktop app looks for the bridge repo next to it, so keep both folders under the same root when possible.

## Required Installs

Install these tools first:

1. Git
2. .NET 10 SDK
3. Visual Studio 2022 Build Tools

Build Tools workloads/components:

- Desktop development with C++
- MSVC build tools
- Windows SDK
- CMake tools for Windows

## MSFS SimConnect Files

Before building `msfs-local-bridge`, restore these files into `lib\`:

- `Microsoft.FlightSimulator.SimConnect.dll`
- `SimConnect.dll`

Expected location:

- `C:\Users\A\Desktop\GitHub\msfs-local-bridge\lib`

## Build Order

Build in this order:

1. Native SimConnect worker
2. Bridge package/output
3. Desktop app

## 1) Build The Native Worker

Project path:

- `C:\Users\A\Desktop\GitHub\msfs-local-bridge\workers\simconnect-native`

Typical flow:

```powershell
cmake -S . -B build -G "Visual Studio 17 2022" -A x64
cmake --build build --config Release
New-Item -ItemType Directory -Force -Path .\dist | Out-Null
Copy-Item .\build\Release\msfs-simconnect-worker.exe .\dist\msfs-simconnect-worker.exe -Force
```

Expected output:

- `C:\Users\A\Desktop\GitHub\msfs-local-bridge\workers\simconnect-native\dist\msfs-simconnect-worker.exe`

## 2) Build The Bridge

Project path:

- `C:\Users\A\Desktop\GitHub\msfs-local-bridge`

Build/package script:

```powershell
.\publish-v0.ps1
```

Expected packaged output:

- `C:\Users\A\Desktop\GitHub\msfs-local-bridge\dist`

For local development, a direct build also works:

```powershell
dotnet build .\MsfsLocalBridge.csproj -c Release
```

## 3) Build The Desktop App

Project path:

- `C:\Users\A\Desktop\GitHub\msfs-local-bridge-desktop`

Build command:

```powershell
dotnet build .\MockupShell\MockupShell.csproj -c Release
```

Expected output:

- `C:\Users\A\Desktop\GitHub\msfs-local-bridge-desktop\MockupShell\bin\Release\net10.0-windows\MockupShell.exe`

## First Runtime Check

After building:

1. Start the desktop app or run `.\run-bridge.ps1`
2. Confirm worker mode is selected
3. Confirm `https://ao.home.arpa:39002/` opens
4. Open `https://anobservatory.com/?msfsBridgeUrl=wss%3A%2F%2Fao.home.arpa%3A39002%2Fstream`

Expected bridge status:

- bridge starts in `worker` mode
- WSS uses `ao.home.arpa`
- browser can load the local HTTPS status page

## Certificate Setup

If WSS certificates are missing, run:

```powershell
.\setup-wss-cert-v0.ps1
```

If listener bootstrap needs hosts mapping, run the bootstrap script from an elevated PowerShell window.

## Ask Codex For This

If you want this rebuilt later, a good prompt is:

```text
Rebuild msfs-local-bridge and msfs-local-bridge-desktop outputs, including the native SimConnect worker and release artifacts.
```

If you also want packaging, say:

```text
Generate the bridge zip package and desktop release outputs again.
```
