using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dotty.NativePty.Unix;
using FluentAssertions;
using Xunit;

namespace Dotty.NativePty.Tests;

/// <summary>Precedence of <see cref="PtyHelperLocator.Resolve"/> over a fake file system (platform-neutral).</summary>
public sealed class PtyHelperLocatorTests
{
    private const string UsrBin = "/usr/bin";
    private const string RepoBuild = "/repo/src/Dotty/bin/Release/net11.0";

    // Paths are written Unix-style and normalized with GetFullPath, so the table also runs on Windows.
    private static string Full(string path) => Path.GetFullPath(path);

    private static PtyHelperResolution Resolve(
        string baseDirectory, bool isRelease, string[] existing, string? overridePath = null, string? searchPath = null)
    {
        var files = new HashSet<string>(existing.Select(Full), StringComparer.Ordinal);
        var environment = new Dictionary<string, string?>
        {
            [PtyHelperLocator.OverrideVariable] = overridePath,
            ["PATH"] = searchPath,
        };
        return PtyHelperLocator.Resolve(
            name => environment.TryGetValue(name, out string? value) ? value : null,
            baseDirectory,
            isRelease,
            files.Contains);
    }

    private static string JoinPath(params string[] directories) => string.Join(Path.PathSeparator, directories);

    public static TheoryData<string, bool, string[], string?, string?, PtyHelperSource, string?> Precedence() => new()
    {
        // DOTTY_PTY_HELPER wins over everything, in release builds too.
        { UsrBin, true, new[] { "/opt/helper", "/usr/bin/pty-helper" }, "/opt/helper", null, PtyHelperSource.Override, "/opt/helper" },
        // A set but missing override is final: no silent fallback to another helper.
        { UsrBin, true, new[] { "/usr/bin/pty-helper" }, "/missing/helper", null, PtyHelperSource.Override, null },
        // A blank override counts as unset.
        { UsrBin, true, new[] { "/usr/bin/pty-helper" }, "   ", null, PtyHelperSource.BesideExecutable, "/usr/bin/pty-helper" },
        // Install layout: beside, then ../libexec/dotty, then ../lib/dotty.
        { UsrBin, true, new[] { "/usr/bin/pty-helper", "/usr/libexec/dotty/pty-helper" }, null, null, PtyHelperSource.BesideExecutable, "/usr/bin/pty-helper" },
        { UsrBin, true, new[] { "/usr/libexec/dotty/pty-helper", "/usr/lib/dotty/pty-helper" }, null, null, PtyHelperSource.Libexec, "/usr/libexec/dotty/pty-helper" },
        { UsrBin, true, new[] { "/usr/lib/dotty/pty-helper" }, null, null, PtyHelperSource.LibDotty, "/usr/lib/dotty/pty-helper" },
        // Flat /usr/lib/dotty layout with the launcher symlink resolved.
        { "/usr/lib/dotty", true, new[] { "/usr/lib/dotty/pty-helper" }, null, null, PtyHelperSource.BesideExecutable, "/usr/lib/dotty/pty-helper" },
        // Source tree walk: development builds only.
        { RepoBuild, false, new[] { "/repo/src/Dotty.NativePty/bin/pty-helper" }, null, null, PtyHelperSource.DevelopmentTree, "/repo/src/Dotty.NativePty/bin/pty-helper" },
        { "/repo/tests/T/bin", false, new[] { "/repo/tests/Dotty.NativePty/bin/pty-helper" }, null, null, PtyHelperSource.DevelopmentTree, "/repo/tests/Dotty.NativePty/bin/pty-helper" },
        { RepoBuild, true, new[] { "/repo/src/Dotty.NativePty/bin/pty-helper" }, null, null, PtyHelperSource.NotFound, null },
        // The install layout beats the tree walk.
        { RepoBuild, false, new[] { "/repo/src/Dotty.NativePty/bin/pty-helper", RepoBuild + "/pty-helper" }, null, null, PtyHelperSource.BesideExecutable, RepoBuild + "/pty-helper" },
        // PATH: development builds only, after the tree walk, first match wins.
        { UsrBin, false, new[] { "/tools/pty-helper", "/more/pty-helper" }, null, JoinPath("/none", "/tools", "/more"), PtyHelperSource.SearchPath, "/tools/pty-helper" },
        { UsrBin, true, new[] { "/tools/pty-helper" }, null, JoinPath("/tools"), PtyHelperSource.NotFound, null },
        { UsrBin, false, Array.Empty<string>(), null, null, PtyHelperSource.NotFound, null },
    };

