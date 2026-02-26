# MSFS Local Bridge First-Time Checklist (10 min target)

Scope: same-network LAN direct setup (`MSFS Windows -> Bridge -> Mac/mobile panel`).

## 1) On Windows

1. Open normal PowerShell (not Administrator).
2. Run:

```powershell
.\start-msfs-sync.ps1
```

3. If preflight reports `FAIL`, fix and rerun starter.
4. Keep this terminal open while flying.
5. Copy one `LAN URL candidates` value from output.

## 2) On Mac

1. Open local panel with query parameter:

```text
http://localhost:3000/?msfsBridgeUrl=ws://<WINDOWS_IP>:39000/stream
```

2. Open MSFS Connect panel.
3. Click `Start Sync`.

## 3) Expected healthy state

1. Badge: `Connected` (or short `Running` during reconnect).
2. Status: `Streaming telemetry.` during active flight.
3. Ownship moves continuously on the map.

## 4) If another device cannot connect

Run one elevated repair on Windows:

```powershell
.\repair-elevated-v0.ps1 -Action OpenFirewall39000 -Port 39000
```

Then restart bridge:

```powershell
.\start-msfs-sync.ps1
```
