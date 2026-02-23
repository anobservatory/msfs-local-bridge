# MSFS Local Bridge (Windows)

This folder contains a Windows console bridge:

- `MSFS SimConnect` -> `WebSocket stream`
- Stream endpoint: `ws://<windows-ip>:39000/stream`
- Payload shape matches `src/services/msfs/msfsClient.ts`

If you are flying at KJFK but the app shows a fixed C172 around KSFO, that usually means a mock sender is running.  
This bridge is the real SimConnect sender.

## 0) 6-step quick start (Tester)

1. Extract `msfs-local-bridge-v0.x.x.zip` on Windows.
2. Verify both SimConnect DLL files exist in package root and `lib/`.
3. Open normal PowerShell (not `Run as administrator`).
4. Run `.\preflight-v0.ps1` and fix all `FAIL` items.
5. Run `.\run-bridge.ps1` and keep the terminal open.
6. On Mac, set `VITE_MSFS_BRIDGE_URL` and choose `Display -> MSFS Local`.

## 0.1) Privilege policy (V1 baseline)

1. Default runtime mode is standard user.
2. Administrator mode is not required for normal bridge operation.
3. Elevation is reserved for explicit repair actions only.
4. Use `.\repair-elevated-v0.ps1` for approved elevated repair actions.

## 0.2) One-click diagnostics (V1 baseline)

Run diagnostics in text mode:

```powershell
.\diagnostics-v0.ps1
```

Run diagnostics in JSON mode:

```powershell
.\diagnostics-v0.ps1 -Format Json
```

## 1) Prerequisites (Windows PC)

1. MSFS 2020 or 2024 installed.
2. `.NET 8 SDK` installed (source-build workflow only).
3. SimConnect DLLs available:
   - `Microsoft.FlightSimulator.SimConnect.dll` (managed wrapper)
   - `SimConnect.dll` (native runtime)
4. Visual C++ Redistributable (x64)
   - Microsoft Visual C++ 2015-2022 Redistributable (x64)

## 2) SimConnect DLL placement

For source builds, copy both files into:

`tools/msfs-local-bridge/lib/`

Final files should be:

`tools/msfs-local-bridge/lib/Microsoft.FlightSimulator.SimConnect.dll`
`tools/msfs-local-bridge/lib/SimConnect.dll`

If you do not know where the DLL is, search in PowerShell:

```powershell
Get-ChildItem -Path "C:\" -Filter "Microsoft.FlightSimulator.SimConnect.dll" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 5 FullName
Get-ChildItem -Path "C:\" -Filter "SimConnect.dll" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 10 FullName
```

Release package note:

- `msfs-local-bridge-vX.Y.Z.zip` should already contain both DLLs in root and `lib/`.
- If runtime reports root DLL missing, copy from `lib/` to root once.
- Release zip is `win-x64 self-contained`, so testers do not need .NET runtime/SDK.

## 3) Preflight + run bridge on Windows

Release zip (tester):

```powershell
cd <extracted-zip-folder>
.\preflight-v0.ps1
.\run-bridge.ps1
```

Source layout (developer):

```powershell
cd tools\msfs-local-bridge
.\preflight-v0.ps1
.\run-bridge.ps1
```

If script execution is blocked once, run:

```powershell
Set-ExecutionPolicy -Scope CurrentUser RemoteSigned
```

Then run `.\preflight-v0.ps1` and `.\run-bridge.ps1` again.

## 4) Confirm it is live

In `cmd`:

```bat
netstat -ano | findstr ":39000"
```

You should see `LISTENING`.

Browser check on Windows:

`http://127.0.0.1:39000/`

Should return JSON like:

```json
{"name":"msfs-local-bridge","status":"ok","streamPath":"/stream","sampleIntervalMs":200}
```

## 5) Connect Mac web app

In `anobservatory/.env.local` on your Mac:

```env
VITE_MSFS_BRIDGE_URL=ws://<WINDOWS_IP>:39000/stream
```

