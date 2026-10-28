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
    [InlineData("\u001b[?15n")]
    [InlineData("\u001b[?25n")]
    [InlineData("\u001b[?26n")]
    [InlineData("\u001b[>6n")]
    public void UnknownOrPrivateStatusCodes_AreSilent(string sequence)
    {
        var (_, parser, replies) = Setup();
        Feed(parser, sequence);
        Assert.Empty(replies);
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
