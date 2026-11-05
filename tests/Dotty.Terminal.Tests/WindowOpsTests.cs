using System;
using System.Collections.Generic;
using System.Text;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Parser;
using Xunit;

namespace Dotty.Terminal.Tests;

/// <summary>CSI Ps ; Ps ; Ps t: window reports, opt-in title reports and resize requests.</summary>
public sealed class WindowOpsTests
{
    private static (TerminalAdapter Adapter, BasicAnsiParser Parser, List<string> Replies, List<(int Rows, int Cols)> Resizes) Setup()
    {
        var adapter = new TerminalAdapter(rows: 2, columns: 8);
        var parser = new BasicAnsiParser { Handler = adapter };
        var replies = new List<string>();
        var resizes = new List<(int, int)>();
        adapter.ReplyRequested += reply => replies.Add(reply.ToString());
        adapter.WindowResizeRequested += (rows, cols) => resizes.Add((rows, cols));
        return (adapter, parser, replies, resizes);
    }

    private static void Feed(BasicAnsiParser parser, string text) => parser.Feed(Encoding.UTF8.GetBytes(text));

    [Fact]
    public void CharacterSizeReports_UseTheGrid()
    {
        var (_, parser, replies, _) = Setup();
        Feed(parser, "\u001b[18t\u001b[19t");
        Assert.Equal(new[] { "\u001b[8;2;8t", "\u001b[9;2;8t" }, replies);
    }

    [Fact]
    public void CharacterSizeReports_FollowAResize()
    {
        var (adapter, parser, replies, _) = Setup();
        adapter.ResizeBuffer(5, 20);
        Feed(parser, "\u001b[18t\u001b[19t");
        Assert.Equal(new[] { "\u001b[8;5;20t", "\u001b[9;5;20t" }, replies);
    }

    [Fact]
    public void DefaultPixelReports_AreDerivedFromGridAndDefaultCell()
    {
        var (_, parser, replies, _) = Setup();
        Feed(parser, "\u001b[16t\u001b[14t\u001b[14;2t\u001b[15t");
        // Default cell 8x16 → text area 8*8 x 2*16; the screen falls back to the text area.
        Assert.Equal(
            new[] { "\u001b[6;16;8t", "\u001b[4;32;64t", "\u001b[4;32;64t", "\u001b[5;32;64t" },
            replies);
    }

    [Fact]
    public void CellPixelSize_DrivesCellAndTextAreaReports()
    {
        var (adapter, parser, replies, _) = Setup();
        adapter.SetCellPixelSize(9, 18);
        Feed(parser, "\u001b[16t\u001b[14t");
        Assert.Equal(new[] { "\u001b[6;18;9t", "\u001b[4;36;72t" }, replies);
    }

    [Fact]
    public void WindowPixelSizeOverride_WinsForTextAreaReport()
    {
        var (adapter, parser, replies, _) = Setup();
        adapter.SetWindowPixelSize(1000, 700);
        Feed(parser, "\u001b[14t");
        Assert.Equal(new[] { "\u001b[4;700;1000t" }, replies);
    }

    [Fact]
    public void ScreenPixelSize_IsReported()
    {
        var (adapter, parser, replies, _) = Setup();
        adapter.SetScreenPixelSize(1920, 1080);
        Feed(parser, "\u001b[15t");
        Assert.Equal(new[] { "\u001b[5;1080;1920t" }, replies);
    }

    [Fact]
    public void WindowPosition_DefaultsToOriginAndFollowsTheHost()
    {
        var (adapter, parser, replies, _) = Setup();
        Feed(parser, "\u001b[13t");
        adapter.SetWindowPosition(40, 60);
        Feed(parser, "\u001b[13t");
        adapter.SetWindowPosition(-8, 0);
        Feed(parser, "\u001b[13;2t");
        Assert.Equal(new[] { "\u001b[3;0;0t", "\u001b[3;40;60t", "\u001b[3;-8;0t" }, replies);
    }


    [Fact]
    public void NonPositiveHostSizes_AreClamped()
    {
        var (adapter, parser, replies, _) = Setup();
        adapter.SetCellPixelSize(0, -5);
        Feed(parser, "\u001b[16t");
        Assert.Equal(new[] { "\u001b[6;1;1t" }, replies);
    }

    [Theory]
    [InlineData("\u001b[20t")]
    [InlineData("\u001b[21t")]
    [InlineData("\u001b[22;0t")]
    [InlineData("\u001b[23;0t")]
    [InlineData("\u001b[1t")]
    [InlineData("\u001b[t")]
    [InlineData("\u001b[?18t")]
    [InlineData("\u001b[>18t")]
    public void TitleReportsAndPrivateForms_AreSilent(string input)
    {
        var (_, parser, replies, resizes) = Setup();
        Feed(parser, input);
        Assert.Empty(replies);
        Assert.Empty(resizes);
    }

