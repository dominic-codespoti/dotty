# Configuration Source Generator (Historical)

The source-generator configuration design described in earlier revisions is
not the runtime configuration contract of the current desktop host. Dotty now
loads `config.json` through `Dotty.Runtime.Config.UserConfigService` and applies
changes through its atomic file watcher.

For the supported configuration schema, platform paths, hot-reload lifecycle,
and troubleshooting, use [Configuration](Configuration.md). For OS support and
release setup, use [Platform Support](PlatformSupport.md).

The `Dotty.Config.SourceGenerator` project remains in the repository for
platform-neutral generated defaults and compatibility with existing consumers;
it does not make the desktop host expect a generated `Config.cs`, an Avalonia
bridge, or a runtime-built user project.
