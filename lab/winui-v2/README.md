# WinUI Mockup v2

Location: `lab/winui-v2`

## Purpose

This mockup models a practical Windows app flow for `msfs-local-bridge` with minimal operator UI.

## Information Architecture

Tabs are intentionally reduced to release-oriented screens:

1. Overview
2. Setup
3. Runtime

Removed from runtime app scope:

- Settings tab (policy is locked)
- Transition Plan tab (internal porting artifact)
- Separate Diagnostics tab (merged into Setup `Checks & Repairs`)

## Runtime Policy (Locked)

- Secure mode is mandatory (`-RequireWss` is always in start command preview).
- Runtime tuning fields are not operator-editable in this mockup.

## Setup Flow Groups

Setup screen is grouped by operational timing:

1. Base Check: diagnostics and blocking checks
2. Secure Setup: `mkcert` + cert/key generation
3. Listener Trust: bootstrap page/scripts for client trust setup
4. Network Repair (Admin): firewall rules for required ports
5. Start Runtime: only when required blockers are clear

## Elevation Status

Setup includes UAC/elevation state handling:

- `Not required`
- `Elevation required`
- `Elevation completed`
- `Policy blocked`

When status is `Policy blocked`, use `Copy Admin Command` to copy the exact elevated repair command.

In mock mode, policy-block can be simulated for workflow validation.

## Host Integration Contract (`window.bridgeApi`)

If a native shell provides `window.bridgeApi`, this mockup can call real operations.

Supported optional methods:

- `runDiagnostics({ port, wssPort, localDomain, certDir }) -> Promise<{ checks: Array<{ id, status, message, repairAction? }> }>`
- `runSetupCertificate({ localDomain, certDir }) -> Promise<void>`
- `applyRepair({ checkId, command }) -> Promise<void>`
- `startBridge({ settings, command }) -> Promise<{ pid?: number|string }>`
- `stopBridge() -> Promise<void>`

Without `window.bridgeApi`, the UI runs in deterministic mock mode.
