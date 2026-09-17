# Configuration Implementation Summary

This file is retained as a migration pointer for older Dotty configuration
notes. The current implementation is:

- `src/Dotty.Runtime/Config/PlatformPaths.cs` — platform-specific directories
  and `DOTTY_CONFIG_HOME` override;
- `src/Dotty.Runtime/Config/DottyUserConfig.cs` — JSON model, atomic defaults,
  debounced watcher, and callback dispatch;
- `src/Dotty/Host/DottyWindowHost.cs` — queues accepted changes on the desktop
  window thread and disposes the watcher during shutdown.

Use [Configuration](Configuration.md) for the supported schema and
Use [Platform Support](PlatformSupport.md) for OS setup and diagnostics. The
current host accepts JSON configuration only; older UI configuration bridges
are not actionable.
