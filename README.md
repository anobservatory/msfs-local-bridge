# MSFS Local Bridge (Windows)

This folder contains a Windows console bridge:

- `MSFS SimConnect` -> `WebSocket stream`
- Stream endpoint: `ws://<windows-ip>:39000/stream`
- Payload shape matches `src/services/msfs/msfsClient.ts`

If you are flying at KJFK but the app shows a fixed C172 around KSFO, that usually means a mock sender is running.  
This bridge is the real SimConnect sender.

## 1) Prerequisites (Windows PC)

1. MSFS 2020 or 2024 installed.
2. `.NET 8 SDK` installed.
3. SimConnect DLLs available:
   - `Microsoft.FlightSimulator.SimConnect.dll` (managed wrapper)
   - `SimConnect.dll` (native runtime)
4. Visual C++ Redistributable (x64)
   - Microsoft Visual C++ 2015-2022 Redistributable (x64)

## 2) Put SimConnect DLLs in this project

Copy both files into:

`tools/msfs-local-bridge/lib/`

Final files should be:

`tools/msfs-local-bridge/lib/Microsoft.FlightSimulator.SimConnect.dll`
`tools/msfs-local-bridge/lib/SimConnect.dll`

If you do not know where the DLL is, search in PowerShell:

```powershell
Get-ChildItem -Path "C:\" -Filter "Microsoft.FlightSimulator.SimConnect.dll" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 5 FullName
Get-ChildItem -Path "C:\" -Filter "SimConnect.dll" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 10 FullName
```

## 3) Run bridge on Windows

From repo root:

```powershell
cd tools\msfs-local-bridge
.\run-bridge.ps1
```

If script execution is blocked once, run:

```powershell
Set-ExecutionPolicy -Scope CurrentUser RemoteSigned
```

Then run `.\run-bridge.ps1` again.

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

## 8) Optional runtime env vars

Defaults are safe for first run.

- `MSFS_BRIDGE_BIND` default: `0.0.0.0`
- `MSFS_BRIDGE_PORT` default: `39000`
- `MSFS_BRIDGE_PATH` default: `/stream`
- `MSFS_BRIDGE_SAMPLE_MS` default: `200`
- `MSFS_BRIDGE_POLL_MS` default: `25`
- `MSFS_BRIDGE_RECONNECT_MS` default: `2000`