    [Theory]
    [InlineData("\u001b[8;30;100t", 30, 100)]
    [InlineData("\u001b[8;0;100t", 0, 100)]
    [InlineData("\u001b[8;30;0t", 30, 0)]
    [InlineData("\u001b[8;30t", 30, 0)]
    [InlineData("\u001b[8;5000;5000t", 1000, 1000)]
    [InlineData("\u001b[8;99999999999;7t", 1000, 7)]
    [InlineData("\u001b[30t", 30, 0)]
    [InlineData("\u001b[24t", 24, 0)]
    [InlineData("\u001b[5000t", 1000, 0)]
    public void ResizeRequests_RaiseTheClampedEvent(string input, int rows, int cols)
    {
        var (_, parser, replies, resizes) = Setup();
        Feed(parser, input);
        Assert.Equal(new[] { (rows, cols) }, resizes);
        Assert.Empty(replies);
    }

    [Theory]
    [InlineData("\u001b[8;0;0t")]
    [InlineData("\u001b[8t")]
    [InlineData("\u001b[8;;t")]
    public void EmptyResizeRequests_RaiseNothing(string input)
    {
        var (_, parser, _, resizes) = Setup();
        Feed(parser, input);
        Assert.Empty(resizes);
    }

    [Fact]
    public void ResizeRequest_WithoutSubscriberIsIgnored()
    {
        var adapter = new TerminalAdapter(rows: 2, columns: 8);
        var parser = new BasicAnsiParser { Handler = adapter };
        var replies = new List<string>();
        adapter.ReplyRequested += reply => replies.Add(reply.ToString());
        Feed(parser, "\u001b[8;30;100t");
        Assert.Empty(replies);
        Assert.Equal(2, adapter.Buffer.Rows);
        Assert.Equal(8, adapter.Buffer.Columns);
    }

    [Fact]
    public void ResizeRequest_DoesNotResizeTheBufferItself()
    {
        var (adapter, parser, _, _) = Setup();
        Feed(parser, "\u001b[8;30;100t");
        Assert.Equal(2, adapter.Buffer.Rows);
        Assert.Equal(8, adapter.Buffer.Columns);
    }

    [Fact]
    public void ResizeHandler_MayResizeSynchronouslyAndLaterReportsSeeIt()
    {
        var adapter = new TerminalAdapter(rows: 2, columns: 8);
        var parser = new BasicAnsiParser { Handler = adapter };
        var replies = new List<string>();
        adapter.ReplyRequested += reply => replies.Add(reply.ToString());
        adapter.WindowResizeRequested += (rows, cols) => adapter.ResizeBuffer(rows == 0 ? 2 : rows, cols == 0 ? 8 : cols);
        Feed(parser, "\u001b[8;4;10t\u001b[18t");
        Assert.Equal(new[] { "\u001b[8;4;10t" }, replies);
    }
}

[Collection("Allocation-sensitive tests")]
public sealed class WindowOpsAllocationTests
{
    private static void IgnoreReply(ReadOnlySpan<char> reply) { }

    [Fact]
    public void WindowReportsAndResizeRequestsDoNotAllocate()
    {
        var adapter = new TerminalAdapter(rows: 2, columns: 8);
        adapter.ReplyRequested += IgnoreReply;
        adapter.WindowResizeRequested += static (rows, cols) => { };
        var parser = new BasicAnsiParser { Handler = adapter };
        byte[][] sequences =
        {
            Encoding.UTF8.GetBytes("\u001b[13t"),
            Encoding.UTF8.GetBytes("\u001b[14t"),
            Encoding.UTF8.GetBytes("\u001b[15t"),
            Encoding.UTF8.GetBytes("\u001b[16t"),
            Encoding.UTF8.GetBytes("\u001b[18t"),
            Encoding.UTF8.GetBytes("\u001b[19t"),
            Encoding.UTF8.GetBytes("\u001b[20t"),
            Encoding.UTF8.GetBytes("\u001b[8;30;100t"),
        };
        foreach (byte[] sequence in sequences)
            for (int i = 0; i < 16; i++) parser.Feed(sequence);

        AllocationAssert.NoAllocations(
            () =>
            {
                foreach (byte[] sequence in sequences) parser.Feed(sequence);
            },
            warmupIterations: 0,
            measuredIterationsPerWindow: 128,
            windows: 5);
    }
}
