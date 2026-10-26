# Windows Host Onboarding LNA

Location: `windows-host-onboarding-lna`

## Intent

This folder is the production WebView2 Host Console for the Windows app.

It keeps the calm operational tone from `anobservatory`, but reduces the visual
weight and information duplication from `windows-host-onboarding-v1`.

## What Changed

- Setup and Access tabs, with one recommended action
- one recommended next action at the top
- one compact host checklist
- one compact host status panel
- Open AO uses the ordinary site address after local bridge startup
- Other devices is optional and exposes access controls, QR, and copy link
- active host actions disable controls and surface notifications
- diagnostics stay available, but behind a lower-emphasis panel

## Integration Note

The console listens for WebView host state, action state, and notifications.
The desktop shell points `BridgeWorkspace.HostConsoleRoot` at this
folder.

When opened directly in a browser, the console exposes three local preview
states:

1. Setup needed
2. Ready to start
3. Running
