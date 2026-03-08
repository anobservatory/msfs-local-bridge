# Windows Host Onboarding Mockup v2

Location: `lab/windows-host-onboarding-v1`

## Purpose

This mockup shifts from a crowded single-page console to a tabbed host app.

## Layout

Persistent top area:

- app title
- compact host status strip
- primary `Start Bridge` action in the Overview tab

Tabs:

1. `Overview`
2. `Setup`
3. `Runtime`

## UX Direction

- daily use should begin in `Overview`
- first-run work should stay in `Setup`
- logs and support actions should stay in `Runtime`
- only the most important statuses remain persistent across tabs

## Product Assumptions

- installer or first-run flow checks `.NET Desktop Runtime x64`
- installer or first-run flow checks `Microsoft Visual C++ Redistributable x64`
- secure browser connection still requires WSS setup
- runtime log must be easy to copy into Codex or support tools
- `Auto start` is intentionally not part of the product surface