Then restart web app dev server:

```bash
npm run dev
```

## 6) Firewalls and network

1. Windows and Mac must be on same LAN.
2. Allow inbound TCP `39000` on Windows (Private network).
3. Keep bridge terminal open while flying.

Optional elevated repair helper:

```powershell
.\repair-elevated-v0.ps1 -Action ShowFirewall39000
.\repair-elevated-v0.ps1 -Action OpenFirewall39000
.\repair-elevated-v0.ps1 -Action RemoveFirewall39000
```

Quick diagnostics helper:

```powershell
.\diagnostics-v0.ps1
```

## 7) Common problems

1. `39000` is occupied by `node.exe`: old mock process is running.
2. `LISTENING` exists but map does not move:
   - wrong `VITE_MSFS_BRIDGE_URL`
   - web app not restarted after `.env.local` change
3. Still fixed `MSFS123/C172` path:
   - mock sender is still active somewhere
4. Startup fails with `Could not load ... Microsoft.FlightSimulator.SimConnect.dll`:
   - copy both DLLs to `lib` (not only managed DLL)
   - run `dotnet clean` then rerun bridge
   - install Microsoft Visual C++ 2015-2022 Redistributable (x64)
   - verify both DLLs also exist in output root (`bin/.../win-x64/`)
5. Bridge starts but no ownship:
   - MSFS not in active flight session yet
   - SimConnect DLL missing/mismatch
6. `Waiting for MSFS + SimConnect... COMException (0x80004005 / E_FAIL)` repeats during startup:
   - expected while MSFS is still loading or not in an active flight
   - if it continues for more than 2 minutes after cockpit load, then investigate
7. `Application Control policy has blocked this file (0x800711C7)`:
   - right-click zip -> Properties -> Unblock before extract
   - or run in a folder excluded from strict organization policy
8. `preflight-v0.ps1` shows `No build output found yet`:
   - this is normal before first `dotnet run` or in release zip layout
9. `preflight-v0.ps1` cannot detect Visual C++ redistributable but bridge runs:
   - treat as non-blocking warning when SimConnect actually connects
10. Runtime says `Could not load ... Microsoft.FlightSimulator.SimConnect.dll` in release zip:
   - confirm both DLLs exist in package root
   - quick fix: copy both DLLs from `lib/` to package root
11. Running bridge as Administrator by default:
   - not required for normal sync
   - use standard user shell unless a specific repair action requests elevation

## 8) Optional runtime env vars

Defaults are safe for first run.

- `MSFS_BRIDGE_BIND` default: `0.0.0.0`
- `MSFS_BRIDGE_PORT` default: `39000`
- `MSFS_BRIDGE_PATH` default: `/stream`
- `MSFS_BRIDGE_SAMPLE_MS` default: `200`
- `MSFS_BRIDGE_POLL_MS` default: `25`
- `MSFS_BRIDGE_RECONNECT_MS` default: `2000`

## 9) V0 Package Build (Operator)

Build portable release zip:

```powershell
cd tools\msfs-local-bridge
.\publish-v0.ps1 -Version 0.1.0
```

`publish-v0.ps1` defaults to `--self-contained true` for `win-x64`.

Output:

`tools/msfs-local-bridge/dist/msfs-local-bridge-v0.1.0.zip`

This package excludes source `bin/obj` clutter and includes runtime bridge files (`MsfsLocalBridge.exe`, `run-bridge.ps1`, `preflight-v0.ps1`, `diagnostics-v0.ps1`, `repair-elevated-v0.ps1`, `README.md`) needed by testers.

## 10) Version Tagging Rule (Release)

Use a semantic git tag and matching package version:

1. Git tag format: `vMAJOR.MINOR.PATCH` (example: `v0.1.0`)
2. Publish argument: `.\publish-v0.ps1 -Version 0.1.0`
3. Output package: `msfs-local-bridge-v0.1.0.zip`

Release command example:

```bash
git tag v0.1.0
git push origin v0.1.0
```
