# BridgeAssistant.WinUI (Bootstrap)

This project is a first-pass WinUI 3 implementation based on `lab/bridge-assistant-ui-prototype/index-winui.html`.

## What is implemented now

- Native app shell with mockup-aligned navigation:
  - Dashboard
  - Setup Wizard
  - Preflight Checks
  - Certificate Manager
  - Bridge Runtime
  - Settings
- Shared state integration for diagnostics/certificate/runtime:
  - `BridgeStateStore` (single source of UI state)
  - `BridgeController` (workflow orchestration and guardrails)
  - `IBridgeHostApi` + `LocalBridgeHostApi` (script-backed host operations)
- Script entry points wired:
  - `diagnostics-v0.ps1`
  - `setup-wss-cert-v0.ps1`
  - `start-msfs-sync.ps1`

## UX constraints intentionally carried over

- Firewall/trust-store updates require Administrator PowerShell + UAC approval.
- `mkcert` prerequisite is explicitly surfaced before cert operations.
- Auto-fix is partial by design; re-run diagnostics after repair steps.
- WSS Required mode blocks start until cert/key checks pass.

## Build notes

- Build/run this project on Windows with .NET SDK + Windows App SDK prerequisites.
- In this environment, package restore/build could not be completed because `api.nuget.org` network access is restricted.
