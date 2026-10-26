# Local Network Access Onboarding

Status: implemented
Last updated: 2026-10-26

## Same-PC Default

1. Open the Windows desktop app and install missing prerequisites if needed.
2. Start Bridge. Its default listener binds only to `127.0.0.1:39000`.
3. Visit https://anobservatory.com/ and Start Sync in MSFS Connect.
4. Allow the browser's local network permission if requested.

Open AO launches the ordinary site URL. An inbound LAN firewall rule, LAN
address, certificate, or first-run setup URL is not required for this path.
The Windows Host Console retains Setup, Access, lifecycle controls, and logs.

## Optional Other Devices

In Access → Other devices, Enable Access sets up the managed inbound TCP 39000
rule for the Private profile and local subnet. If necessary, Windows requests
administrator permission; cancellation leaves sharing disabled. An existing
ready rule does not require another elevation. The setting is saved per user.

Enabling or disabling access restarts a running bridge to change its binding.
Enabled access binds to `0.0.0.0`; disabled access binds to `127.0.0.1`.
Disabling stops remote access without deleting a previously created rule.
QR and connection-link controls appear only while sharing is ready and running.
QR generation uses the bundled MIT-licensed qrcode-generator library, with no
external QR service or CDN request.

The receiving device must be on the same private network. Scan the QR or open
the copied AO link once. AO stores its bridge URL in browser local storage,
then removes only the setup parameter from the address bar. Later visits use
the ordinary AO address. Browser storage is specific to that browser/profile;
clearing it requires registration again. If storage is blocked, the setup URL
is retained so it remains reusable.

Sharing readiness checks the network profile of the actual LAN interface,
not only whether a Private firewall rule exists. Public or unverifiable
profiles do not show a ready QR. The app explains how to select Private for
a trusted home network; it never changes network trust automatically. Local
sync remains available. Other devices has its own sharing status, separate
from same-PC AO readiness.

If DHCP changes the flight PC address, register a fresh link. AO Connection
also accepts a pasted link and offers Use local bridge for recovery. There is
no automatic discovery or cloud telemetry relay. Concurrent browsers receive
the same telemetry through separate WebSocket connections.

## Browser Permission and Compatibility

AO initiates the connection; the browser owns its network permission prompt.
Bridge owns Windows firewall setup. Neither application can grant the browser
permission silently. A refused connection has recovery guidance and an initial
connection timeout; the user can update site permissions and retry sync.

On Android, the browser app may also need Nearby devices / Local network
permission in phone Settings → Apps → the browser → Permissions. This is
separate from the AO site's browser permission. If the host's HTTP health
address will not open directly on the phone, check this app permission as
well as the LAN and firewall settings before changing the web deployment.

Chrome/Edge with WebSocket local-network permission support are the target.
[Chrome 147 release notes](https://developer.chrome.com/release-notes/147)
document the expansion of local-network restrictions to WebSockets.
Safari/iOS, older browsers, managed policies, Public-profile or isolated guest
networks are not guaranteed by this transport. A QR does not bypass these limits.

## Scope

Telemetry payload version 1 is unchanged. WSS, local CA installation, hosts
mapping, and TCP 39002 remain legacy compatibility paths, outside default
onboarding. Host Console remains extensible for later control features; this
change adds no simulator control commands or authorization model for them.
