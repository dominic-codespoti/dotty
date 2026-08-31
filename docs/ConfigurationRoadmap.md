# Configuration Roadmap

The supported configuration contract is implemented and documented in
[Configuration](Configuration.md). The platform path, watcher, and UI callback
lifecycle are covered by `PlatformPathsTests` and the host lifecycle tests.

Future configuration changes must preserve these invariants:

1. one JSON schema on Linux, macOS, and Windows;
2. no hard-coded home-directory path in runtime code;
3. atomic replacement and debounced reloads;
4. last-valid configuration retained after parse failure;
5. callback execution on the desktop window thread;
6. watcher and callback disposal during shutdown.

Use [Platform Support](PlatformSupport.md) for the current support matrix and
promotion gates. Historical C# source-generator proposals are not a release
requirement for the current host.
