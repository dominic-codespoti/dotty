using System;
using System.Collections.Generic;
using System.Text;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Parser;
using Xunit;

namespace Dotty.Terminal.Tests;

/// <summary>DECRQCRA (<c>CSI Pi ; Pg ; Pt ; Pl ; Pb ; Pr * y</c>): gate, checksum and rectangle semantics.</summary>
public sealed class ChecksumReportTests
{
    private static (TerminalAdapter Adapter, BasicAnsiParser Parser, List<string> Replies) Setup(
        int rows = 4, int columns = 10, bool enabled = true)
    {
        var adapter = new TerminalAdapter(rows: rows, columns: columns) { ChecksumReportEnabled = enabled };
        var parser = new BasicAnsiParser { Handler = adapter };
        var replies = new List<string>();
        adapter.ReplyRequested += reply => replies.Add(reply.ToString());
        return (adapter, parser, replies);
    }

    private static void Feed(BasicAnsiParser parser, string text) => parser.Feed(Encoding.UTF8.GetBytes(text));

    private static string Reply(string id, string hex) => "\u001bP" + id + "!~" + hex + "\u001b\\";

    [Fact]
    public void Gate_DefaultsToOff()
    {
        Assert.False(new TerminalAdapter().ChecksumReportEnabled);
    }

    [Fact]
    public void GateOff_SendsNoReply()
    {
        var (_, parser, replies) = Setup(enabled: false);
        Feed(parser, "ab\u001b[7;1;1;1;1;2*y");
        Assert.Empty(replies);
    }

    [Fact]
    public void GateOn_SumsCodePointsOfRectangle()
    {
        var (_, parser, replies) = Setup();
        Feed(parser, "\u001b[2J\u001b[Hab");
        Feed(parser, "\u001b[7;1;1;1;1;2*y");
        Feed(parser, "\u001b[7;;1;1;1;2*y");
        Assert.Equal(new[] { Reply("7", "00C3"), Reply("7", "00C3") }, replies);
    }

    [Fact]
    public void BlankCellsCountAsSpace()
    {
        var (_, parser, replies) = Setup();
        Feed(parser, "\u001b[9;1;1;1;1;2*y");
        Assert.Equal(new[] { Reply("9", "0040") }, replies);
    }

    [Fact]
    public void OmittedBoundsDefaultToWholeScreen()
    {
        var (_, parser, replies) = Setup(rows: 2, columns: 3);
        Feed(parser, "\u001b[5*y");
        Assert.Equal(new[] { Reply("5", "00C0") }, replies); // 6 blanks * 0x20
    }

    [Fact]
    public void WideCharacterCountsOnce()
    {
        var (_, parser, replies) = Setup();
        Feed(parser, "界");
        Feed(parser, "\u001b[1;1;1;1;1;2*y");
        Assert.Equal(new[] { Reply("1", "754C") }, replies);
    }

    [Fact]
    public void CombiningMarksAreExcluded()
    {
        var (_, parser, replies) = Setup();
        Feed(parser, "e\u0301");
        Feed(parser, "\u001b[1;1;1;1;1;1*y");
        Assert.Equal(new[] { Reply("1", "0065") }, replies);
    }

    [Fact]
    public void SumIsReducedToSixteenBitsAndZeroPadded()
    {
        var (_, parser, replies) = Setup(columns: 4);
        Feed(parser, "\U0001F600"); // U+1F600 wide: 0xF600 after masking
        Feed(parser, "\u001b[1;1;1;1;1;2*y");
        Assert.Equal(new[] { Reply("1", "F600") }, replies);
    }

    [Fact]
    public void RectangleBeyondScreenIsClampedOrZero()
    {
        var (_, parser, replies) = Setup();
        Feed(parser, "ab");
        Feed(parser, "\u001b[1;;1;1;99;2*y");   // bottom clamped: 0xC3 + 3 rows * 2 blanks
        Feed(parser, "\u001b[1;;99;1;100;2*y"); // top beyond the screen
        Feed(parser, "\u001b[1;;1;99;1;100*y"); // left beyond the screen
        Feed(parser, "\u001b[1;;2;1;1;2*y");    // top > bottom
        Feed(parser, "\u001b[1;;1;2;1;1*y");    // left > right
        Assert.Equal(
            new[]
            {
                Reply("1", "0183"), Reply("1", "0000"), Reply("1", "0000"), Reply("1", "0000"), Reply("1", "0000"),
            },
            replies);
    }

