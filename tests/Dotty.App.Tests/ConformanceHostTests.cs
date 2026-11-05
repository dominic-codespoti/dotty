using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using Dotty.Conformance.Host;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Parser;
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
        Assert.False(options.EnableTitleReports);
    }

    [Fact]
    public void Parse_Overrides_AndKeepsCommandArgumentsVerbatim()
    {
        HostOptions options = HostOptionsParser.Parse(
            ["--rows", "40", "--columns", "100", "--timeout-seconds", "30", "--working-directory", "/tmp",
             "--transcript", "t.log", "--env", "A=1", "--env", "B=x=y", "--checksum-reports", "--window-resize",
             "--title-reports", "--", "prog", "--rows", "--", "x"],
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
        Assert.True(options.EnableTitleReports);
    }



    [Fact]
    public void Parse_CoreSwitchesCanComeFromEnvironment()
    {
        HostOptions options = HostOptionsParser.Parse(
            ["--", "prog"],
            name => name is HostOptionsParser.ChecksumEnvironmentVariable
                or HostOptionsParser.ResizeEnvironmentVariable
                or HostOptionsParser.TitleReportsEnvironmentVariable ? "1" : null);

        Assert.True(options.EnableChecksumReports);
        Assert.True(options.EnableWindowResize);
        Assert.True(options.EnableTitleReports);
    }

    [Fact]
    public void Parse_TitleReportFlagEnablesIndependentOfFalseEnvironmentValue()
    {
        HostOptions options = HostOptionsParser.Parse(
            ["--title-reports", "--", "prog"],
            name => name == HostOptionsParser.TitleReportsEnvironmentVariable ? "0" : null);

        Assert.True(options.EnableTitleReports);
        Assert.False(options.EnableChecksumReports);
        Assert.False(options.EnableWindowResize);
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
    public void Run_WithChecksumReports_AnswersDecrqcraThroughPtyAndParser()
    {
        // 'A' at 1;1, then DECRQCRA id 1 over that single cell: ESC P 1 ! ~ 0041 ESC \ (11 bytes).
        const string script =
            "stty raw -echo; printf '\\033[2J\\033[HA\\033[1;1;1;1;1;1*y'; r=$(dd bs=1 count=11 2>/dev/null); " +
            "case \"$r\" in *'1!~0041'*) exit 0;; *) exit 3;; esac";

        int code = Run(["--timeout-seconds", "20", "--checksum-reports", "--", "/bin/sh", "-c", script]);

        Assert.Equal(0, code);
    }

    [UnixOnlyFact]
    public void Run_WithTitleReports_PreservesLatin1TitleBytesThroughPty()
    {
        const string script =
            "stty raw -echo; printf '\\033[>2;3T\\033]2;Tit\\351le\\007\\033[21t'; " +
            "expected=$(printf '\\033]lTit\\351le\\033\\\\'); r=$(dd bs=1 count=11 2>/dev/null); " +
            "test \"$r\" = \"$expected\"";

        int code = Run(["--timeout-seconds", "20", "--title-reports", "--", "/bin/sh", "-c", script]);

        Assert.Equal(0, code);
    }

    [UnixOnlyFact]
    public void Run_WithoutChecksumReports_DecrqcraIsSilent()
    {
        // The first reply on stdin must be the window-size report: DECRQCRA produced nothing.
        const string script =
            "stty raw -echo; printf '\\033[2J\\033[HA\\033[1;1;1;1;1;1*y\\033[18t'; r=$(dd bs=1 count=10 2>/dev/null); " +
            "case \"$r\" in *'[8;25;80t') exit 0;; *) exit 3;; esac";

        int code = Run(["--timeout-seconds", "20", "--", "/bin/sh", "-c", script]);

        Assert.Equal(0, code);
    }

    [UnixOnlyFact]
    public void Run_WithWindowResize_HonorsCsi8AndReportsTheNewSize()
    {
        // CSI 8;30;100 t then CSI 18 t in one write: ESC [ 8 ; 30 ; 100 t (11 bytes).
        const string script =
            "stty raw -echo; printf '\\033[8;30;100t\\033[18t'; r=$(dd bs=1 count=11 2>/dev/null); " +
            "case \"$r\" in *'[8;30;100t') exit 0;; *) exit 3;; esac";

        int code = Run(["--timeout-seconds", "20", "--window-resize", "--", "/bin/sh", "-c", script]);

        Assert.Equal(0, code);
    }

    [UnixOnlyFact]
    public void Run_WithoutWindowResize_IgnoresCsi8()
    {
        const string script =
            "stty raw -echo; printf '\\033[8;30;100t\\033[18t'; r=$(dd bs=1 count=10 2>/dev/null); " +
            "case \"$r\" in *'[8;25;80t') exit 0;; *) exit 3;; esac";

        int code = Run(["--timeout-seconds", "20", "--", "/bin/sh", "-c", script]);

        Assert.Equal(0, code);
    }

    [Fact]
    public void ApplyCoreSwitches_DefaultsLeaveChecksumOffAndResizeIgnored()
    {
        (TerminalAdapter adapter, BasicAnsiParser parser, List<string> replies) = CoreHarness([]);

        Assert.False(adapter.ChecksumReportEnabled);
        Feed(parser, "\u001b[2J\u001b[HA\u001b[1;1;1;1;1;1*y\u001b[8;30;100t\u001b[18t\u001b[16t");

        Assert.Equal(new[] { "\u001b[8;25;80t", "\u001b[6;16;8t" }, replies);
        Assert.Equal(25, adapter.Buffer.Rows);
        Assert.Equal(80, adapter.Buffer.Columns);
    }

    [Fact]
    public void ApplyCoreSwitches_ChecksumFlagAnswersDecrqcraWithThePlainCodePointSum()
    {
        (TerminalAdapter adapter, BasicAnsiParser parser, List<string> replies) = CoreHarness(["--checksum-reports"]);

        Assert.True(adapter.ChecksumReportEnabled);
        // 'A' (0x41) at 1;1; the second request covers 'A' plus an unwritten cell, which counts as a space (0x20).
        Feed(parser, "\u001b[2J\u001b[HA\u001b[1;1;1;1;1;1*y\u001b[2;1;1;1;1;2*y");

        Assert.Equal(new[] { "\u001bP1!~0041\u001b\\", "\u001bP2!~0061\u001b\\" }, replies);
    }

    [Fact]
    public void ApplyCoreSwitches_ResizeFlagResizesSynchronouslyAndKeepsZeroDimensions()
    {
        (TerminalAdapter adapter, BasicAnsiParser parser, List<string> replies) = CoreHarness(["--window-resize"]);

        Feed(parser, "\u001b[8;30;100t\u001b[18t\u001b[19t");
        Assert.Equal(new[] { "\u001b[8;30;100t", "\u001b[9;30;100t" }, replies);
        Assert.Equal(30, adapter.Buffer.Rows);
        Assert.Equal(100, adapter.Buffer.Columns);

        replies.Clear();
        Feed(parser, "\u001b[8;0;90t\u001b[18t");   // rows 0 = keep
        Feed(parser, "\u001b[40t\u001b[18t");        // CSI Ps t, Ps >= 24: that many lines, columns kept
        Feed(parser, "\u001b[8;50;0t\u001b[18t");   // columns 0 = keep
        Assert.Equal(new[] { "\u001b[8;30;90t", "\u001b[8;40;90t", "\u001b[8;50;90t" }, replies);
    }

    [Fact]
    public void ApplyCoreSwitches_TitleReportsAreDeniedByDefaultAndAllowedIndependently()
    {
        (TerminalAdapter deniedAdapter, BasicAnsiParser deniedParser, List<string> deniedReplies) = CoreHarness([]);
        Assert.False(deniedAdapter.TitleReportEnabled);
        Assert.False(deniedAdapter.ChecksumReportEnabled);
        Feed(deniedParser, "\u001b]2;Window title\u0007\u001b[21t\u001b[18t");
        Assert.Equal(new[] { "\u001b[8;25;80t" }, deniedReplies);

        (TerminalAdapter allowedAdapter, BasicAnsiParser allowedParser, List<string> allowedReplies) =
            CoreHarness(["--title-reports"]);
        Assert.True(allowedAdapter.TitleReportEnabled);
        Assert.False(allowedAdapter.ChecksumReportEnabled);
        Feed(allowedParser, "\u001b]2;Window title\u0007\u001b[21t\u001b[18t");
        Assert.Equal(new[] { "\u001b]lWindow title\u001b\\", "\u001b[8;25;80t" }, allowedReplies);
    }


    [Fact]
    public void ApplyCoreSwitches_PixelReportsUseTheFixedCellSizeAndTrackTheGrid()
    {
        (_, BasicAnsiParser parser, List<string> replies) = CoreHarness(["--window-resize"]);

        Feed(parser, "\u001b[16t\u001b[14t");
        Feed(parser, "\u001b[8;30;100t\u001b[14t");

        // CSI 16 t -> 6;height;width, CSI 14 t -> 4;height;width (grid x 16 x 8 pixels).
        Assert.Equal(
            new[] { "\u001b[6;16;8t", "\u001b[4;400;640t", "\u001b[4;480;800t" },
            replies);
    }

    [Fact]
    public void ApplyCoreSwitches_ResizeCallbackReceivesRowsThenColumns()
    {
        var adapter = new TerminalAdapter(rows: 25, columns: 80);
        var parser = new BasicAnsiParser { Handler = adapter };
        var requests = new List<(int Rows, int Columns)>();
        HostOptions options = HostOptionsParser.Parse(["--window-resize", "--", "x"], _ => null);

        ConformanceHost.ApplyCoreSwitches(adapter, options, (rows, columns) =>
        {
            requests.Add((rows, columns));
            adapter.ResizeBuffer(rows, columns);
        });
        Feed(parser, "\u001b[8;0;120t\u001b[8;30;100t");

        Assert.Equal(new[] { (25, 120), (30, 100) }, requests);
    }

    private static (TerminalAdapter Adapter, BasicAnsiParser Parser, List<string> Replies) CoreHarness(string[] switches)
    {
        HostOptions options = HostOptionsParser.Parse([.. switches, "--", "x"], _ => null);
        var adapter = new TerminalAdapter(rows: 25, columns: 80);
        ConformanceHost.ApplyCoreSwitches(adapter, options);
        var parser = new BasicAnsiParser { Handler = adapter };
        var replies = new List<string>();
        adapter.ReplyRequested += reply => replies.Add(reply.ToString());
        adapter.RawReplyBytesRequested += reply => replies.Add(Encoding.Latin1.GetString(reply.Span));
        return (adapter, parser, replies);
    }

    private static void Feed(BasicAnsiParser parser, string text) => parser.Feed(Encoding.UTF8.GetBytes(text));

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
