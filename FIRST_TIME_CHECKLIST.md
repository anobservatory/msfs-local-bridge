# MSFS Local Bridge First-Time Checklist (WSS-first, 10 min target)

Scope: same-network LAN direct setup (`MSFS Windows -> Bridge -> Mac/mobile panel`).

## 1) On Windows

1. Open normal PowerShell (not Administrator).
2. Run:

```powershell
.\start-msfs-sync.ps1
```

3. If prompted, complete one-time certificate setup (`setup-wss-cert-v0.ps1`).
4. If preflight reports `FAIL`, fix and rerun starter.
5. Keep this terminal open while flying.

## 2) On Mac

1. Ensure domain mapping to bridge host:
   `ao.home.arpa -> <WINDOWS_IP>`
2. Trust local certificate on this device (one-time).
3. Open with query parameter:

```text
https://anobservatory.com/?msfsBridgeUrl=wss://ao.home.arpa:39002/stream
```

4. Open MSFS Connect panel.
5. Click `Start Sync`.

## 3) Expected healthy state

1. Badge: `Connected` (or short `Running` during reconnect).
2. Status: `Streaming telemetry.` during active flight.
3. Ownship moves continuously on the map.

## 4) If another device cannot connect

Run one elevated repair on Windows:

```powershell
.\repair-elevated-v0.ps1 -Action OpenFirewall39000 -Port 39000
.\repair-elevated-v0.ps1 -Action OpenFirewall39002 -Port 39002
```

Then restart bridge:

```powershell
.\start-msfs-sync.ps1
```
