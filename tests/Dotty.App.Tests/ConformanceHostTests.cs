using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using Dotty.Conformance.Host;
using Xunit;

namespace Dotty.App.Tests;

public sealed class ConformanceHostTests
{
    private static readonly string[] SleepCommand = ["--", "sleep", "30"];

    [Fact]
    public void Parse_Defaults()
    {
        HostOptions options = HostOptionsParser.Parse(["--", "python3", "esctest.py"], _ => null);

        Assert.Equal(25, options.Rows);
        Assert.Equal(80, options.Columns);
        Assert.Equal(TimeSpan.FromMinutes(15), options.Timeout);
        Assert.Null(options.WorkingDirectory);
        Assert.Null(options.TranscriptPath);
        Assert.Empty(options.Environment);
        Assert.Equal(new[] { "python3", "esctest.py" }, options.Command);
        Assert.False(options.EnableChecksumReports);
        Assert.False(options.EnableWindowResize);
    }

    [Fact]
    public void Parse_Overrides_AndKeepsCommandArgumentsVerbatim()
    {
        HostOptions options = HostOptionsParser.Parse(
            ["--rows", "40", "--columns", "100", "--timeout-seconds", "30", "--working-directory", "/tmp",
             "--transcript", "t.log", "--env", "A=1", "--env", "B=x=y", "--checksum-reports", "--window-resize",
             "--", "prog", "--rows", "--", "x"],
            _ => null);

        Assert.Equal(40, options.Rows);
        Assert.Equal(100, options.Columns);
        Assert.Equal(TimeSpan.FromSeconds(30), options.Timeout);
        Assert.Equal("/tmp", options.WorkingDirectory);
        Assert.Equal("t.log", options.TranscriptPath);
        Assert.Equal("1", options.Environment["A"]);
        Assert.Equal("x=y", options.Environment["B"]);
        Assert.Equal(new[] { "prog", "--rows", "--", "x" }, options.Command);
        Assert.True(options.EnableChecksumReports);
        Assert.True(options.EnableWindowResize);
    }

    [Fact]
    public void Parse_CoreSwitchesCanComeFromEnvironment()
    {
        HostOptions options = HostOptionsParser.Parse(
            ["--", "prog"],
            name => name is HostOptionsParser.ChecksumEnvironmentVariable or HostOptionsParser.ResizeEnvironmentVariable ? "1" : null);

        Assert.True(options.EnableChecksumReports);
        Assert.True(options.EnableWindowResize);
    }

    public static TheoryData<string[]> InvalidArguments => new()
    {
        new[] { "prog" },
        new[] { "--" },
        new[] { "--rows", "0", "--", "x" },
        new[] { "--rows", "-3", "--", "x" },
        new[] { "--rows", "abc", "--", "x" },
        new[] { "--columns", "--", "x" },
        new[] { "--timeout-seconds", "0", "--", "x" },
        new[] { "--timeout-seconds", "999999", "--", "x" },
        new[] { "--env", "BAD", "--", "x" },
        new[] { "--env", "=v", "--", "x" },
        new[] { "--bogus", "--", "x" },
        new[] { "--rows", "25" },
    };

    [Theory]
    [MemberData(nameof(InvalidArguments))]
    public void Parse_RejectsInvalidArguments(string[] args)
    {
        Assert.Throws<ArgumentException>(() => HostOptionsParser.Parse(args, _ => null));
    }

    [UnixOnlyFact]
    public void Run_PropagatesChildExitCode()
    {
        int code = Run(["--", "/bin/sh", "-c", "exit 7"]);
        Assert.Equal(7, code);
    }

    [UnixOnlyFact]
    public void Run_AnswersWindowSizeQueryThroughPtyAndParser()
    {
        // CSI 18 t must be answered with ESC [ 8 ; 25 ; 80 t (10 bytes) on the child's stdin.
        const string script =
            "stty raw -echo; printf '\\033[18t'; r=$(dd bs=1 count=10 2>/dev/null); " +
            "case \"$r\" in *'[8;25;80t') exit 0;; *) exit 3;; esac";

        int code = Run(["--timeout-seconds", "20", "--", "/bin/sh", "-c", script]);

        Assert.Equal(0, code);
    }

    [UnixOnlyFact]
    public void Run_TimeoutKillsChildAndReturns124()
    {
        var clock = Stopwatch.StartNew();
        int code = Run(["--timeout-seconds", "1", .. SleepCommand]);

        Assert.Equal(ConformanceHost.ExitTimeout, code);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"Timeout took {clock.Elapsed}.");
    }

    private static int Run(string[] args)
    {
        HostOptions options = HostOptionsParser.Parse(args, _ => null);
        using var diagnostics = new StringWriter();
        return ConformanceHost.Run(options, diagnostics, CancellationToken.None);
    }

    private sealed class UnixOnlyFactAttribute : FactAttribute
    {
        public UnixOnlyFactAttribute(
            [CallerFilePath] string? sourceFilePath = null,
            [CallerLineNumber] int sourceLineNumber = 0)
            : base(sourceFilePath, sourceLineNumber)
        {
            if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
                Skip = "Requires a Unix PTY.";
        }
    }
}
