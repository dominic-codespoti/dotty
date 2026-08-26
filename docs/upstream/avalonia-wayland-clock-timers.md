# [Wayland] Animation-frame callbacks stall for seconds under paced output; DispatcherTimers starve with them

## Environment
- Avalonia 12.1.0, Avalonia.Wayland native backend, Hyprland, radeonsi

## Reproduction
1. Drive TopLevel.RequestAnimationFrame callbacks from paced output
   (~400/s notifications coalesced to ~9-17/s frames).
2. Callbacks arrive in bursts of ~10, then stall for multi-second gaps.
3. DispatcherTimer (both Render and Background priority) scheduled at 50 ms
   fires once in ~10 s during the same window, on an otherwise idle UI
   thread (main thread in epoll_wait, ~0.3% CPU).

## Notes
- The stalled animation clock + starving timers together freeze any
  render-loop-driven surface (OpenGlControlBase included) under paced
  output. Sustained firehose output keeps callbacks flowing.
- XWayland/X11 backend does not exhibit the timer starvation.

## Impact
Terminal emulators (sparse output), chat apps, dashboards — anything with
bursty-then-quiet update cadences freezes visually between bursts.
