# BridgeAssistant.WinUI (Bootstrap)

This project is a first-pass WinUI 3 bootstrap for turning the `index-winui.html` mockup into a real Windows app.

## Scope of this bootstrap

- App shell and page structure aligned with mockup navigation:
  - Dashboard
  - Setup Wizard
  - Preflight Checks
  - Certificate Manager
  - Bridge Runtime
  - Settings
- A bridge host API contract (`IBridgeHostApi`) and local script-backed implementation (`LocalBridgeHostApi`)
- Script integration entry points for:
  - `diagnostics-v0.ps1`
  - `setup-wss-cert-v0.ps1`
  - `start-msfs-sync.ps1`

## Important constraints carried into UX

- Firewall/trust-store changes require Administrator PowerShell + UAC approval.
- `mkcert` missing state must be surfaced and guided before certificate generation.
- Auto-fix scope is partial; re-run diagnostics after each repair action.

## Build notes

- Build/run this project on Windows with .NET SDK + Windows App SDK prerequisites installed.
- This repository was scaffolded from a non-Windows environment, so local compile was not executed here.
