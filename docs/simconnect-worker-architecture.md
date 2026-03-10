# SimConnect Worker Architecture

## Why this split exists

The managed SimConnect wrapper in this project targets `.NETFramework,Version=v4.6.1`.
That makes it a risky dependency to keep inside the main bridge once the bridge itself moves with the latest .NET runtime.

The goal is to keep these constraints true at the same time:
- The main bridge stays on the latest supported .NET runtime.
- End users do not need multiple .NET runtimes installed.
- We avoid large self-contained bundles just to carry an older runtime.
- SimConnect remains isolated behind a stable protocol boundary.

## Target runtime model

- `msfs-local-bridge` (ASP.NET/WebSocket host): latest .NET
- `msfs-simconnect-worker.exe` (native Windows process): no .NET dependency
- IPC between them: line-delimited JSON over stdio

That gives the product a single modern .NET dependency while letting the SimConnect-specific process evolve separately.

## Protocol sketch

Worker output is newline-delimited JSON.

Status example:
```json
{"type":"status","state":"ready","message":"Connected to SimConnect."}
```

Telemetry example:
```json
{
  "type": "telemetry",
  "snapshot": {
    "id": "local:windows-flight-device",
    "callsign": "AAL123",
    "tailNumber": "N123AB",
    "aircraftTitle": "Cessna Skyhawk",
    "squawk": "1200",
    "simVersionLabel": "MSFS 2024",
    "lat": 37.6189,
    "lon": -122.375,
    "altBaroFt": 3500.0,
    "altGeomFt": 3521.4,
    "gsKt": 112.7,
    "headingDegTrue": 182.1,
    "trackDegTrue": 181.8,
    "vsFpm": -320.0,
    "onGround": false,
    "timestampMs": 1773192000000
  }
}
```

Error example:
```json
{"type":"error","message":"SimConnect connection failed."}
```

## Current repository changes

- `Program.cs` can now run either embedded SimConnect or worker-backed SimConnect.
- `SimConnectWorkerOwnshipService.cs` starts the worker and consumes protocol messages.
- `workers/simconnect-native/` contains the native worker skeleton project.

## Environment variables

- `MSFS_BRIDGE_SIMCONNECT_MODE=embedded|worker`
- `MSFS_BRIDGE_SIMCONNECT_WORKER_PATH=...`
- `MSFS_BRIDGE_SIMCONNECT_WORKER_ARGS=...`

Default behavior remains `embedded` so current users are not broken.

## Next milestone

The next real implementation step is to replace the worker placeholder loop with native SimConnect polling and emit real `telemetry` messages.
