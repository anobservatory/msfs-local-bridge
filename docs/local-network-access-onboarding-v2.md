# Local Network Access Onboarding V2

Status: proposed
Date: 2026-05-27

## Decision

Deprecate and remove the default WSS Secure Mode onboarding flow.

The default browser connection should use the local network WebSocket endpoint:

```text
ws://<WINDOWS_IP>:39000/stream
```

The AO connect URL should pass that endpoint directly:

```text
https://anobservatory.com/?msfsBridgeUrl=ws%3A%2F%2F<WINDOWS_IP>%3A39000%2Fstream
```

Chrome and Edge 147+ are expected to handle the secure-site-to-local-network case through the browser's Local Network Access permission prompt instead of requiring a locally trusted WSS certificate.

## Product Flow

The default first-run flow becomes:

1. Verify or install required runtimes.
2. Open Windows Firewall inbound TCP 39000 for the private network.
3. Start the MSFS local bridge.
4. Open AO with the generated `ws://<WINDOWS_IP>:39000/stream` connect URL.
5. User allows the browser Local Network Access prompt.
6. AO connects to the bridge and starts receiving MSFS telemetry.

## Still Required

- MSFS 2020 or 2024 on the Windows host.
- SimConnect runtime files.
- VC++ Redistributable when required by SimConnect/native components.
- .NET runtime only for non-self-contained builds.
- Windows Firewall inbound TCP 39000 for listener devices on the same LAN.
- A running bridge before AO attempts the WebSocket connection.
- Browser permission approval for Local Network Access.

## Removed From Default Flow

- Secure Mode as a required onboarding step.
- Local Root CA generation for normal browser use.
- Listener-device Root CA installation.
- Host or listener certificate trust steps.
- Hosts file mapping for `ao.home.arpa`.
- WSS listener endpoint as the normal connection path.
- TCP 39002 as a default firewall requirement.
- Bootstrap scripts whose only job is CA trust or hosts mapping.

## Browser Support Boundary

The default target is Chrome or Edge 147+.

Safari, iOS Safari, older Chromium browsers, Firefox versions without Local Network Access enabled, enterprise-managed browsers, and locked-down networks are not guaranteed by this V2 default.

If a fallback is needed later, it should be designed as a separate compatibility path rather than remaining in the primary first-run flow.

## Implementation Impact

The product and code should move toward:

- Desktop UI language centered on `Local Bridge`, `Firewall`, `Start Bridge`, `Open AO`, and `Allow Browser Access`.
- `start.ps1` no longer requiring WSS by default.
- Diagnostics and preflight no longer treating WSS certificate material or TCP 39002 as default blockers.
- Desktop app state no longer blocking bridge start on Secure Mode.
- `Open Firewall Rules` opening TCP 39000 only by default.
- README and first-time checklist updated after code behavior matches this decision.

## Non-Goals

- This decision does not remove the need for Windows Firewall access.
- This decision does not remove runtime or SimConnect prerequisites.
- This decision does not guarantee Safari or older-browser support.
- This decision does not require preserving WSS as an in-app advanced mode.
