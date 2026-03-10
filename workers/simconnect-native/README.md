# SimConnect Native Worker

This folder holds the long-term replacement for the in-process managed SimConnect bridge.

Target shape:
- `msfs-local-bridge` stays on the latest .NET runtime.
- SimConnect runs in a small native Windows worker.
- The .NET bridge talks to the worker over line-delimited JSON on stdio.

Current status:
- The process boundary and protocol contract are scaffolded.
- `src/main.cpp` now contains a direct SimConnect DLL loader and ownship telemetry pump.
- The worker looks for `SimConnect.dll` beside itself, in `lib/`, or three levels up in the bridge root.
- The remaining unknown is build/runtime validation on a machine with C++ build tools and active MSFS.

Build:
```powershell
cmake -S . -B build -G "Visual Studio 17 2022" -A x64
cmake --build build --config Release
```

Expected output path:
- `workers/simconnect-native/dist/msfs-simconnect-worker.exe`

Manual bridge test:
```powershell
.\run-bridge.ps1 -SimConnectMode worker
```

Optional explicit worker path:
```powershell
.\run-bridge.ps1 -SimConnectMode worker -SimConnectWorkerPath ".\workers\simconnect-native\dist\msfs-simconnect-worker.exe"
```

Next implementation steps:
1. Build the native worker with local MSVC/CMake tools.
2. Run the bridge in worker mode while MSFS is open.
3. Confirm `telemetry` NDJSON appears and the browser reconnects.
4. Once verified, decide whether `embedded` should stay as fallback or be retired.
