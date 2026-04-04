# GUI Harness Benchmarking

This repository includes a reusable GUI benchmark harness for the release app:

- Script: `artifacts/perf/gui_harness_bench.py`
- App target: `src/Dotty.App/bin/Release/net10.0/Dotty.App`
- Control channel: `DOTTY_TEST_PORT`

## What The Harness Measures

The harness launches a real GUI instance, sends TCP test commands, and captures per-phase metrics for:

- app startup to harness readiness
- command RTT and throughput
- end-to-end canvas capture time after command bursts
- RSS growth during the run
- tab/session lifecycle stats

The app-side harness currently supports:

- `NEW_TAB`
- `NEW_TAB_BG`
- `NEXT_TAB`
- `PREV_TAB`
- `CAPTURE`
- `CAPTURE_CANVAS`
- `STATS`

## Recommended Commands

Build the release app first:

```bash
dotnet build src/Dotty.App/Dotty.App.csproj -c Release
```

Run the standard eager benchmark:

```bash
python3 artifacts/perf/gui_harness_bench.py --runs 2 --new-tabs 20 --switches 200 --port 10300
```

Run the lazy background-tab benchmark:

```bash
python3 artifacts/perf/gui_harness_bench.py --runs 2 --new-tabs 20 --background-new-tabs --switches 200 --port 10260
```

## How To Read The Output

Each run reports:

- `new_tabs`: tab creation phase
- `activation_sweep`: only present for `--background-new-tabs`; activates each background tab once
- `switches`: post-activation tab switching phase
- `stats.initial`: baseline lifecycle counts
- `stats.after_new_tabs`: state after the creation phase
- `stats.after_switches`: state after the switching phase

Lifecycle stats include:

- `totalTabs`
- `sessionsCreated`
- `sessionsStarted`
- `mountedViews`
- `inactiveTimers`
- `snapshots`
- `activeTabIndex`

## Key Findings

### Mounted Views Fixed The Big Switch Backlog

Keeping visited `TerminalView` instances mounted and toggling visibility reduced post-switch canvas capture time from multi-second backlog territory to roughly low-single-second and sub-second ranges in later tuned runs.

### Lazy Background Tabs Defer Session Cost

With eager `NEW_TAB`, creating 20 tabs immediately moved the app to:

- `sessionsCreated: 21`
- `sessionsStarted: 21`
- `mountedViews: 21`

With lazy `NEW_TAB_BG`, creating 20 tabs kept the app at:

- `sessionsCreated: 1`
- `sessionsStarted: 1`
- `mountedViews: 1`

The cost is deferred until `activation_sweep` or user navigation reaches those tabs.

### Eager vs Lazy At Scale

From the side-by-side 20-tab, 200-switch comparison:

```text
Eager:
  startup                  ~804.6 ms
  new-tab capture          ~635.3 ms
  switch capture           ~329.5 ms
  final RSS delta          ~157.7 MB

Lazy:
  startup                  ~704.0 ms
  background capture       ~202.8 ms
  activation sweep capture ~635.2 ms
  switch capture           ~354.7 ms
  final RSS delta          ~186.5 MB
```

Interpretation:

- eager mode pays the session-start cost immediately
- lazy mode makes tab creation much cheaper when the tabs remain unvisited
- once every tab is activated, lazy mode converges toward eager cost because the same sessions eventually start

### Session Count Matters More Than View Count For Idle RSS

Profiling showed the larger idle RSS growth comes primarily from started sessions, not just mounted views. Background tabs help because they avoid creating and starting `TerminalSession` until activation.

## Current Caveats

- `CAPTURE_CANVAS` is the preferred verification path; full-window `CAPTURE` is much heavier.
- Inactive-tab destruction is wired in, but idle RSS still rises enough that session-side memory remains worth watching.
- The benchmark harness is best for comparative trends, not for claiming exact absolute latency numbers across different hosts or compositors.
