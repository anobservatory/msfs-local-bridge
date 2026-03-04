# Bridge Assistant UI Prototype

Static HTML/CSS/JS concept for a future desktop UX around `msfs-local-bridge`.

## Open

From this folder, open `index.html` in a browser.
For the implementable WinUI-based variant, open `index-winui.html`.

## What this prototype demonstrates

- Setup wizard style onboarding for first-time users
- Preflight status dashboard with pass/warn/fail checks
- Certificate Doctor panel for cert/key/root trust state
- Runtime panel with start/stop controls and live log simulation
- WinUI-style flow where each action maps to existing bridge scripts

## Notes

- This is a UI concept only; no scripts are executed.
- Check states are simulated to visualize expected behavior.
- `index-winui.html` + `app-winui.js` are structured for real integration through a host `window.bridgeApi`.
