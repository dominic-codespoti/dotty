# Agent Guidelines

## NuGet package version

The `Dotty.Abstractions` NuGet package version is defined in
`src/Dotty.App/VersionInfo.cs` (`NuGetPackageVersion` constant).
When bumping the application version, update this constant to match
the published NuGet version. The editor project
(`~/.config/dotty/Dotty.UserConfig.csproj`) reads this version and
auto-regenerates when it detects a mismatch.
