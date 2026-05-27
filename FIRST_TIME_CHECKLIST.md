# MSFS Local Bridge First-Time Checklist

Scope: same-network setup (`MSFS Windows -> Bridge -> anobservatory.com`).

## 1) On Windows host

1. Extract the release zip on the Windows PC that runs MSFS.
2. Open normal PowerShell, not Administrator.
3. Run:

```powershell
.\start.ps1
```

4. Keep the terminal open while flying.
5. Confirm output shows a local stream:
   - `ws://<WINDOWS_IP>:39000/stream`

## 2) Firewall, only if another device cannot connect

Run one elevated repair on the Windows host:

```powershell
.\repair-elevated-v0.ps1 -Action OpenFirewall39000 -Port 39000
```

Then restart the bridge:

```powershell
.\start.ps1
```

## 3) Open anobservatory

Open the connect URL printed by the bridge or desktop app:

```text
https://anobservatory.com/?msfsBridgeUrl=ws%3A%2F%2F<WINDOWS_IP>%3A39000%2Fstream
```

When the browser asks for local network access, allow it.

## 4) Expected healthy state

1. Browser local network access is allowed.
2. MSFS is loaded into an active flight.
3. Ownship moves continuously on the map.
