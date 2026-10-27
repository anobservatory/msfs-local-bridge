# Host diagnostics measurements

The desktop console publishes state every five seconds. Previously each update
started a complete Windows PowerShell diagnostics process, including network
and firewall inspection. Measurements on one Windows development PC found:

| Measurement | Result |
| --- | --- |
| Full diagnostics, mean elapsed time (3 samples after warmup) | 4,891.7 ms |
| Full diagnostics, mean PowerShell CPU time | 4,224.0 ms |
| Sampled peak PowerShell working set | 194.9–195.9 MB |
| Cached service: full queries across 6 background polls | 1 |
| Cached service: first poll elapsed | 5,588.03 ms |
| Cached service: remaining 5 polls elapsed | 0.01–0.19 ms each |

The six service polls were five seconds apart after each read completed. The
cache removes repeated full diagnostics during that interval; it does not make
an individual uncached diagnostics script faster. These figures cover the
diagnostics process/service, not total application CPU or memory. Results vary
with the host, and no real MSFS flight was running for these measurements.

## Freshness and failure behavior

The cache expires 30 seconds after a successful read completes. Each console
update still reads the current bridge process state and prerequisite status.
Changing bridge/sharing/runtime settings invalidates diagnostics. Entering a
console tab, opening Diagnostics, or reactivating the window requests a fresh
report. Network/firewall changes made elsewhere may take up to 30 seconds to
appear while the console stays active.

Enabling sharing bypasses the cache before checking the Private network
profile. A prior successful report cannot authorize sharing on a changed
network. Overlapping background reads share a query. Invalidation during an
older query prevents that result from becoming the new cached state. Failed
or cancelled queries are not cached; the next read retries.

## Reproduce

Use PowerShell 7 in the repository root with the .NET SDK available:

```powershell
./tests/measure-diagnostics.ps1 -Samples 3
dotnet build MsfsLocalBridge.Windows.slnx -c Release -m:1 -warnaserror
dotnet run --project tests/WorkspaceChecks/WorkspaceChecks.csproj -c Release --no-build -- --measure-diagnostics "$PWD"
```

Both measurement commands are read-only and print timing/count data without
the diagnostic report. They do not start the bridge or simulator or modify
network/firewall settings. The normal `WorkspaceChecks` run tests cache expiry,
explicit refresh, overlap, in-flight invalidation, failures, and cancellation
with an injected query and clock, without running real host diagnostics.
