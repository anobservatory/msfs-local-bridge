# MSFS Local Bridge (Windows)

Windows bridge for streaming MSFS SimConnect telemetry to anobservatory.

- Local stream endpoint: `ws://<WINDOWS_IP>:39000/stream`
- Browser entry point: `https://anobservatory.com/?msfsBridgeUrl=ws%3A%2F%2F<WINDOWS_IP>%3A39000%2Fstream`
- Default setup does not require a custom certificate, local Root CA, listener certificate install, or TCP `39002`.
- Payload shape matches `src/services/msfs/msfsClient.ts`.

If you are flying at KJFK but the app shows a fixed C172 around KSFO, a mock sender is probably running. This bridge is the real SimConnect sender.

## Quick Start

1. Extract the Windows release zip.
2. Open normal PowerShell, not Administrator.
3. Run:

```powershell
.\start.ps1
```

4. Keep the terminal open while flying.
5. Open the printed anobservatory URL.
6. Allow the browser local network access prompt.

First-time checklist:

- `FIRST_TIME_CHECKLIST.md`

## Prerequisites

1. MSFS 2020 or 2024 installed.
2. Visual C++ Redistributable x64 installed.
3. SimConnect DLLs available:
   - `Microsoft.FlightSimulator.SimConnect.dll`
   - `SimConnect.dll`
4. .NET is required only for source/dev workflows or non-self-contained packages.

The desktop app package handles the normal Windows host onboarding flow and can install missing runtime prerequisites.

## SimConnect DLL Placement

For source builds, copy both files into:

```text
lib/
```

Final files should be:

```text
lib/Microsoft.FlightSimulator.SimConnect.dll
lib/SimConnect.dll
```

Release packages should contain both DLLs in the package root and `lib/`.

## Run Bridge

Release zip:

```powershell
cd <extracted-zip-folder>
.\start.ps1
```

Source layout:

```powershell
cd <msfs-local-bridge-repo-root>
.\start.ps1
```

If script execution is blocked once, run:

```powershell
Set-ExecutionPolicy -Scope CurrentUser RemoteSigned
```

Then run `.\start.ps1` again.

## Confirm It Is Live

In `cmd`:

```bat
netstat -ano | findstr ":39000"
```

You should see `LISTENING`.

Browser check on Windows:

```text
http://127.0.0.1:39000/
```

Expected response:

```json
{"name":"msfs-local-bridge","status":"ok","streamPath":"/stream","sampleIntervalMs":200}
```

## Connect anobservatory

Use the URL printed by `start.ps1`:

```text
https://anobservatory.com/?msfsBridgeUrl=ws%3A%2F%2F<WINDOWS_IP>%3A39000%2Fstream
```

Chrome and Edge may show a local network access prompt. Allow it.

For local web development only:

```text
http://localhost:3000/?msfsBridgeUrl=ws://<WINDOWS_IP>:39000/stream
```

## Firewall and Network

Windows and the listener device must be on the same LAN.

Default required inbound port:

- TCP `39000`

Optional elevated repair helper:

```powershell
.\repair-elevated-v0.ps1 -Action ShowFirewall39000
.\repair-elevated-v0.ps1 -Action OpenFirewall39000 -Port 39000
.\repair-elevated-v0.ps1 -Action RemoveFirewall39000 -Port 39000
```

## Diagnostics

Text mode:

```powershell
.\diagnostics-v0.ps1
```

JSON mode:

```powershell
.\diagnostics-v0.ps1 -Format Json
```

## Common Problems

1. `39000` is occupied by `node.exe`: old mock process is running.
2. `LISTENING` exists but the map does not move:
   - wrong `msfsBridgeUrl`
   - browser local network access was denied
   - MSFS is not in an active flight yet
3. Still fixed `MSFS123/C172` path:
   - mock sender is still active somewhere
4. Startup fails with `Could not load ... Microsoft.FlightSimulator.SimConnect.dll`:
   - copy both DLLs to `lib/`
   - install Microsoft Visual C++ 2015-2022 Redistributable x64
   - verify both DLLs also exist in output root
5. `Waiting for MSFS + SimConnect... COMException (0x80004005 / E_FAIL)` repeats during startup:
   - expected while MSFS is still loading or not in an active flight
   - if it continues for more than 2 minutes after cockpit load, investigate SimConnect/DLL setup
6. `Application Control policy has blocked this file (0x800711C7)`:
   - right-click zip -> Properties -> Unblock before extract
   - or run in a folder excluded from strict organization policy

## Runtime Env Vars

Defaults are safe for first run.

- `MSFS_BRIDGE_BIND` default: `0.0.0.0`
- `MSFS_BRIDGE_PORT` default: `39000`
- `MSFS_BRIDGE_PATH` default: `/stream`
- `MSFS_BRIDGE_SAMPLE_MS` default: `200`
- `MSFS_BRIDGE_POLL_MS` default: `25`
- `MSFS_BRIDGE_RECONNECT_MS` default: `2000`
- `MSFS_BRIDGE_RECONNECT_MAX_MS` default: `10000`

Legacy WSS support remains available in source for advanced testing, but it is not part of the default onboarding or package flow.

## Package Build

Build portable release zip:

```powershell
cd <msfs-local-bridge-repo-root>
.\publish-v0.ps1 -Version 0.2.14 -Package self-contained
```

Build lite zip:

```powershell
cd <msfs-local-bridge-repo-root>
.\publish-v0.ps1 -Version 0.2.14 -Package lite
```

Output:

- `dist/msfs-local-bridge-v0.2.14-self-contained.zip`
- `dist/msfs-local-bridge-v0.2.14-lite.zip`

This package excludes source `bin/obj` clutter and includes runtime bridge files needed by testers.
