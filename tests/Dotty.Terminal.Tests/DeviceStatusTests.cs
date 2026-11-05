using System.Collections.Generic;
using System.Text;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Parser;
using Xunit;

namespace Dotty.Terminal.Tests;

/// <summary>DSR / CPR / DECXCPR / private DSR and DECBKM through the real parser.</summary>
public sealed class DeviceStatusTests
{
    private static (TerminalAdapter Adapter, BasicAnsiParser Parser, List<string> Replies) Setup()
    {
        var adapter = new TerminalAdapter(rows: 10, columns: 20);
        var parser = new BasicAnsiParser { Handler = adapter };
        var replies = new List<string>();
        adapter.ReplyRequested += reply => replies.Add(reply.ToString());
        return (adapter, parser, replies);
    }

    private static void Feed(BasicAnsiParser parser, string text) => parser.Feed(Encoding.UTF8.GetBytes(text));

    [Fact]
    public void Cpr_ReportsCursorPosition()
    {
        var (_, parser, replies) = Setup();
        Feed(parser, "\u001b[2;5H\u001b[6n");
        Assert.Equal(new[] { "\u001b[2;5R" }, replies);
    }

    [Fact]
    public void Decxcpr_ReportsPageOne()
    {
        var (_, parser, replies) = Setup();
        Feed(parser, "\u001b[2;5H\u001b[?6n");
        Assert.Equal(new[] { "\u001b[?2;5;1R" }, replies);
    }

    [Fact]
    public void Decxcpr_SplitAcrossFeeds()
    {
        var (_, parser, replies) = Setup();
        Feed(parser, "\u001b[2;5H\u001b[?");
        Feed(parser, "6");
        Assert.Empty(replies);
        Feed(parser, "n");
        Assert.Equal(new[] { "\u001b[?2;5;1R" }, replies);
    }

    [Fact]
    public void OriginMode_ReportsRowRelativeToTopMargin()
    {
        var (_, parser, replies) = Setup();
        Feed(parser, "\u001b[3;6r\u001b[?6h\u001b[2;4H\u001b[6n\u001b[?6n");
        Assert.Equal(new[] { "\u001b[2;4R", "\u001b[?2;4;1R" }, replies);
    }

    [Fact]
    public void Dsr5_ReportsOk()
    {
        var (_, parser, replies) = Setup();
        Feed(parser, "\u001b[5n");
        Assert.Equal(new[] { "\u001b[0n" }, replies);
    }

    [Theory]
    [InlineData("\u001b[0n")]
    [InlineData("\u001b[7n")]
    [InlineData("\u001b[n")]
    [InlineData("\u001b[>6n")]
    public void UnknownOrPrivateStatusCodes_AreSilent(string sequence)
    {
        var (_, parser, replies) = Setup();
        Feed(parser, sequence);
        Assert.Empty(replies);
    }

    [Fact]
    public void Decid_AndPrimaryDaAliasesReplyWithoutPrintingAndPreserveCprOrder()
    {
        var (adapter, parser, replies) = Setup();
        Feed(parser, "\u001b[2;3H\u001b[31mA\u001bZ");
        Assert.Single(replies);

        Feed(parser, "\u001b[6nB\u001b[c");

        Assert.Equal("\u001b[2;4R", replies[1]);
        Assert.Equal(replies[0], replies[2]);
        Assert.Equal('A', (char)adapter.Buffer.GetCell(1, 2).Rune);
        Assert.Equal('B', (char)adapter.Buffer.GetCell(1, 3).Rune);
        Assert.True(adapter.Buffer.GetCell(1, 2).StyleId != 0);
        Assert.Equal(adapter.Buffer.GetCell(1, 2).StyleId, adapter.Buffer.GetCell(1, 3).StyleId);
        Assert.Equal((1, 4), (adapter.Buffer.CursorRow, adapter.Buffer.CursorCol));
    }

    [Fact]
    public void Decid_SplitAcrossFeedsIsConsumedAsARequest()
    {
        var (adapter, parser, replies) = Setup();
        Feed(parser, "\u001b");
        Assert.Empty(replies);
        Feed(parser, "Z\u001b[6n");
        Assert.Equal(2, replies.Count);
        Assert.Equal("\u001b[1;1R", replies[1]);
        Assert.True(adapter.Buffer.GetCell(0, 0).IsEmpty);
        Assert.Equal((0, 0), (adapter.Buffer.CursorRow, adapter.Buffer.CursorCol));
    }

    [Theory]
    [InlineData("\u001b[?1;0c")]
    [InlineData("\u001b[>1;999;0c")]
    [InlineData("\u001b[1c")]
    [InlineData("\u001b[>7c")]
    [InlineData("\u001b[0;0c")]
    [InlineData("\u001b[0:0c")]
    [InlineData("\u001b[0$c")]
    public void DeviceAttributeResponsesAndMalformedRequestsAreSilent(string sequence)
    {
        var (adapter, parser, replies) = Setup();
        Feed(parser, sequence + "\u001b[6nX");

        Assert.Equal(new[] { "\u001b[1;1R" }, replies);
        Assert.Equal('X', (char)adapter.Buffer.GetCell(0, 0).Rune);
        Assert.Equal((0, 1), (adapter.Buffer.CursorRow, adapter.Buffer.CursorCol));
    }

    [Fact]
    public void Da2AcceptsOmittedAndLeadingZeroParameters()
    {
        var (_, parser, replies) = Setup();
        Feed(parser, "\u001b[>c\u001b[>0c\u001b[>000c");

        Assert.Equal(3, replies.Count);
        Assert.Equal(replies[0], replies[1]);
        Assert.Equal(replies[1], replies[2]);
    }

    [Fact]
    public void Decbkm_DefaultsResetAndTracksSetReset()
    {
        var (adapter, parser, replies) = Setup();
        Assert.False(adapter.BackarrowSendsBackspace);
        Feed(parser, "\u001b[?67$p");
        Feed(parser, "\u001b[?67h");
        Assert.True(adapter.BackarrowSendsBackspace);
        Feed(parser, "\u001b[?67$p");
        Feed(parser, "\u001b[?67l");
        Assert.False(adapter.BackarrowSendsBackspace);
        Feed(parser, "\u001b[?67$p");
        Assert.Equal(new[] { "\u001b[?67;2$y", "\u001b[?67;1$y", "\u001b[?67;2$y" }, replies);
    }
}