    [Theory]
    [MemberData(nameof(Precedence))]
    public void Resolve_FollowsPrecedence(
        string baseDirectory, bool isRelease, string[] existing, string? overridePath, string? searchPath,
        PtyHelperSource expectedSource, string? expectedPath)
    {
        PtyHelperResolution resolution = Resolve(baseDirectory, isRelease, existing, overridePath, searchPath);

        resolution.Source.Should().Be(expectedSource);
        resolution.Path.Should().Be(expectedPath is null ? null : Full(expectedPath));
        resolution.Found.Should().Be(expectedPath is not null);
    }

    [Fact]
    public void Release_ProbesOnlyTheInstallLayoutInOrder()
    {
        PtyHelperResolution resolution = Resolve(UsrBin, isRelease: true, Array.Empty<string>(), searchPath: JoinPath("/tools"));

        resolution.ProbedPaths.Should().Equal(
            Full("/usr/bin/pty-helper"),
            Full("/usr/libexec/dotty/pty-helper"),
            Full("/usr/lib/dotty/pty-helper"));
    }

    [Fact]
    public void Development_ProbesInstallLayoutThenTreeThenPath()
    {
        PtyHelperResolution resolution = Resolve("/a/b", isRelease: false, Array.Empty<string>(), searchPath: JoinPath("/tools"));

        resolution.ProbedPaths.Should().Equal(
            Full("/a/b/pty-helper"),
            Full("/a/libexec/dotty/pty-helper"),
            Full("/a/lib/dotty/pty-helper"),
            Full("/a/b/src/Dotty.NativePty/bin/pty-helper"),
            Full("/a/b/Dotty.NativePty/bin/pty-helper"),
            Full("/a/src/Dotty.NativePty/bin/pty-helper"),
            Full("/a/Dotty.NativePty/bin/pty-helper"),
            Full("/src/Dotty.NativePty/bin/pty-helper"),
            Full("/Dotty.NativePty/bin/pty-helper"),
            Full("/tools/pty-helper"));
    }

    [Fact]
    public void Describe_ListsEveryProbedPathWhenMissing()
    {
        PtyHelperResolution resolution = Resolve(UsrBin, isRelease: true, Array.Empty<string>());

        resolution.Describe().Should().Be(
            "pty-helper: not found; probed: "
            + string.Join(", ", Full("/usr/bin/pty-helper"), Full("/usr/libexec/dotty/pty-helper"), Full("/usr/lib/dotty/pty-helper")));
    }

    [Fact]
    public void Describe_NamesTheOverrideWhenItIsMissing()
    {
        PtyHelperResolution resolution = Resolve(UsrBin, isRelease: true, Array.Empty<string>(), overridePath: "/missing/helper");

        resolution.Describe().Should().Be(
            $"pty-helper: {PtyHelperLocator.OverrideVariable} does not name an existing file; probed: {Full("/missing/helper")}");
    }

    [Fact]
    public void Describe_NamesPathAndSourceWhenFound()
    {
        PtyHelperResolution resolution = Resolve(UsrBin, isRelease: true, new[] { "/usr/lib/dotty/pty-helper" });

        resolution.Describe().Should().Be($"pty-helper: {Full("/usr/lib/dotty/pty-helper")} (LibDotty)");
    }

    [Fact]
    public void ThrowingFileProbe_CountsAsMissing()
    {
        PtyHelperResolution resolution = PtyHelperLocator.Resolve(
            _ => null, UsrBin, isRelease: true, _ => throw new UnauthorizedAccessException());

        resolution.Found.Should().BeFalse();
        resolution.ProbedPaths.Should().HaveCount(3);
    }
}
