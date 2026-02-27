# MSFS Local Bridge First-Time Checklist (WSS Bootstrap)

Scope: same-network setup (`MSFS Windows -> Bridge -> anobservatory.com`).

## 1) On Windows host (bridge machine)

1. Open normal PowerShell (not Administrator).
2. Run:

```powershell
.\start-msfs-sync.ps1 -LocalDomain ao.home.arpa -RequireWss
```

3. Keep terminal open while flying.
4. Confirm output shows:
   - secure stream: `wss://<WINDOWS_IP>:39002/stream`
   - listener onboarding page: `http://<WINDOWS_IP>:39000/bootstrap`

## 2) On listener device (Mac/Windows, one-time bootstrap)

1. Open onboarding page from host output:
   `http://<WINDOWS_IP>:39000/bootstrap`
2. Run one-time setup script from that page:
   - Mac:
     `curl -fsSL http://<WINDOWS_IP>:39000/bootstrap/listener/mac.sh | bash`
   - Windows:
     `powershell -ExecutionPolicy Bypass -Command "iwr 'http://<WINDOWS_IP>:39000/bootstrap/listener/windows.ps1' -UseBasicParsing | iex"`
3. Open the `anobservatory.com` connect URL printed by host.
4. Open MSFS Connect panel and click `Start Sync`.

## 3) Expected healthy state

1. Badge: `Connected` (or short `Running` during reconnect).
2. Status: `Streaming telemetry.` during active flight.
3. Ownship moves continuously on map.

## 4) If another device cannot connect

1. Run one elevated repair on host:

```powershell
.\repair-elevated-v0.ps1 -Action OpenFirewall39000 -Port 39000
.\repair-elevated-v0.ps1 -Action OpenFirewall39002 -Port 39002
```

2. Restart bridge:

```powershell
.\start-msfs-sync.ps1 -LocalDomain ao.home.arpa -RequireWss
```