    [Fact]
    public void OnlyPageOneExists()
    {
        var (_, parser, replies) = Setup();
        Feed(parser, "ab");
        Feed(parser, "\u001b[1;0;1;1;1;2*y");
        Feed(parser, "\u001b[1;1;1;1;1;2*y");
        Feed(parser, "\u001b[1;2;1;1;1;2*y");
        Assert.Equal(new[] { Reply("1", "00C3"), Reply("1", "00C3"), Reply("1", "0000") }, replies);
    }

    [Fact]
    public void OriginModeMakesRowsRelativeToScrollRegion()
    {
        var (_, parser, replies) = Setup();
        Feed(parser, "\u001b[2;3r\u001b[?6hx");
        Feed(parser, "\u001b[1;;1;1;1;1*y");   // region row 1 = screen row 2 = 'x'
        Feed(parser, "\u001b[1;;;1;;1*y");     // region rows 2..3 of column 1: 'x' + blank
        Feed(parser, "\u001b[1;;3;1;3;1*y");   // beyond the 2-row region
        Feed(parser, "\u001b[?6l");
        Feed(parser, "\u001b[1;;1;1;1;1*y");   // absolute row 1: blank
        Assert.Equal(
            new[] { Reply("1", "0078"), Reply("1", "0098"), Reply("1", "0000"), Reply("1", "0020") },
            replies);
    }

    [Fact]
    public void IdIsEchoedAndSaturatesWithoutWrapping()
    {
        var (_, parser, replies) = Setup();
        Feed(parser, "\u001b[0;0;1;1;1;1*y");
        Feed(parser, "\u001b[4294967295;0;1;1;1;1*y");
        Assert.Equal(new[] { Reply("0", "0020"), Reply("2147483647", "0020") }, replies);
    }

    [Fact]
    public void TinyAdapterDoesNotThrow()
    {
        var (_, parser, replies) = Setup(rows: 1, columns: 1);
        Feed(parser, "\u001b[1*y");
        Feed(parser, "\u001b[1;;1;1;5;5*y");
        Assert.Equal(new[] { Reply("1", "0020"), Reply("1", "0020") }, replies);
    }

    [Fact]
    public void WithoutReplySubscriberIsIgnored()
    {
        var adapter = new TerminalAdapter(rows: 2, columns: 4) { ChecksumReportEnabled = true };
        var parser = new BasicAnsiParser { Handler = adapter };
        Feed(parser, "\u001b[1;;1;1;1;1*y");
    }

    [Fact]
    public void AlternateScreenIsReadWhenActive()
    {
        var (_, parser, replies) = Setup();
        Feed(parser, "a\u001b[?1049hz");
        Feed(parser, "\u001b[1;;1;1;1;10*y"); // row 1: 'z' (wherever it landed) + 9 blanks; the main screen's 'a' is not read
        Assert.Equal(new[] { Reply("1", "019A") }, replies);
    }
}

[Collection("Allocation-sensitive tests")]
public sealed class ChecksumReportAllocationTests
{
    private static readonly TerminalReplyHandler IgnoreReplyHandler = IgnoreReply;
    private static void IgnoreReply(ReadOnlySpan<char> reply) { }

    [Fact]
    public void RepeatedChecksumRequestDoesNotAllocate()
    {
        var adapter = new TerminalAdapter { ChecksumReportEnabled = true };
        adapter.ReplyRequested += IgnoreReplyHandler;
        var parser = new BasicAnsiParser { Handler = adapter };
        parser.Feed(Encoding.UTF8.GetBytes("hello world"));
        byte[] request = Encoding.UTF8.GetBytes("\u001b[7;1;1;1;24;80*y");
        for (int i = 0; i < 16; i++) parser.Feed(request);

        AllocationAssert.NoAllocations(
            () => parser.Feed(request),
            warmupIterations: 0,
            measuredIterationsPerWindow: 128,
            windows: 5);
    }
}
