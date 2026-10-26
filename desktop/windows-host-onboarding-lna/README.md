# Windows Host Onboarding LNA

Location: `windows-host-onboarding-lna`

## Intent

This mockup is the Local Network Access direction for the MSFS bridge host
console.

It keeps the calm operational tone from `anobservatory`, but reduces the visual
weight and information duplication from `windows-host-onboarding-v1`.

## What Changed

- no top-level tabs
- one recommended next action at the top
- one compact host checklist
- one compact host status panel
- AO handoff stays secondary until the host is actually ready
- diagnostics stay available, but behind a lower-emphasis panel

## Integration Note

This mockup listens for the same WebView message shape as the current host
console. The desktop shell points `BridgeWorkspace.HostConsoleRoot` at this
folder.

When opened directly in a browser, the mockup exposes three local preview
states:

1. Setup needed
2. Ready to start
3. Running
