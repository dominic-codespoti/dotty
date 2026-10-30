using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dotty.Runtime.Sessions;
using Xunit;

namespace Dotty.App.Tests;

/// <summary>The <c>term</c> setting: <see cref="TerminalIdentity.ResolveTerm"/>, the ncurses search order and <c>TERMINFO_DIRS</c>.</summary>
public sealed class TermResolutionTests
{
    private const string Bundled = "/opt/dotty/terminfo";
    private const string Home = "/home/u";
    private static readonly char Sep = Path.PathSeparator;

    // Fake file system: paths are compared with '/' so the table also runs on Windows.
    private static Func<string, bool> Files(params string[] paths)
    {
        var set = new HashSet<string>(paths, StringComparer.Ordinal);
        return p => set.Contains(p.Replace('\\', '/'));
    }

    private static Func<string, string?> Env(string? terminfo = null, string? terminfoDirs = null) => name => name switch
    {
        "TERMINFO" => terminfo,
        "TERMINFO_DIRS" => terminfoDirs,
        _ => null,
    };

    private static string Dirs(params string[] entries) => string.Join(Sep, entries);

    [Theory]
    [InlineData(null, "xterm-256color")]
    [InlineData("", "xterm-256color")]
    [InlineData("   ", "xterm-256color")]
    [InlineData("xterm-dotty", "xterm-dotty")]
    [InlineData(" xterm-dotty ", "xterm-dotty")]
    [InlineData("xterm-missing", "xterm-256color")]
    [InlineData("../../etc/passwd", "xterm-256color")]
    [InlineData("x/xterm-dotty", "xterm-256color")]
    [InlineData("x\\xterm-dotty", "xterm-256color")]
    [InlineData(".hidden", "xterm-256color")]
    [InlineData("xterm dotty", "xterm-256color")]
    [InlineData("xterm-d\u00f6tty", "xterm-256color")]
    public void ResolveTerm_UsesTheEntryOnlyWhenItExists(string? configured, string expected)
    {
        // The lookup claims every valid-looking name except xterm-missing exists, so only validation and the
        // lookup decide.
        Assert.Equal(expected, TerminalIdentity.ResolveTerm(configured, name => name != "xterm-missing"));
    }

    [Fact]
    public void ResolveTerm_NeverLooksUpTheDefault() =>
        Assert.Equal("xterm-256color", TerminalIdentity.ResolveTerm("xterm-256color", _ => throw new InvalidOperationException()));

    public static TheoryData<string, string?, string?, string[], string[], string, string?> Selections() => new()
    {
        // configured, $TERMINFO, $TERMINFO_DIRS, existing files, existing system dirs, expected TERM, expected TERMINFO_DIRS
        { "xterm-256color", null, null, Array.Empty<string>(), Array.Empty<string>(), "xterm-256color", null },
        // Installed system-wide: used as is, environment untouched.
        { "xterm-dotty", null, null, new[] { "/usr/share/terminfo/x/xterm-dotty" }, new[] { "/usr/share/terminfo" }, "xterm-dotty", null },
        // macOS hexadecimal layout.
        { "xterm-dotty", null, null, new[] { "/usr/share/terminfo/78/xterm-dotty" }, new[] { "/usr/share/terminfo" }, "xterm-dotty", null },
        // $TERMINFO and ~/.terminfo come first.
        { "xterm-dotty", "/t", null, new[] { "/t/x/xterm-dotty" }, Array.Empty<string>(), "xterm-dotty", null },
        { "xterm-dotty", null, null, new[] { Home + "/.terminfo/x/xterm-dotty" }, Array.Empty<string>(), "xterm-dotty", null },
        // Only in the shipped directory: TERMINFO_DIRS gains it; unset becomes default entry + system dirs + bundled.
        { "xterm-dotty", null, null, new[] { Bundled + "/x/xterm-dotty" }, new[] { "/etc/terminfo", "/usr/share/terminfo" },
            "xterm-dotty", Dirs("", "/etc/terminfo", "/usr/share/terminfo", Bundled) },
        { "xterm-dotty", null, "/nix/terminfo", new[] { Bundled + "/78/xterm-dotty" }, Array.Empty<string>(),
            "xterm-dotty", Dirs("/nix/terminfo", Bundled) },
        // A set TERMINFO_DIRS replaces the system directories unless it has an empty entry.
        { "xterm-dotty", null, "/a", new[] { "/usr/share/terminfo/x/xterm-dotty" }, new[] { "/usr/share/terminfo" }, "xterm-256color", null },
        { "xterm-dotty", null, Dirs("", "/a"), new[] { "/usr/share/terminfo/x/xterm-dotty" }, new[] { "/usr/share/terminfo" }, "xterm-dotty", null },
        // A system directory that does not exist is not searched.
        { "xterm-dotty", null, null, new[] { "/usr/share/terminfo/x/xterm-dotty" }, Array.Empty<string>(), "xterm-256color", null },
        // Missing everywhere.
        { "xterm-dotty", null, null, Array.Empty<string>(), new[] { "/usr/share/terminfo" }, "xterm-256color", null },
    };

    [Theory]
    [MemberData(nameof(Selections))]
    public void Select_FollowsTheNcursesSearchOrderAndAddsTheBundledDirectory(
        string configured, string? terminfo, string? terminfoDirs, string[] files, string[] systemDirectories,
        string expectedTerm, string? expectedTerminfoDirs)
    {
        TermSelection selection = TerminalIdentity.Select(
            configured, Env(terminfo, terminfoDirs), Home, Bundled, Files(files), Files(systemDirectories));

        Assert.Equal(new TermSelection(expectedTerm, expectedTerminfoDirs), selection);
    }

    [Fact]
    public void Select_WithoutBundledDirectoryFallsBack() =>
        Assert.Equal(
            new TermSelection("xterm-256color", null),
            TerminalIdentity.Select("xterm-dotty", Env(), Home, null, Files(Bundled + "/x/xterm-dotty"), Files()));

    [Fact]
    public void SearchDirectories_FollowNcursesOrder()
    {
        List<string> directories = TerminalIdentity.SearchDirectories(
            Env("/t", Dirs("/a", "", "/b", "")), Home, Files("/lib/terminfo", "/usr/share/terminfo"));

        Assert.Equal(
            new[] { "/t", Home + "/.terminfo", "/a", "/lib/terminfo", "/usr/share/terminfo", "/b" },
            directories.Select(d => d.Replace('\\', '/')));
    }

    [Theory]
    [InlineData(null, "|/usr/share/terminfo|" + Bundled)]
    [InlineData("", "|/usr/share/terminfo|" + Bundled)]
    [InlineData("/a", "/a|" + Bundled)]
    [InlineData("/a|", "/a||" + Bundled)]
    [InlineData("/a|" + Bundled, "/a|" + Bundled)]
    public void AppendTerminfoDirs_AppendsOnceAndKeepsTheSystemDefault(string? existing, string expected)
    {
        string? input = existing?.Replace('|', Sep);
        Assert.Equal(
            expected.Replace('|', Sep),
            TerminalIdentity.AppendTerminfoDirs(input, Bundled, TerminalIdentity.SystemDirectories, Files("/usr/share/terminfo")));
    }
}
